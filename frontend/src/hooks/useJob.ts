import { useQuery } from "@tanstack/react-query";
import { ApiError, getJob } from "../api/client";
import type { JobStatus, JobSummary } from "../api/types";

const TERMINAL_STATUSES: ReadonlySet<JobStatus> = new Set<JobStatus>([
  "Completed",
  "Failed",
  "Canceled",
]);

export function isTerminalJobStatus(status: JobStatus | undefined): boolean {
  return status !== undefined && TERMINAL_STATUSES.has(status);
}

export function useJob(jobId: string | undefined) {
  return useQuery<JobSummary, ApiError>({
    queryKey: ["job", jobId],
    queryFn: () => {
      if (!jobId) {
        throw new ApiError(400, { detail: "Job ID is required." }, "Job ID is required.");
      }
      return getJob(jobId);
    },
    enabled: Boolean(jobId),
    // Stop polling once the job reaches any terminal state (Completed, Failed, Canceled).
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      if (isTerminalJobStatus(status)) {
        return false;
      }
      if (query.state.error?.status === 404) {
        return false;
      }
      return 2000;
    },
    retry: (failureCount, error) => {
      if (error.status === 404) {
        return false;
      }
      return failureCount < 1;
    },
  });
}

