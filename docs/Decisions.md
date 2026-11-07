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

---

### D-011: EF Core Tooling Setup and Startup Project Configuration
- Phase: 1
- Status: Accepted
- Context: `dotnet ef` migration commands require an executable startup project with access to DbContext registration and `Microsoft.EntityFrameworkCore.Design`.
- Options considered:
  1. **Configure a separate CLI migration tool/project** — clean separation, but adds another project to the solution.
  2. **Configure `Crawler.Api` as the startup project for EF migrations** with `Microsoft.EntityFrameworkCore.Design` added — standard ASP.NET Core pattern, minimal ceremony.
- Decision & why: Option 2. `Crawler.Api` already configures PostgreSQL connection strings and DbContext registration for runtime, making it the natural startup project for design-time tooling.
- Trade-off / cost accepted: `Crawler.Api` directly references `Microsoft.EntityFrameworkCore.Design`.
- Revisit when: Architecture changes to separate migrations into a dedicated runner image or CLI container.

---

### D-012: Trailing-Slash Policy for UrlNormalizer — Preserve As-Is
- Phase: 2
- Status: Accepted
- Context: Many web servers distinguish `/page` and `/page/` as different resources. We need a single consistent policy so the same URL is always represented the same way, preventing both missed pages and false duplicate detection.
- Options considered:
  1. **Always add trailing slash** — `/page` → `/page/`. Loses distinction on servers that redirect `/page/` → `/page`.
  2. **Always remove trailing slash** — `/page/` → `/page`. Loses distinction on servers that redirect `/page` → `/page/`; would strip the root URL `/` to empty string (requires special-casing).
  3. **Preserve as-is** — no slash is added or removed. The URL is normalized in all other ways (scheme, host, default ports, fragment) but the trailing slash state from the original href is kept.
- Decision & why: Preserve as-is (option 3). It is the safest choice: it never changes server-intended URL structure, requires no special-casing for the root path, and duplicates are caught by the `ON CONFLICT (job_id, normalized_url)` DB constraint rather than by over-aggressively normalizing. Documented in `UrlNormalizer.cs`.
- Trade-off / cost accepted: A site that publishes both `/page` and `/page/` as separate links may cause two DB rows for what is functionally the same content. Acceptable within our `maxPages` budget.
- Revisit when: We observe widespread false duplicates caused by trailing-slash inconsistency in a real crawl, at which point we can reconsider option 2 with root-path special-casing.

---

### D-013: Duplicate Links Count Multiple Times in DomainLinkRatioCalculator
- Phase: 2
- Status: Accepted
- Context: A page may contain multiple `<a href="...">` tags pointing to the same URL. We must decide whether to count each occurrence or de-duplicate before computing the ratio.
- Options considered:
  1. **De-duplicate links before counting** — ratio reflects unique destinations; a page linking to the same internal URL five times has the same ratio as one that links once.
  2. **Count each occurrence** — ratio reflects the raw HTML structure; more repetitions of an internal link increase the internal score.
- Decision & why: Count each occurrence (option 2). The input to `Calculate` is the list of raw extracted href occurrences. De-duplicating is the caller's concern (the `LinkExtractor` could do it if desired). Counting occurrences gives a more accurate picture of the page's "orientation" — a page with 10 internal links and 1 external link should score differently than one with 1 internal and 1 external. Documented in `DomainLinkRatioCalculator.cs`.
- Trade-off / cost accepted: A page that happens to have many repeated internal anchor tags will score higher than its unique-destination ratio. Acceptable; the ratio is a heuristic, not an exact metric.
- Revisit when: Integration tests reveal that repeated anchor tags cause misleading ratios in practice.

---

### D-014: Retry via Re-Publish to Delayed TTL Queue Instead of NACK Requeue
- Phase: 3
- Status: Accepted
- Context: When handling transient failures (timeouts, 5xx, 429, temporary network/DB issues), consumer must not lock up the queue or spin in a tight failure loop.
- Options considered:
  1. **NACK with requeue=true** — message is re-inserted at the front of `crawl.tasks` immediately. Causes immediate redelivery, hammering failing endpoints and blocking other messages in the queue.
  2. **Thread.Sleep / Task.Delay before NACK** — blocks the consumer thread, stalling processing of other healthy tasks.
  3. **Publish to dead-letter delayed retry queue and ACK original** — original message is acknowledged and removed; a new message with incremented `Attempt` is published to `crawl.retry`. A per-queue TTL delays it, and broker automatically dead-letters it back to `crawl.tasks`.
- Decision & why: Option 3. It provides non-blocking, non-starving delayed redelivery. The consumer thread immediately moves on to the next task while the failed message waits out its delay.
- Trade-off / cost accepted: Higher broker message churn (a retry publishes a new message and acks the old). New message receives a new AMQP delivery tag. Acceptable for our scale.
- Revisit when: Retry requirements require dynamic per-message backoff intervals rather than per-queue TTL, where RabbitMQ delayed exchange plugin or multiple tier queues might be evaluated.

