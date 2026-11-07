# API Contract — Web Crawler

This document is the single source of truth for the Crawl API (`Crawler.Api`) and any client consuming it (including the React frontend).

---

## General Rules

1. **Content-Type:** Request and response bodies use `application/json`.
2. **Naming:** All JSON property names are serialized in `camelCase`.
3. **Enums:** All enums (`JobStatus`, `PageStatus`) are serialized as strings (`"Pending"`, `"Running"`, `"Completed"`, `"Failed"`, `"Canceled"`, `"Crawled"`).
4. **Timestamps:** All timestamps (`createdAt`, `startedAt`, `completedAt`) are serialized as ISO 8601 UTC strings (e.g., `"2026-11-07T05:30:00.123456Z"`), or `null` when not yet set.
5. **Ratios:** `domainLinkRatio` is a floating-point number in the range `[0, 1]` (`0` when a page has zero outgoing valid links or has not been crawled yet).
6. **Errors:** All non-2xx error responses use RFC 7807 `ProblemDetails` (`application/problem+json`) with `type`, `title`, `status`, and `detail` (plus optional `errors` dictionary for validation errors). Stack traces are never exposed.
7. **Correlation Header:** Every request accepts an optional `X-Correlation-Id` header (a new GUID is generated if omitted) and echoes `X-Correlation-Id` in the response headers.

---

## TypeScript Types

```ts
export type JobStatus = "Pending" | "Running" | "Completed" | "Failed" | "Canceled";

export type PageStatus = "Crawled" | "Failed" | "Pending";

export type CreateJobRequest = {
  url: string;
  maxDepth?: number;
};

export type CreateJobResponse = {
  jobId: string;
};

export type JobSummary = {
  jobId: string;
  url: string;
  maxDepth: number;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
  pagesCrawled: number;
  pendingTasks: number;
};

export type TreeNode = {
  url: string;
  depth: number;
  domainLinkRatio: number;
  totalLinks: number;
  internalLinks: number;
  status: "Crawled" | "Failed" | "Pending";
  error?: string | null;
  children: TreeNode[];
};

export type JobListResponse = {
  items: JobSummary[];
  page: number;
  pageSize: number;
  totalCount: number;
};

export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
};
```

---

## Endpoints

### 1. `POST /api/jobs` — Create and Enqueue a Crawl Job

- **Request Body (`CreateJobRequest`):**
  ```json
  {
    "url": "https://example.com",
    "maxDepth": 2
  }
  ```
  - `url` (required): Must be a valid absolute `http://` or `https://` URL. Normalized via `UrlNormalizer` before storage.
  - `maxDepth` (optional): Defaults to `2`. Values outside `0..5` are clamped to `[0, 5]`.
- **Responses:**
  - `201 Created`: Job persisted (`Pending`, `pendingTasks = 1`) and root `CrawlPageTask` published to RabbitMQ.
    - `Location` header: `/api/jobs/{jobId}`
    - Body (`CreateJobResponse`):
      ```json
      {
        "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
      }
      ```
  - `400 Bad Request` (`ProblemDetails`): Missing, malformed, or non-`http(s)` URL.
  - `503 Service Unavailable` (`ProblemDetails`): Job row was created, but publishing the root task to RabbitMQ failed. The job is marked `Failed` with `failureReason = "failed to enqueue"` and `completedAt` set.

---

### 2. `GET /api/jobs/{id}` — Get Job Summary

- **Route Parameters:**
  - `id` (UUID): The job ID.
- **Responses:**
  - `200 OK`: Returns `JobSummary`.
    ```json
    {
      "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "url": "https://example.com/",
      "maxDepth": 2,
      "status": "Completed",
      "createdAt": "2026-11-07T05:30:00.000000Z",
      "startedAt": "2026-11-07T05:30:00.150000Z",
      "completedAt": "2026-11-07T05:30:01.200000Z",
      "failureReason": null,
      "pagesCrawled": 1,
      "pendingTasks": 0
    }
    ```
  - `404 Not Found` (`ProblemDetails`): No job exists with the given `id`.

---

### 3. `GET /api/jobs/{id}/tree` — Get Crawled Page Tree

- **Route Parameters:**
  - `id` (UUID): The job ID.
- **Behaviour:**
  - Assembles the hierarchical `TreeNode` from the job's pages and edges using BFS from the depth-0 root page ("first-discovery-parent" rule; each page appears at most once; cycle-safe; children ordered deterministically by discovery order, then URL).
  - `domainLinkRatio` is a number in `[0, 1]`.
  - Nodes carry `status`: `"Crawled"`, `"Failed"` (with `error` populated), or `"Pending"` (discovered child placeholder not yet crawled).
- **Responses:**
  - `200 OK`: Returns root `TreeNode`.
    ```json
    {
      "url": "https://example.com/",
      "depth": 0,
      "domainLinkRatio": 0,
      "totalLinks": 1,
      "internalLinks": 0,
      "status": "Crawled",
      "error": null,
      "children": []
    }
    ```
  - `404 Not Found` (`ProblemDetails`):
    - Job does not exist, OR
    - Job exists, but no root page row has been recorded yet (`detail: "tree not available yet"`).

---

### 4. `GET /api/jobs` — List Jobs (Paginated, Newest First)

- **Query Parameters:**
  - `page` (optional, default `1`): 1-based page number (`< 1` clamped to `1`).
  - `pageSize` (optional, default `20`): Items per page (clamped to `1..100`).
- **Responses:**
  - `200 OK`: Returns `JobListResponse` ordered by `createdAt DESC`.
    ```json
    {
      "items": [
        {
          "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
          "url": "https://example.com/",
          "maxDepth": 2,
          "status": "Completed",
          "createdAt": "2026-11-07T05:30:00.000000Z",
          "startedAt": "2026-11-07T05:30:00.150000Z",
          "completedAt": "2026-11-07T05:30:01.200000Z",
          "failureReason": null,
          "pagesCrawled": 1,
          "pendingTasks": 0
        }
      ],
      "page": 1,
      "pageSize": 20,
      "totalCount": 1
    }
    ```

---

### 5. `POST /api/jobs/{id}/cancel` — Cancel an Active Job

- **Route Parameters:**
  - `id` (UUID): The job ID.
- **Responses:**
  - `200 OK`: Returns updated `JobSummary` with `status: "Canceled"` and `completedAt` set.
  - `404 Not Found` (`ProblemDetails`): Job does not exist.
  - `409 Conflict` (`ProblemDetails`): Job is already in a terminal state (`Completed`, `Failed`, or `Canceled`).

---

### 6. `GET /health` — Liveness & Readiness Probe

- **Behaviour:** Verifies connectivity to PostgreSQL and RabbitMQ.
- **Responses:**
  - `200 OK`: All checks healthy.
  - `503 Service Unavailable`: One or more checks degraded/unhealthy.
  - Body:
    ```json
    {
      "status": "Healthy",
      "checks": [
        { "name": "postgres", "status": "Healthy" },
        { "name": "rabbitmq", "status": "Healthy" }
      ]
    }
    ```

