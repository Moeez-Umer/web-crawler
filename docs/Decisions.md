# Decisions Log (ADR-Lite)

This file is an **ADR-lite decision log** for the `web-crawler` project.  
Every non-trivial architectural or design choice is recorded here so future contributors (and the original author) understand **what was decided, why, and what was given up**.

> **How to use:**  
> Copy the template below, assign the next `D-NNN` number, fill every field, and append the entry.  
> Status is either `Accepted` (standing decision) or `Revisit` (we know we may change it).

---

## Template

```
### D-XXX: <title>
- Phase:
- Status: Accepted | Revisit
- Context: (what problem)
- Options considered: (2-3)
- Decision & why:
- Trade-off / cost accepted:
- Revisit when:
```

---

## Decisions

---

### D-001: RabbitMQ over Kafka
- Phase: 0 (design)
- Status: Accepted
- Context: We need a message broker to fan out per-page crawl tasks and handle retries/DLQ without writing a lot of custom retry infrastructure.
- Options considered:
  1. **Apache Kafka** — high throughput, log-based, great for event sourcing.
  2. **RabbitMQ** — per-message ack, native dead-letter exchange (DLX), straightforward task-queue semantics.
  3. **Azure Service Bus / AWS SQS** — managed, but cloud-vendor-specific; ruled out for local-first dev.
- Decision & why: RabbitMQ. The workload is a task queue, not a log. Per-message manual ack/nack, native DLX routing, and TTL-based delayed retry queues are first-class features that map directly onto the crawl retry/DLQ requirements. Kafka would require consumer-side offset management and an external retry mechanism.
- Trade-off / cost accepted: RabbitMQ is not ideal for very high throughput replay or audit-log scenarios; ordering guarantees are weaker than Kafka partitions.
- Revisit when: Throughput requirements exceed what a single RabbitMQ node can handle, or we need event-sourcing/replay semantics.

---

### D-002: Raw RabbitMQ.Client over MassTransit
- Phase: 0 (design)
- Status: Accepted
- Context: We need explicit control over ack, nack, retry routing, and DLQ to demonstrate understanding of the AMQP model.
- Options considered:
  1. **MassTransit** — high-level abstraction, built-in retry/redelivery pipelines, less boilerplate.
  2. **Raw `RabbitMQ.Client`** — full control, verbose but transparent.
  3. **EasyNetQ** — middle ground, slightly higher-level than raw client but still exposes AMQP primitives.
- Decision & why: Raw `RabbitMQ.Client`. The spec explicitly requires the ability to explain ack/nack/DLQ mechanics, which MassTransit hides. Using the raw client forces every ack, nack, and routing decision to be explicit in the codebase, making the intent readable.
- Trade-off / cost accepted: More boilerplate (topology declaration, channel management, reconnect handling). We accept this verbosity in exchange for transparency.
- Revisit when: The broker interaction code grows to the point where the boilerplate obscures business logic, or the team grows and needs convention-over-configuration.

---

### D-003: PostgreSQL + EF Core with Raw SQL for Upserts
- Phase: 0 (design)
- Status: Accepted
- Context: We need a relational store for jobs, pages, and edges. Some operations (idempotent upserts) cannot be expressed cleanly in LINQ/EF.
- Options considered:
  1. **EF Core only** — clean, type-safe, but `AddOrUpdate` is not safe under concurrency.
  2. **Dapper only** — full SQL control, less ceremony, but no migration tooling or change tracking.
  3. **EF Core + raw SQL for upserts** — migrations and entity tracking from EF; `INSERT ... ON CONFLICT DO NOTHING` for atomic upserts where needed.
- Decision & why: Hybrid. EF Core owns schema migrations, entity configuration, and simple CRUD. Raw SQL (`ExecuteSqlRawAsync`) is used for `ON CONFLICT` upserts on `crawled_pages`, `page_edges`, and `processed_messages` where atomicity under concurrent inserts is required.
- Trade-off / cost accepted: Two "dialects" (LINQ and SQL) in the data layer. Developers must know when to drop to raw SQL.
- Revisit when: A cleaner EF interceptor or `Merge` extension covers the upsert cases without sacrificing atomicity.

---

