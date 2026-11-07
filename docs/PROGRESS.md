# Progress Tracker — Web Crawler

> This file tracks phase completion against the SPEC.  
> Update **Status** when a phase ships, and move resolved items out of **Open Issues**.

---

## Status Key

| Symbol | Meaning |
|---|---|
| ⬜ | Not started |
| 🔄 | In progress |
| ✅ | Done (builds + tests pass) |
| ❌ | Blocked |

---

## Phase Checklist

### Phase 0 — Plan + Folder Tree
**Status:** ✅ Approved

| Deliverable | Done? |
|---|---|
| Implementation plan written | ✅ |
| Exact folder tree produced | ✅ |
| `docs/Decisions.md` created (D-001 → D-010) | ✅ |
| `docs/Flow.md` created (target design) | ✅ |
| `docs/PROGRESS.md` created (this file) | ✅ |
| User approval received before code written | ✅ |

**Open issues:** —

**Next step:** Begin Phase 1 on user confirmation.

---

### Phase 1 — Solution Scaffold + Docker Compose + Entities + EF Migration
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| `web-crawler.sln` created with all projects | ✅ |
| `Crawler.Domain` — entities, enums (zero external deps) | ✅ |
| `Crawler.Contracts` — `CrawlPageTask` message schema | ✅ |
| `Crawler.Infrastructure` — `CrawlDbContext`, EF entity configs | ✅ |
| `Crawler.Api` — project shell, `Program.cs` wired up | ✅ |
| `Crawler.Worker` — project shell, `Program.cs` wired up | ✅ |
| `tests/Crawler.Domain.Tests` — project shell | ✅ |
| `tests/Crawler.Worker.Tests` — project shell | ✅ |
| `docker-compose.yml` — postgres + rabbitmq with healthchecks | ✅ |
| Initial EF Core migration generated and applied | ✅ |
| All DB constraints and indexes in migration | ✅ |
| `dotnet build` passes with zero warnings | ✅ |

**Open issues:** —

**Next step:** Await "continue" from user, then begin Phase 2 (Domain Logic + Unit Tests).

---

### Phase 2 — Domain Logic + Unit Tests
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| `UrlNormalizer` implemented (pure static, no external deps) | ✅ |
| `UrlNormalizer` trailing-slash policy chosen and documented | ✅ (preserve as-is — D-012) |
| `DomainLinkRatioCalculator` implemented | ✅ |
| Unit tests: `UrlNormalizerTests` — all edge cases from SPEC | ✅ (38 tests) |
| Unit tests: `DomainLinkRatioCalculatorTests` — zero links, all-internal, all-external, mixed | ✅ (10 tests) |
| `dotnet test` passes with zero failures | ✅ (48 passed, 0 failed) |

**Open issues:** —

**Next step:** Await "continue" from user, then begin Phase 3 (RabbitMQ Topology + Publisher + Consumer + Retry + DLQ).

---

### Phase 3 — RabbitMQ Topology + Publisher + Consumer + Retry + DLQ
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| `RabbitMqTopology` — declare exchanges and queues on startup | ✅ |
| `crawl.exchange` → `crawl.tasks` (DLX = `crawl.dlx`) | ✅ |
| `crawl.retry` (TTL → DLX → `crawl.exchange` → `crawl.tasks`) | ✅ |
| `crawl.dlx` → `crawl.tasks.dlq` | ✅ |
| Durable queues, persistent messages, manual acks | ✅ |
| `RabbitMqPublisher` — serialise, publisher confirms, headers | ✅ |
| `RabbitMqConsumer` — `BackgroundService`, manual acks, prefetch | ✅ |
| Retry logic: transient error → publish to `crawl.retry` with `attempt+1` | ✅ |
| Exhausted retries (attempt ≥ MaxAttempts) → NACK `requeue=false` → DLQ | ✅ |
| Poison message (deserialization failure) → NACK `requeue=false` → DLQ | ✅ |
| Graceful shutdown: drain in-flight, stop consuming | ✅ |
| Unit tests: `RetryPolicyTests` (pure logic) | ✅ (11 tests) |

**Open issues:** —

**Next step:** Await "continue" from user, then begin Phase 4 (Worker: PageFetcher, LinkExtractor, CrawlTaskProcessor, Idempotency, Completion Counter).

---

