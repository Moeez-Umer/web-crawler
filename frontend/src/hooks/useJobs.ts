import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ApiError, getJobs } from "../api/client";
import type { JobListResponse } from "../api/types";

export function useJobs(page: number, pageSize: number) {
  return useQuery<JobListResponse, ApiError>({
    queryKey: ["jobs", page, pageSize],
    queryFn: () => getJobs(page, pageSize),
    placeholderData: keepPreviousData,
    retry: 1,
  });
}

