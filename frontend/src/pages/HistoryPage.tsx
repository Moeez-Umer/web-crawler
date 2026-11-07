import { useId, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { formatDuration, formatTimestamp } from "../api/client";
import { EmptyState } from "../components/EmptyState";
import { ErrorBanner } from "../components/ErrorBanner";
import { Skeleton } from "../components/Skeleton";
import { StatusBadge } from "../components/StatusBadge";
import { useJobs } from "../hooks/useJobs";

const PAGE_SIZE_OPTIONS = [10, 20, 50] as const;

export function HistoryPage() {
  const navigate = useNavigate();
  const captionId = useId();
  const pageSizeSelectId = useId();

  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState<number>(20);

  const jobsQuery = useJobs(page, pageSize);
  const data = jobsQuery.data;
  const totalPages = data
    ? Math.max(1, Math.ceil(data.totalCount / pageSize))
    : 1;

  return (
    <section className="page-container history-page">
      <div className="card">
        <div className="history-header">
          <div>
            <h1 className="page-title">Crawl History</h1>
            <p className="page-subtitle">
              All crawl jobs ordered newest first. Click any row to inspect the
              job and its page tree.
            </p>
          </div>
          {jobsQuery.isFetching && !jobsQuery.isPending && (
            <span className="updating-indicator">Updating...</span>
          )}
        </div>

        {jobsQuery.isError && (
          <ErrorBanner
            title="Unable to load crawl history"
            message={jobsQuery.error.message}
            onRetry={() => void jobsQuery.refetch()}
          />
        )}

        {jobsQuery.isPending && (
          <Skeleton rows={6} label="Loading crawl history..." />
        )}

        {!jobsQuery.isPending &&
          !jobsQuery.isError &&
          data &&
          data.totalCount === 0 && (
            <EmptyState
              title="No crawl jobs yet"
              description="Submit a website URL to run your first crawl job."
              action={
                <Link to="/" className="btn btn-primary">
                  Start your first crawl
                </Link>
              }
            />
          )}

        {!jobsQuery.isPending && data && data.totalCount > 0 && (
          <>
            <div
              tabIndex={0}
              role="region"
              aria-labelledby={captionId}
              className="table-wrapper"
            >
              <table className="history-table">
                <caption id={captionId} className="visually-hidden">
                  Crawl job history ordered newest first
                </caption>
                <thead>
                  <tr>
                    <th scope="col">ID</th>
                    <th scope="col">URL</th>
                    <th scope="col">Status</th>
                    <th scope="col">Max Depth</th>
                    <th scope="col">Created</th>
                    <th scope="col">Duration</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((job) => {
                    const shortId = job.jobId.slice(0, 8);
                    return (
                      <tr
                        key={job.jobId}
                        className="history-row"
                        onClick={() => navigate(`/jobs/${job.jobId}`)}
                      >
                        <th scope="row" className="cell-id">
                          <Link
                            to={`/jobs/${job.jobId}`}
                            className="short-id-link"
                            onClick={(e) => e.stopPropagation()}
                            title={job.jobId}
                          >
                            <code>{shortId}</code>
                          </Link>
                        </th>
                        <td className="cell-url" title={job.url}>
                          <span className="truncated-url">{job.url}</span>
                        </td>
                        <td>
                          <StatusBadge status={job.status} />
                        </td>
                        <td>{job.maxDepth}</td>
                        <td>{formatTimestamp(job.createdAt)}</td>
                        <td>
                          {formatDuration(
                            job.startedAt,
                            job.createdAt,
                            job.completedAt
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>

            <div className="pagination-bar">
              <div className="page-size-control">
                <label htmlFor={pageSizeSelectId}>Rows per page:</label>
                <select
                  id={pageSizeSelectId}
                  className="select"
                  value={pageSize}
                  onChange={(e) => {
                    setPageSize(Number(e.target.value));
                    setPage(1);
                  }}
                >
                  {PAGE_SIZE_OPTIONS.map((size) => (
                    <option key={size} value={size}>
                      {size}
                    </option>
                  ))}
                </select>
              </div>

              <div className="pagination-controls">
                <span className="page-indicator">
                  Page {page} of {totalPages}
                </span>
                <button
                  type="button"
                  className="btn btn-secondary btn-sm"
                  disabled={page <= 1 || jobsQuery.isPlaceholderData}
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                >
                  Previous
                </button>
                <button
                  type="button"
                  className="btn btn-secondary btn-sm"
                  disabled={page >= totalPages || jobsQuery.isPlaceholderData}
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                >
                  Next
                </button>
              </div>
            </div>
          </>
        )}
      </div>
    </section>
  );
}