### D-004: Per-Page Task Fan-Out Instead of One BFS in a Single Message
- Phase: 0 (design)
- Status: Accepted
- Context: We need to crawl a website breadth-first up to `maxDepth`. Two broad approaches exist for distributing this work.
- Options considered:
  1. **Single BFS in one worker thread** — simple, but blocks the worker for the entire job duration; no parallelism.
  2. **Fan-out: one message per page** — each consumed message processes one page and publishes child messages; natural parallelism across worker replicas.
  3. **Batch fan-out: one message per depth level** — reduces message count but adds complexity (batch ack, partial failure handling).
- Decision & why: Per-page fan-out (option 2). Each `CrawlPageTask` message carries exactly one URL. The worker fetches that page, extracts links, and publishes one new `CrawlPageTask` per unvisited child link within `maxDepth`. This gives horizontal scalability, natural back-pressure via RabbitMQ prefetch, and simple per-message idempotency.
- Trade-off / cost accepted: Higher message volume than a batch approach; RabbitMQ must handle O(pages) messages per job. Acceptable for the scale targeted by this project.
- Revisit when: Message volume per job becomes a bottleneck, or we need strict ordering within a depth level.

---

### D-005: Completion Detection via Atomic `pending_tasks` Counter on Jobs
- Phase: 0 (design)
- Status: Accepted
- Context: We need to know when all pages for a job have been processed so the job can be marked `Completed`. Workers are concurrent and stateless.
- Options considered:
  1. **Polling query** — periodically count outstanding messages; requires RabbitMQ management API access and is eventually consistent.
  2. **Saga/orchestrator** — a separate process tracks state; adds significant complexity.
  3. **Atomic counter on the job row** — `pending_tasks` is incremented on publish and decremented on task completion via `UPDATE ... RETURNING`; when it reaches 0, the job is done.
- Decision & why: Atomic counter (option 3). It is simple, lives in the same PostgreSQL transaction as task processing, and requires no additional infrastructure. The `UPDATE ... RETURNING` pattern ensures the decrement and completion check are atomic under concurrent workers.
- Trade-off / cost accepted: The counter is correct only if every publish is paired with exactly one decrement. A bug that publishes without decrementing (or vice versa) will cause the job to never complete or to complete prematurely. The logic must be tested carefully.
- Revisit when: We adopt a saga framework, or the counter approach proves unreliable under failure scenarios we did not anticipate.

---

### D-006: Idempotency via `processed_messages` + Unique Keys + `ON CONFLICT DO NOTHING` in One Transaction
- Phase: 0 (design)
- Status: Accepted
- Context: RabbitMQ delivers messages at-least-once. A network blip after `ACK` is sent but before the broker records it, or a worker crash mid-processing, can cause the same message to be delivered again. We must not double-insert pages, double-decrement counters, or double-publish children.
- Options considered:
  1. **Application-level check before insert** — read-then-write; not safe under concurrent delivery of the same message to two workers.
  2. **Unique DB constraints + absorb conflict** — idempotent at the DB level but does not prevent double-processing logic (counter decrement, child publish).
  3. **`processed_messages` table + unique constraints + single transaction** — insert `ProcessedMessage(messageId)` first; if it conflicts, the message was already processed — ACK and return. Otherwise, all writes (page upsert, edge upserts, counter decrement, job status update) happen in the same transaction.
- Decision & why: Option 3 — full transaction with `ProcessedMessages` guard. This is the only approach that prevents any double-processing side-effect including counter double-decrement and child double-publish under at-least-once delivery.
- Trade-off / cost accepted: `processed_messages` table grows indefinitely. We accept this for now; a TTL cleanup job is a natural next step.
- Revisit when: The `processed_messages` table becomes a storage concern, or we adopt a framework (e.g., Outbox pattern) that provides built-in idempotency.

---

### D-007: Tree Built in Memory from Flat Pages + Edges Using First-Discovery Parent (BFS)
- Phase: 0 (design)
- Status: Accepted
- Context: The `GET /api/jobs/{id}/tree` endpoint must return a hierarchical tree of crawled pages. The DB stores pages (flat) and edges (parent URL → child URL). A page may be reachable from multiple parents (a cycle or diamond).
- Options considered:
  1. **Recursive CTE in SQL** — elegant but complex; dialect-specific; harder to enforce first-discovery rule.
  2. **Build tree in memory from two queries** — load all pages, load all edges, assemble hierarchy in C#; first-discovery wins (BFS order assigns parent).
  3. **Store parent in page row** — denormalized; first-discovery parent is baked in at write time, but requires careful write ordering.