---

### D-016: MaxPages Guard Placement in CrawlTaskProcessor
- Phase: 4
- Status: Accepted
- Context: We need to enforce `MaxPages` to prevent infinite crawls or unbounded database growth when crawling large websites.
- Options considered:
  1. **Publisher-side guard only in API**: Only stops the root task, but does not constrain worker recursive fan-out.
  2. **Worker-side guard in `CrawlTaskProcessor` during child upsert & fan-out**: Before creating child page records and publishing child tasks, check `job.PagesCrawled + job.PendingTasks + newlyInserted.Count >= job.MaxPages`.
  3. **Database constraint or trigger**: Reject page inserts exceeding `MaxPages` at SQL schema level.
- Decision & why: Option 2. Evaluating `MaxPages` before creating child page rows and child task publishing halts expansion cleanly without database exceptions or aborted transactions.
- Trade-off / cost accepted: Snapshot of `job.PagesCrawled` + `job.PendingTasks` under high concurrent worker count may slightly overshoot `MaxPages` by a few tasks due to race conditions.
- Revisit when: Strict hard-cap enforcement requires serializing page insertions via row-level locks on the `crawl_jobs` row (`SELECT ... FOR UPDATE`).

---

### D-017: Worker Health Endpoint via ASP.NET Core Minimal API on Dedicated Port 5001
- Phase: 4
- Status: Accepted
- Context: The worker process is primarily a background messaging consumer, but container orchestration (Docker Compose / Kubernetes) requires an HTTP liveness and readiness probe.
- Options considered:
  1. **File-based health check (`touch /tmp/healthy`)**: Docker-friendly but doesn't expose component health (DB / RabbitMQ status).
  2. **Socket or TCP ping**: Only proves process is alive, not functional.
  3. **Embedded Kestrel HTTP server exposing `/health` on port 5001**: Leverages `Microsoft.Extensions.Diagnostics.HealthChecks` with PostgreSQL check.
- Decision & why: Option 3. ASP.NET Core Web SDK (`Microsoft.NET.Sdk.Web`) running Kestrel on port 5001 provides a standard, probeable HTTP health endpoint without interfering with the API service on port 5000.
- Trade-off / cost accepted: Worker process runs a lightweight web server, consuming marginally more memory than a pure headless Worker SDK process.
- Revisit when: Extreme memory optimization in low-resource environments requires a purely headless worker.

---

### D-018: Dead-Letter Finalization Order and Fallback
- Phase: 4
- Status: Accepted
- Context: When retries are exhausted or a message is poison, sending the task to DLQ without updating PostgreSQL leaves `pending_tasks` un-decremented and the job permanently unfinished.
- Options considered:
  1. **Nack to DLQ first, then finalize DB**: If the worker crashes or DB fails after NACK, the task is already dead-lettered and `pending_tasks` is permanently leaked.
  2. **DB finalization first; if it commits, NACK to DLQ; if DB throws, treat as transient**: Run DB finalization (`OnDeadLetteredAsync`: mark page Failed, decrement `pending_tasks`, finalize job if 0) and commit before sending NACK. If DB throws, do not NACK; republish to `crawl.retry` with unchanged Attempt so the task is not lost and is re-attempted once DB recovers.
  3. **DLQ monitor / sweeper job**: External process polls DLQ and updates database; adds extra moving parts.
- Decision & why: Option 2. Guarantees that no task enters the DLQ while still counted as pending in PostgreSQL. If the database is experiencing an outage or deadlock at the moment of dead-lettering, republishing with the same attempt keeps the task alive until the DB is available to commit the finalization.
- Trade-off / cost accepted: Temporary DB failure during DLQ routing delays DLQ delivery by one retry interval.
- Revisit when: A dedicated transactional outbox or change data capture (CDC) pipeline manages DLQ state transitions.

---

### D-019: Page Discovery Placeholder (`PageStatus.Pending`)
- Phase: 4
- Status: Accepted
- Context: When parent pages discover children, they insert child page rows to reserve URLs and track tree depth. If a task fails or dead-letters, it must not overwrite a previously crawled page with `PageStatus.Failed`.
- Options considered:
  1. **Only insert pages when crawled**: Child URLs only exist as edges until fetched; complicates BFS tree and duplicate tracking.
  2. **Insert child rows as Crawled upfront**: Prematurely marks unvisited pages as crawled.
  3. **Insert child rows with `PageStatus.Pending` placeholder; upgrade on fetch**: Child placeholder is created with `status = 'Pending'`. `RecordPageResultAsync` updates status and stats only `WHERE status = 'Pending'` or on new insert. If a page is already `Crawled`, a subsequent failure (e.g. from a redundant delivery) is ignored.