### Phase 4 — Worker: PageFetcher + LinkExtractor + CrawlTaskProcessor + Idempotency + Completion Counter
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| `IPageFetcher` interface + `PageFetcher` impl (HtmlAgilityPack, Polly, 2 MB cap) | ✅ |
| `ILinkExtractor` interface + `LinkExtractor` impl | ✅ |
| `CrawlTaskProcessor` — orchestrates one task end-to-end | ✅ |
| Idempotency: `ProcessedMessages` guard in transaction | ✅ |
| Page upsert `ON CONFLICT DO NOTHING`, capture rows-affected | ✅ |
| Edge upsert `ON CONFLICT DO NOTHING` | ✅ |
| Fan-out: publish child tasks only for newly inserted pages | ✅ |
| `pending_tasks` counter: atomic `UPDATE ... RETURNING` | ✅ |
| Completion detection: if `RETURNING = 0`, mark job Completed/Failed | ✅ |
| Permanent page failure: record page as Failed, ACK, decrement counter | ✅ |
| DLQ finalization: `OnDeadLetteredAsync` decrements `pending_tasks`, marks page Failed (D-018) | ✅ |
| Root page permanent failure: set job status to Failed | ✅ |
| Canceled job: ACK and skip | ✅ |
| Worker `/health` endpoint (Kestrel on separate port 5001) | ✅ |
| Serilog enrichment: `jobId`, `messageId`, `correlationId`, `url`, `depth`, `attempt` | ✅ |

**Open issues:** — (Resolved: MaxPages guard placed in `CrawlTaskProcessor` [D-016]; Kestrel health endpoint configured on port 5001 [D-017]).

**Next step:** Await "continue" from user, then begin Phase 5 (API Endpoints + DTOs + Tree Builder + Health + Serilog + CORS + Dockerfiles).

---

### Phase 5 — API Endpoints + DTOs + Tree Builder + Health + Serilog + CORS + Dockerfiles
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| `docs/api-contract.md` created as single source of truth (D-021) | ✅ |
| `POST /api/jobs` — validate URL, clamp maxDepth, return 201 + jobId (503 on enqueue failure) | ✅ |
| `GET /api/jobs/{id}` — `JobSummary` DTO, 404 if missing | ✅ |
| `GET /api/jobs/{id}/tree` — in-memory BFS tree builder (`TreeBuilder`) | ✅ |
| `GET /api/jobs` — paginated, newest first, pageSize capped at 100 | ✅ |
| `POST /api/jobs/{id}/cancel` — set Canceled status, 409 if terminal (D-024) | ✅ |
| `GET /health` — liveness + readiness (DB + RabbitMQ) | ✅ |
| JSON camelCase + `JsonStringEnumConverter` for enums | ✅ |
| CORS allowing `http://localhost:5173` | ✅ |
| `ProblemDetails` global exception handler | ✅ |
| EF `MigrateAsync` on API startup with bounded retry (D-023) | ✅ |
| Serilog JSON console + `X-Correlation-Id` + `jobId` scope | ✅ |
| Multi-stage `Dockerfile` for API | ✅ |
| Multi-stage `Dockerfile` for Worker | ✅ |
| `docker-compose.yml` updated: api + worker services with healthcheck dependencies | ✅ |
| `tests/Crawler.Api.Tests` — TreeBuilder, URL/depth validation, publish failure, cancel | ✅ (28 tests) |
| Configurable `User-Agent` header on `PageFetcher` (D-025) | ✅ |

**Open issues:** — (Resolved: `TreeNode` shape codified in `docs/api-contract.md` [D-021]; `GET /api/jobs/{id}/tree` returns `404 ProblemDetails` `"tree not available yet"` when no root page row exists yet [D-022]; `POST /api/jobs/{id}/cancel` implemented and verified [D-024]; configurable `User-Agent` added [D-025]).

**Next step:** Await "continue" from user, then begin Phase 5.5 (React Frontend) or Phase 6 (Integration Tests).

---

### Phase 5.5 — React Frontend (`/frontend`)
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| React 18 + TypeScript (strict) + Vite + React Router + TanStack Query scaffold | ✅ |
| ESLint with `@typescript-eslint/no-explicit-any: "error"` | ✅ |
| Single `frontend/src/index.css` with CSS custom properties (D-029) | ✅ |
| `frontend/src/api/types.ts` copied verbatim from `docs/api-contract.md` | ✅ |
| `frontend/src/api/client.ts` — typed fetchers + `ApiError` carrying `ProblemDetails` | ✅ |
| `useJob` (2s polling until terminal state), `useJobTree` (terminal state + 404 → `null`), `useJobs` (`keepPreviousData`) (D-030) | ✅ |
| `StartCrawlPage` (`/`) — URL validation, `maxDepth` 0..5, spinner, dismissible `ProblemDetails` banner | ✅ |
| `JobDetailsPage` (`/jobs/:jobId`) — copyable ID, timestamps, duration, failure alert, indeterminate progress + live counters + `"Updating..."`, cancel note, summary cards, 404/error/skeleton states | ✅ |
| `PageTree` — recursive WAI-ARIA `role="tree"` / `role="treeitem"`, `aria-expanded`, keyboard navigation, URL filter, expand/collapse all, collapsed beyond depth 1 by default, color-coded Domain Link Ratio bars (D-031) | ✅ |
| `HistoryPage` (`/history`) — newest first, row click navigation, server-side pagination (10/20/50), skeleton/empty/error states | ✅ |
| Optional `frontend` service in `docker-compose.yml` under `profiles: ["frontend"]` (D-032) | ✅ |
| `npm install`, `npm run build`, `npm run lint`, and live end-to-end verification | ✅ |