- Decision & why: In-memory assembly from two queries (option 2). Load all `CrawledPage` rows for the job and all `PageEdge` rows for the job, then run a BFS in C# that assigns each child URL to its first-discovered parent. This guarantees each page appears exactly once, cycles are impossible (a node is only visited once), and the logic is easy to test without a DB.
- Trade-off / cost accepted: Memory usage proportional to pages + edges per job. For `maxPages = 200` this is trivial. For very large jobs it could be a concern.
- Revisit when: `maxPages` is raised significantly and the in-memory assembly becomes a bottleneck, or we need pagination of the tree.

---

### D-008: Strict Host Match; Recurse Only Same-Host; External Links Count in Ratio
- Phase: 0 (design)
- Status: Accepted
- Context: We need a clear policy for what "internal" means when computing `DomainLinkRatio` and when deciding whether to recurse into a linked page.
- Options considered:
  1. **eTLD+1 matching** — `www.example.com` and `example.com` are the same domain; friendlier but requires a public suffix list.
  2. **Strict host match** — `www.example.com != example.com`; simple, deterministic, no external library needed.
  3. **Subdomain-aware** — `blog.example.com` is treated as internal to `example.com`; complex to define.
- Decision & why: Strict host comparison (option 2). `www.example.com` and `example.com` are treated as different hosts. This avoids a public suffix library dependency and makes the crawl boundary completely deterministic. The spec explicitly calls this out as the required behaviour.
- Trade-off / cost accepted: A site that serves the same content on `www.` and the bare domain will not be fully crawled from either entry point. Users who submit `example.com` will not follow links to `www.example.com`, which may feel surprising.
- Revisit when: User feedback shows the strict host policy causes missed crawls in common real-world scenarios.

---

### D-009: Dual-Write (DB Commit + Broker Publish) Accepted as a Known Limitation
- Phase: 0 (design)
- Status: Revisit
- Context: Publishing child crawl tasks to RabbitMQ and committing the DB transaction are two separate I/O operations. If the DB commits but the publish fails (or vice versa), the system is in an inconsistent state.
- Options considered:
  1. **Transactional outbox** — write outbox records to DB in the same transaction; a relay process publishes them to the broker; guarantees exactly-once publish. Correct but adds infrastructure.
  2. **Dual-write with retry on publish failure** — commit DB, attempt publish, retry publish on failure; still not atomic (DB committed, publish might ultimately fail).
  3. **Accept dual-write** — document the limitation; the system is correct in the happy path and under most failure modes; the outbox is the documented next step.
- Decision & why: Accept dual-write (option 3) for the initial implementation. The scope of this project is to demonstrate the crawl architecture, not to build a production-grade outbox. The limitation is documented in the README and here.
- Trade-off / cost accepted: A crash between DB commit and broker publish means child tasks are lost and the job may stall with `pending_tasks > 0` permanently. Recovery requires manual intervention or a background job.
- Revisit when: We move toward a production deployment or the dual-write failure mode is observed in testing.

---

### D-010: EF Migrations Applied on API Startup
- Phase: 0 (design)
- Status: Revisit
- Context: The database schema must be initialized before the API can serve requests. Options range from manual migration steps to automatic application on startup.
- Options considered:
  1. **Manual `dotnet ef database update`** — explicit, safe for production; requires a migration step in the deploy pipeline.
  2. **Init container / migration job in Docker Compose** — separates migration from service startup; cleaner but adds a service entry.
  3. **`context.Database.MigrateAsync()` on API startup** — zero extra steps for local dev and CI; dangerous if multiple API replicas race.
- Decision & why: Automatic migration on API startup (option 3). For local development and the CI environment, this is the simplest path. `MigrateAsync` is idempotent and EF applies a migration lock, so a single-replica startup race is handled. The limitation (not safe for multi-replica production deploys) is noted.
- Trade-off / cost accepted: Running migrations inside the application process means a migration failure will crash the API on startup. Not suitable for blue/green or canary deploys with multiple replicas.
- Revisit when: We move to a multi-replica production deployment or adopt a proper CD pipeline with a dedicated migration step.