- Decision & why: Option 3. Maintains the full graph of discovered links while protecting successfully crawled pages from being downgraded to `Failed`.
- Trade-off / cost accepted: Introduces a third `PageStatus` enum value (`Pending`) alongside `Crawled` and `Failed`.
- Revisit when: Tree representation shifts to an alternate graph schema.

---

### D-020: Consumer Sync/Async Boundary (RabbitMQ.Client 6.x)
- Phase: 4
- Status: Accepted
- Context: `RabbitMQ.Client` 6.8 `EventingBasicConsumer.Received` is a synchronous event handler (`EventHandler<BasicDeliverEventArgs>`). Our domain/pipeline logic (`DeliveryProcessor.ProcessAsync`, `ICrawlTaskHandler`) is asynchronous (`Task`-based).
- Options considered:
  1. **`AsyncEventingBasicConsumer` with `DispatchConsumersAsync = true`**: RabbitMQ.Client 6.x supports async consumer, but requires setting `DispatchConsumersAsync = true` on the `ConnectionFactory`. Channels created under this mode must handle async dispatch strictly.
  2. **Single sync-over-async block at the boundary**: Keep `EventingBasicConsumer`, encapsulate all delivery orchestration in `DeliveryProcessor.ProcessAsync`, and call `.GetAwaiter().GetResult()` at exactly one location in `RabbitMqConsumer.OnMessageReceived`. This also pins the lifetime of `ea.Body` memory buffer during processing.
  3. **Upgrade to RabbitMQ.Client 7.x**: 7.x is completely async-first with native `IChannel` and async pipelines, but is a major breaking change across the entire codebase.
- Decision & why: Option 2 for Phase 4. Having a single boundary call in `RabbitMqConsumer` isolates the sync/async impedance mismatch to one line while keeping `DeliveryProcessor` fully asynchronous, testable, and broker-agnostic.
- Trade-off / cost accepted: One thread per consumer prefetch slot blocked during message processing. Documented as a known limitation.
- Revisit when: Migrating to `RabbitMQ.Client` 7.x in a future modernization phase.

---

### D-021: Contract-First API Design & `PageStatus.Pending` Reconciliation in `SPEC.md`
- Phase: 5
- Status: Accepted
- Context: Phase 4 introduced `PageStatus.Pending` (D-019) for child page placeholders, whereas `docs/SPEC.md` originally listed only `Crawled|Failed`. The API and future React frontend need a single authoritative contract.
- Options considered:
  1. **Filter out `Pending` pages in `TreeBuilder`**: Hides discovered-but-unfinished child URLs from the tree response.
  2. **Include `Pending` in `TreeNode.status` (`"Crawled" | "Failed" | "Pending"`), codify `docs/api-contract.md` first, and reconcile `docs/SPEC.md`**: Keeps the API contract, domain model, and UI aligned.
- Decision & why: Option 2. Writing `docs/api-contract.md` before any API code and reconciling `docs/SPEC.md` with a one-line clarification guarantees zero drift between backend serialization and frontend TypeScript types.
- Trade-off / cost accepted: Clients rendering `TreeNode` must handle three page statuses (`Crawled`, `Failed`, `Pending`).
- Revisit when: Never — the contract is now locked in `docs/api-contract.md`.

---

### D-022: `GET /api/jobs/{id}/tree` Returns `404 ProblemDetails` ("tree not available yet") Before Root Page Row Exists
- Phase: 5
- Status: Accepted
- Context: Between `POST /api/jobs` (which inserts `CrawlJob`) and the worker finishing or dead-lettering the depth-0 root page (or when publishing the root task failed), `crawled_pages` has no depth-0 row for that job.
- Options considered:
  1. **Return `200 OK` with a synthetic root `TreeNode`**: Invents a fake `Pending` node in memory even when no page row exists.
  2. **Return `404 Not Found` `ProblemDetails` with `detail: "tree not available yet"`**: Distinguishes a missing job (`"Crawl job '{id}' was not found."`) from a job whose root page has not been recorded yet.
- Decision & why: Option 2. Avoids fabricating state that is not in PostgreSQL and gives clients a clear, explicit RFC 7807 `ProblemDetails` message (`"tree not available yet"`).
- Trade-off / cost accepted: Clients should wait until job status is `Completed` (or inspect `detail`) before requesting `/tree`.
- Revisit when: A future streaming/live-tree view wants partial trees before the root fetch finishes.

---

