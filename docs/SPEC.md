You are a senior .NET engineer. Build a job-based web crawler system in a monorepo called "web-crawler". Work in PHASES. After each phase: build, run tests, summarize what you did, and STOP and wait for me to say "continue". Do not start the next phase on your own. Before writing code, produce a short implementation plan and the exact folder tree, and wait for my approval.

## What the system does
A user submits a website URL (+ optional maxDepth, default 2). The Crawl API creates a job, returns a jobId immediately, and publishes a crawl task to RabbitMQ. A separate Crawl Worker consumes tasks, fetches HTML pages, extracts links, computes a Domain Link Ratio per page, stores pages and edges in PostgreSQL, and fans out child tasks until maxDepth or maxPages is reached. A React UI polls the API for status and shows a tree of pages.

## Tech stack (do not substitute)
- .NET 8, ASP.NET Core Web API (controllers), Worker as a Generic Host with BackgroundService
- RabbitMQ using the official RabbitMQ.Client (no MassTransit; I need to explain ack/nack/DLQ myself)
- PostgreSQL with EF Core + Npgsql; use raw SQL (ON CONFLICT) where EF cannot express upserts
- HtmlAgilityPack for parsing
- Microsoft.Extensions.Http.Resilience (or Polly) for HTTP timeout/retry
- Serilog with JSON console output, enriched with jobId and correlationId
- xUnit + FluentAssertions for tests
- Docker + docker-compose

