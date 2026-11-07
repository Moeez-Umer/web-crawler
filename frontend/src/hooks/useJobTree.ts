import { useQuery } from "@tanstack/react-query";
import { ApiError, getJobTree } from "../api/client";
import type { JobStatus, TreeNode } from "../api/types";
import { isTerminalJobStatus } from "./useJob";

export function useJobTree(
  jobId: string | undefined,
  jobStatus: JobStatus | undefined
) {
  return useQuery<TreeNode | null, ApiError>({
    queryKey: ["jobTree", jobId],
    queryFn: async () => {
      if (!jobId) {
        return null;
      }
      try {
        return await getJobTree(jobId);
      } catch (err) {
        // A 404 from /api/jobs/:id/tree means no root page was recorded (D-022);
        // treat it as "no tree available" rather than an error state.
        if (err instanceof ApiError && err.status === 404) {
          return null;
        }
        throw err;
      }
    },
    enabled: Boolean(jobId) && isTerminalJobStatus(jobStatus),
    retry: (failureCount, error) => {
      if (error.status === 404) {
        return false;
      }
      return failureCount < 1;
    },
  });
}

