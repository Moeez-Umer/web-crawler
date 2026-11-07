import type { JobStatus } from "../api/types";

interface StatusBadgeProps {
  status: JobStatus;
}

export function StatusBadge({ status }: StatusBadgeProps) {
  const statusClass = `status-badge status-${status.toLowerCase()}`;
  return <span className={statusClass}>{status}</span>;
}