## Folder structure (create exactly this)
web-crawler/
  docker-compose.yml
  README.md
  .gitignore
  .github/workflows/ci.yml
  src/
    Crawler.Domain/          (entities, enums, UrlNormalizer, DomainLinkRatioCalculator; ZERO external dependencies)
    Crawler.Contracts/       (message schemas: CrawlPageTask)
    Crawler.Infrastructure/  (CrawlDbContext, EF configurations, migrations, RabbitMQ topology/publisher/consumer, logging enrichers)
    Crawler.Api/             (Controllers/JobsController, Dtos, Services/JobService, Program.cs, Dockerfile)
    Crawler.Worker/          (Consumers/CrawlTaskConsumer, Services/PageFetcher, LinkExtractor, CrawlTaskProcessor, Program.cs, Dockerfile)
  tests/
    Crawler.Domain.Tests/    (UrlNormalizerTests, DomainLinkRatioTests)
    Crawler.Worker.Tests/    (Fixtures/*.html, CrawlIntegrationTests)
  frontend/                  (leave empty; I will add the React app)
Dependency rule: Domain depends on nothing. Infrastructure depends on Domain + Contracts. Api and Worker depend on Infrastructure.

## Domain model
CrawlJob: Id (Guid), Url, MaxDepth, MaxPages, Status (Pending|Running|Completed|Failed|Canceled), CreatedAt, StartedAt, CompletedAt, FailureReason, PendingTasks (int), PagesCrawled (int)
CrawledPage: Id, JobId, NormalizedUrl, Depth, DomainLinkRatio, TotalLinks, InternalLinks, Status (Crawled|Failed), Error, CrawledAt
PageEdge: Id, JobId, ParentPageId, ChildUrl
ProcessedMessage: MessageId (PK), ProcessedAt

Constraints and indexes:
- UNIQUE (JobId, NormalizedUrl) on pages
- UNIQUE (JobId, ParentPageId, ChildUrl) on page_edges
- Index jobs(created_at DESC) for history pagination
- Index pages(job_id), page_edges(job_id, parent_page_id)
Store enums as strings.

## Message contract (Crawler.Contracts)
CrawlPageTask { messageId: Guid, jobId: Guid, url: string, parentUrl: string?, depth: int, maxDepth: int, rootHost: string, attempt: int }

## Business rules
UrlNormalizer (pure, static, heavily unit-tested):
- Resolve relative URLs against the page's base URL
- Strip the #fragment
- Lowercase scheme and host, remove default ports (80/443), resolve ./ and ../
- Reject non-http(s) schemes (mailto:, tel:, javascript:, data:, ftp:) by returning null
- Pick ONE trailing-slash policy and document it in the code and README
- Strict host comparison: www.example.com != example.com

DomainLinkRatio = internalLinks / totalValidOutgoingLinks, where internal means host equals the host of the job's starting URL. Ignore invalid/non-http(s) links. Zero links => ratio 0. Never divide by zero.

Crawl rules:
- Only parse responses with Content-Type text/html; cap body size (e.g. 2 MB)
- Only RECURSE into same-host links; external links still count toward the ratio
- Do not reprocess the same normalized URL within a job
- Respect MaxPages (default 200, configurable): stop publishing new tasks once reached
- HTTP: 10s timeout, 3 retries with exponential backoff on transient errors only

## Event-driven design (this is graded heavily)
Topology:
- exchange crawl.exchange -> queue crawl.tasks (x-dead-letter-exchange = crawl.dlx)
- exchange crawl.dlx -> queue crawl.tasks.dlq
- Delayed retry: queue crawl.retry with a per-queue TTL that dead-letters back to crawl.tasks. Increment `attempt` when re-publishing. Max attempts = 5, then route to DLQ.
- Manual acks, prefetch count configurable, durable queues, persistent messages.

Failure classification:
- TRANSIENT (retry via retry queue): timeout, HTTP 5xx, 429, network errors, DB deadlock/transient DB errors
- PERMANENT page-level (record page as Failed, ACK, job continues; NOT a DLQ case): 404, 403, non-HTML content, invalid URL
- POISON (nack requeue=false -> DLQ): deserialization failure, invalid message, attempts exhausted
- If the ROOT page (depth 0) fails permanently => job status Failed with reason. If only child pages fail => job still Completed.

Idempotency (at-least-once delivery assumed):
1. Inside ONE DB transaction: check/insert ProcessedMessages(messageId); if it already exists, ACK and skip
2. Page upsert with INSERT ... ON CONFLICT (job_id, normalized_url) DO NOTHING
3. Edge upsert with ON CONFLICT DO NOTHING
4. Only publish child tasks for pages whose insert actually created a NEW row
5. Update counters in the same transaction

Completion detection:
- PendingTasks is incremented when a task is published (API increments by 1 when creating the root task) and decremented atomically when a task finishes (success, permanent page failure, or sent to DLQ), via a single atomic UPDATE ... RETURNING.
- When PendingTasks reaches 0, set the job to Completed (or Failed if root failed) and set CompletedAt.
- First task consumed moves job Pending -> Running and sets StartedAt.
- Known limitation: DB commit and broker publish are not atomic (dual write). Document it and mention the transactional outbox as the next step.

## API (Crawler.Api)
- POST /api/jobs  body { url, maxDepth? } -> 201 { jobId }. Validate absolute http/https URL; clamp maxDepth to 0..5 (default 2)
- GET /api/jobs/{id} -> JobSummary { jobId, url, maxDepth, status, createdAt, startedAt, completedAt, failureReason, pagesCrawled, pendingTasks }, 404 if missing
- GET /api/jobs/{id}/tree -> hierarchical TreeNode { url, depth, domainLinkRatio, totalLinks, internalLinks, status, error, children[] }. Build the tree in memory from two queries (pages + edges). Use a "first discovery parent" rule (BFS order) so each page appears once and cycles are impossible.
- GET /api/jobs?page=1&pageSize=20 -> { items, page, pageSize, totalCount }, newest first, pageSize capped at 100
- POST /api/jobs/{id}/cancel (optional; implement last): set Canceled, the worker skips tasks for canceled jobs and ACKs them
- GET /health (liveness + readiness checking DB and RabbitMQ)
- JSON camelCase, enums serialized as strings (JsonStringEnumConverter)
- CORS allowing http://localhost:5173
- ProblemDetails for errors, global exception handler
- Apply EF migrations on API startup (note it as a limitation)

## Worker (Crawler.Worker)
- Exposes a minimal HTTP /health endpoint (Kestrel on a separate port) in addition to consuming
- Graceful shutdown: stop consuming, finish in-flight message
- Small methods, CrawlTaskProcessor orchestrates one task; PageFetcher and LinkExtractor are separate and behind interfaces so they can be faked in tests

## Observability
- Serilog JSON logs; every log inside task processing has jobId, messageId, correlationId (use jobId as correlationId if none), url, depth, attempt
- Log every retry, DLQ routing, and job state transition

## Tests
- Unit: UrlNormalizer (relative paths, ../, fragments, default ports, mixed-case host, mailto/tel/javascript, protocol-relative //host/path, query strings, empty href)
- Unit: DomainLinkRatioCalculator (zero links, all internal, all external, mixed, ignored schemes)
- Integration-style: use local HTML fixtures (index.html linking to about.html, contact.html, an external link, a mailto link, a duplicate link, and a fragment link) served by an in-process test HTTP server or a fake HttpMessageHandler; run CrawlTaskProcessor with an in-memory/test DB and a fake publisher; assert pages, edges, ratios, no duplicates, and that processing the SAME message twice creates no duplicate rows and does not double-decrement the counter.

## Docker
docker-compose.yml with: postgres (healthcheck), rabbitmq:3-management (healthcheck), api, worker. Use depends_on with condition: service_healthy. Expose API on 5000, RabbitMQ management on 15672. Environment variables for all connection strings and limits (MaxPages, retry settings). Multi-stage Dockerfiles for api and worker.

## CI
GitHub Actions: restore, build, test on push/PR.

## README (write at the end)
Sections: overview + architecture diagram (ASCII or mermaid), how to run, message schema, idempotency strategy, retry policy (transient vs permanent), DLQ handling, SQL model + index rationale, key architectural choices, known limitations, and the three required sections: "What I implemented first and why", "What I cut and why", "What I'd do next".

## Phases
Phase 0: plan + folder tree (stop for approval)
Phase 1: solution scaffold, projects, references, docker-compose with postgres + rabbitmq, entities + EF config + initial migration
Phase 2: Domain logic (UrlNormalizer, DomainLinkRatioCalculator) + unit tests. All tests must pass.
Phase 3: RabbitMQ topology, publisher, consumer, retry + DLQ handling
Phase 4: Worker (PageFetcher, LinkExtractor, CrawlTaskProcessor), idempotency, completion counter
Phase 5: API endpoints, DTOs, tree builder, health checks, Serilog, CORS, Dockerfiles
Phase 6: integration test with HTML fixtures, including the duplicate-delivery test
Phase 7: README + CI. Then run `docker compose up --build` and verify end to end with curl against a real site.

## Rules for you
- Do not invent extra features. If something is ambiguous, pick the simplest option and note it in a "DECISIONS.md" file.
- Keep classes small, use meaningful names, handle errors explicitly. No empty catch blocks.
- Never hardcode secrets; use configuration/environment variables.
- Do not mark a phase done unless it builds and its tests pass. Show me the command output.
- Add short comments only where the WHY is not obvious (idempotency, completion counter, tree building).