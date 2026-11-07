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

