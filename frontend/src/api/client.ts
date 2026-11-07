import type {
  CreateJobRequest,
  CreateJobResponse,
  JobListResponse,
  JobSummary,
  ProblemDetails,
  TreeNode,
} from "./types";

const rawBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";
const API_BASE_URL = rawBaseUrl.endsWith("/")
  ? rawBaseUrl.slice(0, -1)
  : rawBaseUrl;

const UNREACHABLE_MESSAGE = "Can't reach the server";

const TIMESTAMP_FORMATTER = new Intl.DateTimeFormat("en-GB", {
  day: "numeric",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false,
});

function isGatewayOrUnavailableStatus(status: number): boolean {
  return status === 0 || status === 502 || status === 503 || status === 504;
}

export class ApiError extends Error {
  public readonly status: number;
  public readonly problemDetails: ProblemDetails;

  constructor(
    status: number,
    problemDetails: ProblemDetails,
    fallbackMessage: string
  ) {
    const detailFromErrors = problemDetails.errors
      ? Object.values(problemDetails.errors).flat().join(" ")
      : undefined;
    const message = isGatewayOrUnavailableStatus(status)
      ? UNREACHABLE_MESSAGE
      : problemDetails.detail ||
        detailFromErrors ||
        problemDetails.title ||
        fallbackMessage;
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.problemDetails = problemDetails;
  }
}

async function parseErrorResponse(response: Response): Promise<ApiError> {
  const isUnreachable = isGatewayOrUnavailableStatus(response.status);
  const fallback = isUnreachable
    ? UNREACHABLE_MESSAGE
    : `Request failed with status ${response.status}`;
  const text = await response.text();
  if (!text) {
    return new ApiError(
      response.status,
      {
        status: response.status,
        title: response.statusText || "Error",
        detail: fallback,
      },
      fallback
    );
  }

  try {
    const parsed = JSON.parse(text) as ProblemDetails;
    if (parsed && typeof parsed === "object") {
      return new ApiError(
        parsed.status ?? response.status,
        parsed,
        fallback
      );
    }
  } catch {
    // Non-JSON error response (e.g. proxy error when backend container is down)
  }

  return new ApiError(
    response.status,
    {
      status: response.status,
      title: response.statusText || "Error",
      detail: isUnreachable
        ? UNREACHABLE_MESSAGE
        : text.length <= 240
          ? text
          : fallback,
    },
    fallback
  );
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      ...init,
      headers: {
        Accept: "application/json, application/problem+json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });
  } catch {
    throw new ApiError(
      0,
      { status: 0, title: "Network Error", detail: UNREACHABLE_MESSAGE },
      UNREACHABLE_MESSAGE
    );
  }

  if (!response.ok) {
    throw await parseErrorResponse(response);
  }

  return (await response.json()) as T;
}

export function createJob(payload: CreateJobRequest): Promise<CreateJobResponse> {
  return requestJson<CreateJobResponse>("/api/jobs", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function getJob(jobId: string): Promise<JobSummary> {
  return requestJson<JobSummary>(`/api/jobs/${encodeURIComponent(jobId)}`);
}

export function getJobTree(jobId: string): Promise<TreeNode> {
  return requestJson<TreeNode>(`/api/jobs/${encodeURIComponent(jobId)}/tree`);
}

export function getJobs(page: number, pageSize: number): Promise<JobListResponse> {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  return requestJson<JobListResponse>(`/api/jobs?${params.toString()}`);
}

export function formatTimestamp(iso: string | null): string {
  if (!iso) {
    return "-";
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return TIMESTAMP_FORMATTER.format(date);
}

export function formatDuration(
  startedAt: string | null,
  createdAt: string,
  completedAt: string | null
): string {
  if (!completedAt) {
    return "-";
  }
  const startIso = startedAt ?? createdAt;
  const startMs = new Date(startIso).getTime();
  const endMs = new Date(completedAt).getTime();
  if (Number.isNaN(startMs) || Number.isNaN(endMs)) {
    return "-";
  }
  const diffMs = Math.max(0, endMs - startMs);
  if (diffMs < 1000) {
    return `${diffMs}ms`;
  }
  const totalSeconds = diffMs / 1000;
  if (totalSeconds < 60) {
    return `${totalSeconds.toFixed(1)}s`;
  }
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = Math.round(totalSeconds % 60);
  return `${minutes}m ${seconds}s`;
}
