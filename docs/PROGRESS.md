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
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| `UrlNormalizer` implemented (pure static, no external deps) | ⬜ |
| `UrlNormalizer` trailing-slash policy chosen and documented | ⬜ |
| `DomainLinkRatioCalculator` implemented | ⬜ |
| Unit tests: `UrlNormalizerTests` — all edge cases from SPEC | ⬜ |
| Unit tests: `DomainLinkRatioTests` — zero links, all-internal, all-external, mixed | ⬜ |
| `dotnet test` passes with zero failures | ⬜ |

**Edge cases to cover in `UrlNormalizerTests`:**
- Relative paths, `../`, `./`
- Fragment stripping (`#section`)
- Default port removal (`:80`, `:443`)
- Mixed-case host/scheme normalisation
- `mailto:`, `tel:`, `javascript:`, `data:`, `ftp:` → reject (return null)
- Protocol-relative `//host/path`
- Query strings preserved
- Empty `href`

**Open issues:** —

**Next step:** Await "continue" from user, then implement domain layer.

---

### Phase 3 — RabbitMQ Topology + Publisher + Consumer + Retry + DLQ
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| `RabbitMqTopology` — declare exchanges and queues on startup | ⬜ |
| `crawl.exchange` → `crawl.tasks` (DLX = `crawl.dlx`) | ⬜ |
| `crawl.retry.exchange` → `crawl.retry` (TTL → DLX → `crawl.tasks`) | ⬜ |
| `crawl.dlx` → `crawl.tasks.dlq` | ⬜ |
| Durable queues, persistent messages, manual acks | ⬜ |
| `CrawlTaskPublisher` — serialise and publish `CrawlPageTask` | ⬜ |
| `CrawlTaskConsumer` — `BackgroundService`, prefetch configurable | ⬜ |
| Retry logic: transient error → publish to `crawl.retry` with `attempt+1` | ⬜ |
| Exhausted retries (attempt ≥ 5) → NACK `requeue=false` → DLQ | ⬜ |
| Poison message → NACK `requeue=false` → DLQ | ⬜ |
| Graceful shutdown: drain in-flight, stop consuming | ⬜ |

**Open issues:**
- Confirm retry TTL default value (e.g. 5 000 ms with exponential per-attempt).

**Next step:** Await "continue" from user, then implement broker layer.

---

### Phase 4 — Worker: PageFetcher + LinkExtractor + CrawlTaskProcessor + Idempotency + Completion Counter
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| `IPageFetcher` interface + `PageFetcher` impl (HtmlAgilityPack, Polly, 2 MB cap) | ⬜ |
| `ILinkExtractor` interface + `LinkExtractor` impl | ⬜ |
| `CrawlTaskProcessor` — orchestrates one task end-to-end | ⬜ |
| Idempotency: `ProcessedMessages` guard in transaction | ⬜ |
| Page upsert `ON CONFLICT DO NOTHING`, capture rows-affected | ⬜ |
| Edge upsert `ON CONFLICT DO NOTHING` | ⬜ |
| Fan-out: publish child tasks only for newly inserted pages | ⬜ |
| `pending_tasks` counter: atomic `UPDATE ... RETURNING` | ⬜ |
| Completion detection: if `RETURNING = 0`, mark job Completed/Failed | ⬜ |
| Permanent page failure: record page as Failed, ACK, decrement counter | ⬜ |
| Root page permanent failure: set job status to Failed | ⬜ |
| Canceled job: ACK and skip | ⬜ |
| Worker `/health` endpoint (Kestrel on separate port) | ⬜ |
| Serilog enrichment: `jobId`, `messageId`, `correlationId`, `url`, `depth`, `attempt` | ⬜ |

**Open issues:**
- Confirm `maxPages` guard placement (before publishing children, in `CrawlTaskProcessor`).
- Decide Kestrel health port (e.g. 5001 or 8080).

**Next step:** Await "continue" from user, then implement worker layer.

---

### Phase 5 — API Endpoints + DTOs + Tree Builder + Health + Serilog + CORS + Dockerfiles
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| `POST /api/jobs` — validate URL, clamp maxDepth, return 201 + jobId | ⬜ |
| `GET /api/jobs/{id}` — `JobSummary` DTO, 404 if missing | ⬜ |
| `GET /api/jobs/{id}/tree` — in-memory BFS tree builder | ⬜ |
| `GET /api/jobs` — paginated, newest first, pageSize capped at 100 | ⬜ |
| `POST /api/jobs/{id}/cancel` — set Canceled status | ⬜ |
| `GET /health` — liveness + readiness (DB + RabbitMQ) | ⬜ |
| JSON camelCase + `JsonStringEnumConverter` for enums | ⬜ |
| CORS allowing `http://localhost:5173` | ⬜ |
| `ProblemDetails` global exception handler | ⬜ |
| EF `MigrateAsync` on API startup | ⬜ |
| Serilog JSON console + request logging | ⬜ |
| Multi-stage `Dockerfile` for API | ⬜ |
| Multi-stage `Dockerfile` for Worker | ⬜ |
| `docker-compose.yml` updated: api + worker services with healthcheck dependencies | ⬜ |

**Open issues:**
- Confirm tree response shape (`TreeNode` with `children[]`).
- Decide whether `GET /api/jobs/{id}/tree` returns 404 or empty tree while job is still running.

**Next step:** Await "continue" from user, then implement API layer.

---

### Phase 6 — Integration Tests (HTML Fixtures + Duplicate Delivery Test)
**Status:** ⬜ Not started

| Deliverable | Done? |
|---|---|
| HTML fixtures: `index.html`, `about.html`, `contact.html` (external link, mailto, duplicate, fragment) | ⬜ |
| In-process test HTTP server or fake `HttpMessageHandler` serving fixtures | ⬜ |
| Test DB: in-memory or Testcontainers PostgreSQL | ⬜ |
| Fake publisher: captures published messages without broker | ⬜ |
| Integration test: crawl fixture site, assert pages + edges + ratios | ⬜ |
| Integration test: no duplicate rows on first crawl | ⬜ |
| Integration test: same message delivered twice → no duplicate rows, counter not double-decremented | ⬜ |
| All tests pass: `dotnet test` | ⬜ |

**Open issues:**
- Decide whether to use Testcontainers (real Postgres) or SQLite for integration tests; Testcontainers preferred for `ON CONFLICT` syntax compatibility.

**Next step:** Await "continue" from user, then implement integration tests.

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
| OI-5 | Cancel endpoint implementation order | 5 | Marked optional in SPEC; implement last within Phase 5 |
| OI-6 | Frontend (React app) | Post-7 | `frontend/` left empty per SPEC; user will add it |
| OI-7 | eTLD+1 host matching (D-008) | Post-7 | Strict host match is current policy; eTLD+1 would be friendlier but needs a public suffix library |