### D-023: Bounded-Retry EF Core Migrations on API Startup Only
- Phase: 5
- Status: Revisit
- Context: In containerized deployments, even after `pg_isready` passes, PostgreSQL or RabbitMQ may briefly reject the first connection while completing startup initialization.
- Options considered:
  1. **Single-shot `MigrateAsync()` on both API and Worker**: Causes migration lock contention between API and Worker and crashes on transient startup hiccups.
  2. **Bounded retry loop (up to 5 attempts, linear backoff) in API startup only; Worker never runs migrations**: Centralizes schema migration in one service (`Crawler.Api`) and absorbs transient startup races.
- Decision & why: Option 2 (extending D-010). Running `Database.MigrateAsync()` and `RabbitMqTopology.Declare()` with a 5-attempt bounded retry in `Crawler.Api` makes `docker compose up --build` resilient while avoiding worker/API migration races.
- Trade-off / cost accepted: Still subject to D-010's multi-replica API limitation in production.
- Revisit when: Moving to multi-replica API deployments with a dedicated migration init job.

---

### D-024: Optional `POST /api/jobs/{id}/cancel` Endpoint Implemented
- Phase: 5
- Status: Accepted
- Context: `SPEC.md` marks `POST /api/jobs/{id}/cancel` as optional (implement last in Phase 5). The Worker already skips and ACKs tasks for `Canceled` jobs.
- Options considered:
  1. **Cut the cancel endpoint**: Leaves the worker's cancel-skipping branch unreachable via HTTP.
  2. **Implement `POST /api/jobs/{id}/cancel`**: Transitions `Pending` or `Running` jobs to `Canceled` with `CompletedAt = now()`, and returns `409 Conflict` `ProblemDetails` if the job is already in a terminal state (`Completed`, `Failed`, `Canceled`).
- Decision & why: Option 2. The implementation is small, completes the job state machine end-to-end, and is covered by unit tests.
- Trade-off / cost accepted: In-flight tasks already queued in RabbitMQ drain asynchronously as the worker consumes and ACKs them.
- Revisit when: Immediate queue purging per job is needed.

---

### D-025: Configurable Descriptive `User-Agent` Header on `PageFetcher`
- Phase: 5
- Status: Accepted
- Context: `HttpClient` in .NET sends no `User-Agent` header by default. Many CDNs and origin servers (such as Cloudflare in front of `www.iana.org`) block empty `User-Agent` requests with `403 Forbidden`, hiding real status codes like `404 Not Found` and preventing polite bot identification.
- Options considered:
  1. **Leave `User-Agent` empty**: Rejected by CDNs with `403 Forbidden`.
  2. **Spoof a desktop browser `User-Agent`**: Deceptive and violates polite crawling practices.
  3. **Send a configurable descriptive bot `User-Agent` (`Crawler:UserAgent`, default placeholder `WebCrawler/1.0 (+https://your-domain.example/contact; set Crawler:UserAgent)`)**: Identifies the crawler via configuration (`Crawler:UserAgent` / `CRAWLER_USER_AGENT`) and avoids hardcoding a fabricated URL.
- Decision & why: Option 3. Configured on the named `"crawler"` `HttpClient` in `Crawler.Worker/Program.cs` (with a fallback in `PageFetcher` if no `User-Agent` was configured on the `HttpClient`). Operators should override `Crawler:UserAgent` (or `CRAWLER_USER_AGENT` in Docker Compose) with a real contact URL or email before crawling external sites.
- Trade-off / cost accepted: Out-of-the-box config carries an RFC 2606 `.example` placeholder URL until configured by the operator; sites that explicitly block non-browser user agents may still return `403`, which `PageFetcher` classifies as a permanent failure.
- Revisit when: `robots.txt` parsing is added and requires matching a specific bot token.

---

### D-026: Configurable `API_PORT` (Default 5000) and Loopback Port Binding for macOS AirPlay Conflict
- Phase: 5
- Status: Accepted
- Context: On macOS (Monterey and later), `ControlCenter` (AirPlay Receiver) listens on wildcard `*:5000` (`0.0.0.0:5000` and `[::]:5000`). A default Docker port mapping of `"5000:5000"` (`0.0.0.0:5000`) fails to bind (`address already in use`), and binding only IPv4 `"127.0.0.1:5000:5000"` causes `http://localhost:5000` on IPv6 (`[::1]:5000`) to hit `ControlCenter` (`Server: AirTunes`, `403 Forbidden`).
- Options considered:
  1. **Change default API port away from 5000**: Diverges from `docs/SPEC.md` and `docs/api-contract.md` default URL (`http://localhost:5000`).
  2. **Bind both IPv4 and IPv6 loopback (`127.0.0.1:${API_PORT:-5000}:5000` and `[::1]:${API_PORT:-5000}:5000`) with configurable `API_PORT` (default `5000`)**: Allows `http://localhost:5000` to reach the `api` container on macOS over both IPv4 and IPv6 while letting users override `API_PORT` if needed.