**Open issues:** —

**Next step:** Await "continue" from user, then begin Phase 6 (Integration Tests).

---

### Phase 6 — Integration Tests (HTML Fixtures + Duplicate Delivery Test)
**Status:** ✅ Done

| Deliverable | Done? |
|---|---|
| HTML fixtures: `index.html`, `about.html`, `contact.html`, `base-href.html`, `transient-parent.html` | ✅ |
| In-process `FakeHttpHandler` serving fixtures with status codes (200, 404, 500, image/png) | ✅ |
| Test DB: real PostgreSQL with unique schema isolation per test class (D-034) | ✅ |
| Fake publisher (`InMemoryPublisher`) + multi-strategy `BrokerPump` (FIFO, reverse, N-times, concurrent) | ✅ |
| Scenario 1: full crawl to completion (pages, statuses, edges, Domain Link Ratio 0.9, job counters) | ✅ |
| Scenario 2: duplicate delivery (every task replayed 2×, one 3×; zero extra rows/tasks/counter drifts) | ✅ |
| Scenario 3: out-of-order delivery (reverse order yields identical final state) | ✅ |
| Scenario 4: concurrent delivery (two siblings concurrently + same message twice; counter never negative) | ✅ |
| Scenario 5: limits (MaxDepth 0 restricts depth; MaxPages 1 halts expansion with 0 pending) | ✅ |
| Scenario 6: root failure (root 404 fails job with reason and 0 pending) | ✅ |
| Scenario 7: transient 500 then dead-letter (OnDeadLetteredAsync marks Failed, double DL idempotent) | ✅ |
| Scenario 8: base href (<base href="https://site.test/sub/"> relative link resolution) | ✅ |
| Scenario 9: job isolation (parallel crawls of same URLs in separate jobs do not interfere) | ✅ |
| Scenario 10: API-to-worker handoff (clean DB with pendingTasks=1 transitions to Running then Completed) | ✅ |
| All tests pass: `dotnet test` (60 passed in Worker.Tests, 136 passed solution-wide) | ✅ |

**Open issues:** — (Resolved: real PostgreSQL used via schema-per-test-class isolation [D-034]; in-process `FakeHttpHandler` + `BrokerPump` [D-035]).

**Next step:** Await "continue" from user, then begin Phase 7 (README + CI + End-to-End Verification).

---

### Phase 7 — README + CI + End-to-End Verification
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| `README.md` — overview + architecture diagram | ⬜ |
| `README.md` — how to run (`docker compose up --build`) | ⬜ |
| `README.md` — message schema | ⬜ |
| `README.md` — idempotency strategy | ⬜ |
| `README.md` — retry policy (transient vs permanent) | ⬜ |
| `README.md` — DLQ handling | ⬜ |
| `README.md` — SQL model + index rationale | ⬜ |
| `README.md` — key architectural choices | ⬜ |
| `README.md` — known limitations | ⬜ |
| `README.md` — "What I implemented first and why" | ⬜ |
| `README.md` — "What I cut and why" | ⬜ |
| `README.md` — "What I'd do next" | ⬜ |
| `.github/workflows/ci.yml` — restore, build, test on push/PR | ⬜ |
| `docker compose up --build` succeeds | ⬜ |
| `curl POST /api/jobs` against live stack returns jobId | ⬜ |
| `curl GET /api/jobs/{id}` shows Completed status | ⬜ |
| `curl GET /api/jobs/{id}/tree` returns populated tree | ⬜ |

**Open issues:**
- Choose a safe, small real-world site for end-to-end curl verification.

**Next step:** Await "continue" from user, then write README + CI and run end-to-end.

---

## Cross-Cutting Open Issues

| # | Issue | Phase | Notes |
|---|---|---|---|
| OI-1 | `processed_messages` table TTL / cleanup | 4+ | Table grows unbounded; a nightly delete job or partitioning is the fix |
| OI-2 | Transactional outbox (D-009) | Post-7 | Dual-write is the current limitation; outbox relay is the correct next step |
| OI-3 | Multi-replica API migration safety (D-010) | Post-7 | `MigrateAsync` on startup is not safe with >1 replica; needs dedicated migration job |
| OI-4 | Job stall recovery | Post-7 | No timeout or recovery mechanism if `pending_tasks` never reaches 0 |
| OI-7 | eTLD+1 host matching (D-008) | Post-7 | Strict host match is current policy; eTLD+1 would be friendlier but needs a public suffix library |

