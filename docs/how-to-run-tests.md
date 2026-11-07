# How to Run Tests

This guide explains how to run the test suite for the `web-crawler` solution across unit and integration tests.

---

## Prerequisites

- **.NET 8 SDK**
- *(Optional for unit tests, recommended for integration tests)* **PostgreSQL 16**:
  - The integration test suite (`CrawlIntegrationTests`) tests true SQL upsert semantics (`ON CONFLICT DO NOTHING`, `ON CONFLICT DO UPDATE WHERE status='Pending'`, and atomic `pending_tasks` counter updates) against a live PostgreSQL database.
  - Start PostgreSQL via Docker Compose:
    ```bash
    docker compose up -d postgres
    ```
  - **Graceful Skip:** If PostgreSQL is not running or unreachable at `localhost:5432`, the integration tests will automatically skip with a clear message rather than fail. All pure unit tests will still run and pass.
  - To point tests to a custom PostgreSQL instance, set the `CRAWLER_TEST_DB` environment variable:
    ```bash
    export CRAWLER_TEST_DB="Host=localhost;Port=5432;Database=crawlerdb;Username=crawler;Password=crawler_pass"
    ```

---

## Test Commands

### 1. Run All Tests Across the Solution
Runs all unit and integration tests:
```bash
dotnet test
```

### 2. Run Only Unit Tests (No Database Required)
Skips the integration suite and executes only pure unit tests (domain logic, URL normalizer, ratio calculator, API validation, tree builder, retry policy):
```bash
dotnet test --filter "FullyQualifiedName!~Integration"
```

### 3. Run Only Worker Integration Tests
Runs the integration tests against real PostgreSQL using local HTML fixtures:
```bash
dotnet test tests/Crawler.Worker.Tests --filter "FullyQualifiedName~Integration"
```

### 4. Run Integration Tests with Detailed Output
```bash
dotnet test tests/Crawler.Worker.Tests --filter "FullyQualifiedName~Integration" --logger "console;verbosity=normal"
```

### 5. Verify Flake-Free Execution (Loop 5x)
```bash
for i in {1..5}; do
  echo "=== RUN $i ==="
  dotnet test tests/Crawler.Worker.Tests --filter "FullyQualifiedName~Integration" --no-build --logger "console;verbosity=normal"
done
```

---

## Test Suite Overview

| Test Project | Type | Requirements | Coverage |
|---|---|---|---|
| `tests/Crawler.Domain.Tests` | Unit | None | `UrlNormalizer` RFC & edge cases, `DomainLinkRatioCalculator` heuristic |
| `tests/Crawler.Api.Tests` | Unit | None | `TreeBuilder` (BFS hierarchy), `JobsController` validation, job cancellation |
| `tests/Crawler.Worker.Tests` (Unit) | Unit | None | `LinkExtractor`, `PageFetcher` (mocked HTTP), `RetryPolicy`, `DeliveryProcessor` |
| `tests/Crawler.Worker.Tests` (`Integration`) | Integration | PostgreSQL | `CrawlIntegrationTests`: 11 scenarios with local HTML fixtures, fake HTTP handler, in-memory broker pump, duplicate delivery, concurrency, dead-letter, and limits |