- Decision & why: Option 2. Preserves the `5000` default from `SPEC.md`, avoids the macOS `AirPlay Receiver` wildcard listener conflict on loopback, and exposes `API_PORT` for custom host port mapping.
- Trade-off / cost accepted: The API container binds to host loopback (`127.0.0.1` and `::1`) rather than LAN interfaces (`0.0.0.0`) by default.
- Revisit when: Exposing the API container directly on a non-loopback host interface without a reverse proxy.

---

### D-027: Canceled Jobs Retain `Pending` Page Rows and Non-Zero `pending_tasks`
- Phase: 5
- Status: Accepted
- Context: When `POST /api/jobs/{id}/cancel` transitions an active job to `Canceled`, child page placeholder rows (`status = 'Pending'`) already inserted by parent tasks exist in `crawled_pages`, and queued RabbitMQ tasks are skipped (`ACK`ed immediately) by the worker without decrementing `pending_tasks`.
- Options considered:
  1. **Delete `Pending` page rows and zero out `pending_tasks` on cancel**: Loses visibility into how many tasks were queued and which child URLs had already been discovered when the job was canceled.
  2. **Have worker decrement `pending_tasks` as it drains canceled tasks**: Requires a DB write for every skipped message after the job is already in terminal `Canceled` status.
  3. **Keep existing `Pending` page rows and leave `pending_tasks` at its cancellation-time value**: `CancelJobAsync` atomically sets `status = 'Canceled'` and `completed_at = now()`, and the worker skips queued tasks for `Canceled` jobs with a read-only status check.
- Decision & why: Option 3. Keeps cancellation O(1) in PostgreSQL, allows the worker to drain remaining tasks without DB writes, and preserves the exact snapshot of discovered `Pending` URLs and in-flight queue count at cancellation time.
- Trade-off / cost accepted: Canceled jobs retain `Pending` rows in `crawled_pages` and a non-zero `pendingTasks` count in `JobSummary`.
- Revisit when: A future requirement demands purging unvisited child placeholders on cancellation.

---

### D-028: Crawl Tree Can Differ Between Runs When `MaxPages` Cap Is Hit
- Phase: 5
- Status: Accepted
- Context: When crawling a site with more reachable pages than `MaxPages` (default 200), such as `https://books.toscrape.com` at `maxDepth: 2`, `CrawlTaskProcessor` stops admitting new child pages once `pages_crawled + pending_tasks + newlyInserted >= MaxPages` (D-016).
- Options considered:
  1. **Enforce deterministic global BFS ordering across the entire crawl**: Would require strict single-worker serialization and deterministic response latency across all target URLs.
  2. **Accept that the crawled subtree can vary between runs when `MaxPages` truncates the frontier**: Small variations in HTTP response times, retry timing, or worker concurrency change the order in which depth-1 tasks finish and claim the remaining `MaxPages` budget for their depth-2 children.
- Decision & why: Option 2. Asynchronous queue-driven crawling is naturally non-deterministic in completion order; any run still strictly respects `maxDepth` and `MaxPages` while building a valid first-discovery tree for the pages visited in that run.
- Trade-off / cost accepted: Two crawls of the same large site that hit `MaxPages` may include different subsets of deeper child pages and different first-discovery parent assignments.
- Revisit when: Deterministic crawl replay is required.

---

### D-029: Single Plain CSS Stylesheet with CSS Custom Properties (No UI Component Library)
- Phase: 5.5
- Status: Accepted
- Context: The React frontend needs styling for forms, badges, tables, progress bars, skeletons, and the recursive page tree while keeping the dependency footprint small and prioritizing clarity over visual complexity.
- Options considered:
  1. **Component library (MUI / Chakra / Ant Design)**: Ships pre-built tree and table widgets, but adds heavy bundle weight, custom theming layers, and opaque DOM structures.
  2. **Utility CSS framework (Tailwind CSS)**: Fast authoring, but adds PostCSS/Tailwind build configuration and verbose class strings across recursive tree markup.
  3. **Plain CSS in a single `frontend/src/index.css` with CSS custom properties (`:root` variables)**: Zero runtime or build dependencies, explicit semantic class names, and dynamic state (such as Domain Link Ratio bar width) passed cleanly via inline CSS custom properties (`--ratio-width`).
- Decision & why: Option 3. Keeps the frontend self-contained, transparent, and easy to inspect while enforcing consistent colors, spacing, and focus rings via `:root` variables.
- Trade-off / cost accepted: All layout, badge, skeleton, and tree styles are maintained by hand in `index.css`.
- Revisit when: The UI grows to dozens of screens requiring a shared multi-package design system.

---

### D-030: TanStack Query Polling with Terminal-State Cutoff and 404-Tolerant Tree Query
- Phase: 5.5
- Status: Accepted
- Context: `JobDetailsPage` must poll `GET /api/jobs/:id` every 2 seconds while a job is `Pending` or `Running`, stop automatically at any terminal state (`Completed`, `Failed`, `Canceled`), and then fetch `GET /api/jobs/:id/tree` — which may return `404` (`"tree not available yet"`, D-022) if no root page row was recorded.
- Options considered:
  1. **Manual `setInterval` + `useEffect` `fetch` state machine**: Error-prone cleanup, manual race-condition guards on route changes, and manual retry logic.
  2. **TanStack Query `useQuery` with state-driven `refetchInterval` and a dependent `useJobTree` hook**: `useJob` computes `refetchInterval` from `query.state.data?.status` (returning `false` for `Completed`, `Failed`, `Canceled`, or `404` errors), and `useJobTree` enables once `isTerminalJobStatus(job.status)` is true while catching `ApiError(404)` and returning `null`.
- Decision & why: Option 2. Eliminates manual timer management, exposes `isFetching && !isPending` for the subtle `"Updating..."` indicator during background polls, and converts a missing tree `404` into a neutral empty state (`null`) without triggering error banners.
- Trade-off / cost accepted: Adds `@tanstack/react-query` as a runtime dependency.
- Revisit when: Server-Sent Events (SSE) or WebSockets replace HTTP polling for real-time job progress.

---

### D-031: Default Tree Expansion Capped at Depth 1 with WAI-ARIA Treeview Keyboard Navigation
- Phase: 5.5
- Status: Accepted
- Context: A crawl job can return up to `MaxPages` (200) nodes in `GET /api/jobs/:id/tree`. Rendering all depths expanded by default creates a tall, hard-to-scan list, while collapsing the root node hides immediate child links.
- Options considered:
  1. **Expand all nodes by default**: Immediately overwhelms the viewport on 200-page crawls.
  2. **Collapse all nodes including root**: Requires an extra click before seeing any discovered links.
  3. **Expand only the depth-0 root by default (`expandedUrls = new Set([root.url])`), leaving depth-1 nodes visible and depth ≥ 2 collapsed**: Shows the root page and its immediate depth-1 children immediately while keeping deeper levels collapsed until the user expands a node, clicks `"Expand all"`, or enters a URL filter query.
- Decision & why: Option 3, paired with WAI-ARIA `role="tree"` / `role="treeitem"`, `aria-expanded`, and roving keyboard handlers (`ArrowDown`, `ArrowUp`, `ArrowRight`, `ArrowLeft`, `Home`, `End`, `Enter`, `Space`).
- Trade-off / cost accepted: Users must click a depth-1 chevron or `"Expand all"` to inspect depth-2+ nodes when no URL filter is active.
- Revisit when: Tree sizes exceed thousands of nodes and require virtualized tree rendering.

---

### D-032: Optional Docker Compose Frontend Service Behind `frontend` Profile
- Phase: 5.5
- Status: Accepted
- Context: During normal development, engineers run the Vite dev server (`npm run dev` on port `5173` proxying `/api` to `http://127.0.0.1:5000`), and plain `docker compose up` should only start backend infrastructure (`postgres`, `rabbitmq`, `api`, `worker`).
- Options considered:
  1. **Always start `frontend` in `docker compose up`**: Occupies host port `5173` by default, conflicting with the local Vite dev server.
  2. **Omit `frontend` from `docker-compose.yml` entirely**: Provides no containerized way to run the full stack with nginx.
  3. **Define `frontend` in `docker-compose.yml` under `profiles: ["frontend"]` using a multi-stage Node + nginx `Dockerfile`**: Plain `docker compose up` ignores `frontend`, leaving port `5173` free for Vite, while `docker compose --profile frontend up --build` builds the static bundle and serves it via nginx (proxying `/api/` to `http://api:5000/api/`).
- Decision & why: Option 3. Supports both the local Vite workflow and containerized full-stack verification without port conflicts.
- Trade-off / cost accepted: Starting the containerized UI requires passing `--profile frontend`.
- Revisit when: A single unified production deployment manifest is introduced.

---

### D-033: Mapping Proxy/Gateway Errors and macOS `AirTunes` Port-5000 Fallback to `"Can't reach the server"`
- Phase: 5.5
- Status: Accepted
- Context: On macOS, `ControlCenter` (AirPlay Receiver) listens on wildcard `*:5000` (`0.0.0.0:5000` and `[::]:5000`). While the Docker `api` container is running, its specific loopback bind (`127.0.0.1:5000` / `[::1]:5000`, D-026) takes precedence. However, when the `api` container is stopped (`docker compose stop api`), Docker unbinds `127.0.0.1:5000` and connections to `http://127.0.0.1:5000` fall back to `ControlCenter`, which returns `HTTP/1.1 403 Forbidden` with `Server: AirTunes/...` instead of `ECONNREFUSED`. Additionally, when AirPlay is disabled and nothing listens on `:5000`, Vite's default `http-proxy` returns `500` on `ECONNREFUSED`.
- Options considered:
  1. **Leave Vite proxy defaults and surface raw HTTP status strings (`Request failed with status 403` / `500`)**: Confuses users with a misleading `403` from macOS `ControlCenter` when the backend container is actually stopped.
  2. **Convert proxy connection errors (`ECONNREFUSED`) and `Server: AirTunes` responses to `502 Bad Gateway` in `vite.config.ts`, and map network errors (`status = 0`) plus `502`/`503`/`504` to `"Can't reach the server"` in `ApiError`**: Aligns Vite's dev proxy with nginx (`502 Bad Gateway`) when the upstream API is down, neutralizes the macOS AirPlay Receiver `403` fallback when the `api` container stops, and presents a clear `"Can't reach the server"` message with a `Retry` button.
- Decision & why: Option 2. Gives users an accurate, actionable `"Can't reach the server"` message across network drops, gateway failures (`502`/`503`/`504`), and stopped local containers on macOS, while still preserving `error.problemDetails.detail` on `ApiError.problemDetails` for `POST /api/jobs` `503` responses.
- Trade-off / cost accepted: Custom `configure` hook in `frontend/vite.config.ts` inspects the upstream `Server` header for `"AirTunes"`.
- Revisit when: Default API port is moved off `5000`.

---

### D-034: Schema-Per-Test-Class Isolation Against Real PostgreSQL
- Phase: 6
- Status: Accepted
- Context: Integration tests for `CrawlTaskProcessor` must verify atomic SQL semantics: `ON CONFLICT DO NOTHING` on `processed_messages` and `page_edges`, `ON CONFLICT (job_id, normalized_url) DO UPDATE ... WHERE status='Pending'` on `crawled_pages`, and atomic `pending_tasks` counter decrements with row-level locks under concurrent delivery. EF Core's InMemory provider and SQLite cannot execute PostgreSQL dialect raw SQL or provide PG locking semantics.
- Options considered:
  1. **In-memory provider / mocks only**: Fast, but completely incapable of executing raw SQL upserts or proving true database idempotency.
  2. **Testcontainers PostgreSQL**: Automatically boots a dedicated container per run, but requires Docker socket access, adds cold-start container spin-up overhead (~5-10s), and conflicts if Docker-in-Docker is restricted.
  3. **Real PostgreSQL with unique schema per test class**: Uses the PostgreSQL instance already running via Docker Compose (`localhost:5432`). Each test class creates a dedicated schema (`test_{guid}`), runs EF migrations within that schema, and drops the schema on teardown. If Postgres is unreachable, tests skip gracefully with a clear message.
- Decision & why: Option 3. Gives 100% genuine PostgreSQL behavior with native `ON CONFLICT` execution and concurrency guarantees while keeping test runs sub-second (no container boot penalty) and zero new test dependencies.
- Trade-off / cost accepted: Requires the local `postgres` container from `docker-compose.yml` to be running on `localhost:5432` (or set via `CRAWLER_TEST_DB`). If not running, the integration suite skips.
- Revisit when: CI environment prefers fully dynamic throwaway database containers via Testcontainers.

---

### D-035: In-Process `HttpMessageHandler` and Memory Broker Pump for Integration Testing
- Phase: 6
- Status: Accepted
- Context: We need to test the real `CrawlTaskProcessor`, `LinkExtractor`, and `PageFetcher` pipeline across diverse network and delivery scenarios (cycles, limits, duplicates, out-of-order, concurrency, dead-letter, base href) deterministically without external web dependencies or live RabbitMQ cluster churn.
- Options considered:
  1. **Spin up a local Kestrel HTTP server and live RabbitMQ broker**: Heavyweight, introduces socket port conflicts, flaky timing for retry TTLs and DLQ routing.
  2. **In-process fake `HttpMessageHandler` and an in-memory `BrokerPump`**: `FakeHttpHandler` serves local disk HTML fixtures (`index.html`, `about.html`, `contact.html`, `base-href.html`, `transient-parent.html`) with deterministic HTTP response codes (404, 500, `image/png`). `InMemoryPublisher` collects published `CrawlPageTask` messages, and `BrokerPump` drives them into the processor with selectable dispatch policies (FIFO, reverse, N-times replay, concurrent pairs).
- Decision & why: Option 2. Executes the full business logic, HTML parsing, link extraction, Domain Link Ratio computation, idempotency guards, and completion counters synchronously and deterministically in under 2 seconds.
- Trade-off / cost accepted: Does not test raw AMQP protocol byte framing; AMQP topology and consumer behaviors are already verified in Phase 3 and end-to-end Docker Compose.
- Revisit when: End-to-end multi-process testing across real network boundaries is performed in Phase 7.

---

### D-036: GitHub Actions PostgreSQL 16 Service Container for Real Integration Tests in CI
- Phase: 7
- Status: Accepted
- Context: `CrawlIntegrationTests` gracefully skip if PostgreSQL is unreachable (D-034). If GitHub Actions ran `dotnet test` without a database service, CI would silently skip all 11 database concurrency/idempotency integration scenarios.
- Options considered:
  1. **Run unit tests only in CI**: Fast, but leaves SQL upserts, migrations, and row-level locking untested on push/PR.
  2. **Provision a `postgres:16` service container in `.github/workflows/ci.yml` with `pg_isready` healthcheck and wire `CRAWLER_TEST_DB` via workflow environment variables**: Ensures `PostgresFixture` connects and executes all 11 integration tests alongside the 125 unit tests on every push and pull request, while a parallel `frontend` job runs Node 20 `npm ci`, `npm run lint`, and `npm run build`.
- Decision & why: Option 2. Gives full unit + real PostgreSQL integration test coverage and frontend static verification on every commit.
- Trade-off / cost accepted: Adds ~5–10 seconds to CI job startup while the `postgres:16` service container initializes.
- Revisit when: End-to-end Docker Compose smoke tests are added as a separate CI job.

---

### D-037: Root `.env.example` and `.gitignore` Hygiene for Clean-Clone Reproducibility
- Phase: 7
- Status: Accepted
- Context: `docker-compose.yml` parameterizes credentials, ports (`API_PORT`, `FRONTEND_PORT`), worker tuning (`CRAWLER_MAX_PAGES`, `CRAWLER_MAX_ATTEMPTS`, `CRAWLER_RETRY_DELAY_MS`, `CRAWLER_PREFETCH_COUNT`), and `CRAWLER_USER_AGENT` via environment variables with safe defaults, while local overrides live in `.env`.
- Options considered:
  1. **Rely solely on inline `${VAR:-default}` syntax in `docker-compose.yml`**: Works out of the box, but reviewers have to read `docker-compose.yml` to discover tunable variables.
  2. **Commit `.env.example` documenting all compose and test variables while ignoring `.env`, `.env.local`, `.env.*.local`, and `dist/` in root `.gitignore`**: Keeps `docker compose up --build` zero-config out of the box while giving operators a self-documented template for local overrides without risking accidental secret/artifact commits.
- Decision & why: Option 2. Improves reviewer ergonomics and guarantees clean archives/clones.
- Trade-off / cost accepted: New environment variables added to `docker-compose.yml` must be kept in sync with `.env.example`.
- Revisit when: Configuration moves to external secret managers in production.

---

### D-038: Explicit MSBuild `Link`/`TargetPath` for Test HTML Fixtures Across macOS `/tmp` Symlinks
- Phase: 7
- Status: Accepted
- Context: On macOS, `/tmp` is a symbolic link to `/private/tmp`. When `dotnet test /tmp/<clone>/web-crawler.sln` is invoked via the symlinked `/tmp` path, MSBuild's glob evaluation on `<Content Include="Fixtures\**\*">` resolves the real path `/private/tmp/.../Fixtures/index.html` while `$(MSBuildProjectDirectory)` remains `/tmp/...`. Because the path prefixes differ, MSBuild's `AssignTargetPath` treats the matched files as outside the project directory and strips the `Fixtures/` subfolder, copying `index.html` flat into `bin/Debug/net8.0/index.html` instead of `bin/Debug/net8.0/Fixtures/index.html` and causing `DirectoryNotFoundException` in `FakeHttpHandler`.
- Options considered:
  1. **Require callers to resolve symlinks (`cd /private/tmp/...`) before running `dotnet test`**: Fragile and surprising when cloning into `/tmp` on macOS.
  2. **Add explicit `<Link>Fixtures\%(RecursiveDir)%(Filename)%(Extension)</Link>` and `<TargetPath>Fixtures\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>` (after removing default SDK `Content`/`None` matches for `Fixtures\**\*`) in `Crawler.Worker.Tests.csproj`**: Forces MSBuild to preserve the `Fixtures/` output subdirectory regardless of whether the solution path traverses a symlink.
- Decision & why: Option 2. Fixes the MSBuild packaging edge case so `dotnet test` succeeds identically whether invoked from `/tmp/...`, `/private/tmp/...`, or the working directory.
- Trade-off / cost accepted: Four extra lines of MSBuild metadata in `tests/Crawler.Worker.Tests/Crawler.Worker.Tests.csproj`.
- Revisit when: Never — deterministic output path metadata is standard MSBuild practice.











