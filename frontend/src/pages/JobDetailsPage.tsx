import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import { formatDuration, formatTimestamp } from "../api/client";
import { EmptyState } from "../components/EmptyState";
import { ErrorBanner } from "../components/ErrorBanner";
import { PageTree } from "../components/PageTree";
import { Skeleton } from "../components/Skeleton";
import { StatusBadge } from "../components/StatusBadge";
import { SummaryCards } from "../components/SummaryCards";
import { isTerminalJobStatus, useJob } from "../hooks/useJob";
import { useJobTree } from "../hooks/useJobTree";

export function JobDetailsPage() {
  const { jobId } = useParams<{ jobId: string }>();
  const [copied, setCopied] = useState(false);

  const jobQuery = useJob(jobId);
  const job = jobQuery.data;
  const isTerminal = isTerminalJobStatus(job?.status);
  const treeQuery = useJobTree(jobId, job?.status);

  async function handleCopyId() {
    if (!job?.jobId) return;
    try {
      await navigator.clipboard.writeText(job.jobId);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1800);
    } catch {
      // Ignore clipboard write errors in restricted contexts
    }
  }

  if (jobQuery.isPending) {
    return (
      <section className="page-container">
        <div className="card">
          <h1 className="page-title">Job Details</h1>
          <Skeleton rows={6} label="Loading job details..." />
        </div>
      </section>
    );
  }

  if (jobQuery.isError) {
    if (jobQuery.error.status === 404) {
      return (
        <section className="page-container">
          <div className="card">
            <h1 className="page-title">Job not found</h1>
            <p className="page-subtitle">
              No crawl job exists with ID <code>{jobId}</code>.
            </p>
            <div className="empty-state-action">
              <Link to="/" className="btn btn-primary">
                Back to Start Crawl
              </Link>
            </div>
          </div>
        </section>
      );
    }

    return (
      <section className="page-container">
        <div className="card">
          <h1 className="page-title">Job Details</h1>
          <ErrorBanner
            title="Unable to load job"
            message={jobQuery.error.message}
            onRetry={() => void jobQuery.refetch()}
          />
        </div>
      </section>
    );
  }

  if (!job) {
    return null;
  }

  const isInProgress = job.status === "Pending" || job.status === "Running";

  return (
    <section className="page-container job-details-page">
      <div className="card">
        <div className="job-header-top">
          <div>
            <h1 className="page-title">Crawl Job Details</h1>
            <div className="job-id-row">
              <span className="meta-label">Job ID:</span>
              <code className="job-id-code">{job.jobId}</code>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={() => void handleCopyId()}
              >
                {copied ? "Copied!" : "Copy"}
              </button>
            </div>
          </div>

          <div className="job-status-area">
            {jobQuery.isFetching && !jobQuery.isPending && (
              <span className="updating-indicator">Updating...</span>
            )}
            <StatusBadge status={job.status} />
          </div>
        </div>

        <dl className="job-metadata-grid">
          <div className="meta-item meta-item-wide">
            <dt>Target URL</dt>
            <dd>
              <a
                href={job.url}
                target="_blank"
                rel="noopener noreferrer"
                className="external-url-link"
              >
                {job.url}
              </a>
            </dd>
          </div>
          <div className="meta-item">
            <dt>Max Depth</dt>
            <dd>{job.maxDepth}</dd>
          </div>
          <div className="meta-item">
            <dt>Created</dt>
            <dd>{formatTimestamp(job.createdAt)}</dd>
          </div>
          <div className="meta-item">
            <dt>Started</dt>
            <dd>{formatTimestamp(job.startedAt)}</dd>
          </div>
          <div className="meta-item">
            <dt>Completed</dt>
            <dd>{formatTimestamp(job.completedAt)}</dd>
          </div>
          {isTerminal && (
            <div className="meta-item">
              <dt>Duration</dt>
              <dd>
                {formatDuration(job.startedAt, job.createdAt, job.completedAt)}
              </dd>
            </div>
          )}
        </dl>

        {job.status === "Failed" && (
          <div className="failure-alert" role="alert">
            <strong>Failure Reason:</strong>{" "}
            <span>{job.failureReason || "The crawl job failed."}</span>
          </div>
        )}

        {isInProgress && (
          <div className="live-progress-panel">
            <div
              className="progress-indeterminate"
              role="progressbar"
              aria-label="Crawl in progress"
            >
              <div className="progress-indeterminate-bar" />
            </div>
            <div className="live-counters">
              <span className="live-counter">
                Pages crawled: <strong>{job.pagesCrawled}</strong>
              </span>
              <span className="live-counter">
                Tasks in queue: <strong>{job.pendingTasks}</strong>
              </span>
            </div>
          </div>
        )}
      </div>

      {isTerminal && (
        <div className="card tree-card">
          <h2 className="section-title">Crawled Page Tree</h2>

          {job.status === "Canceled" && (
            <div className="canceled-note" role="note">
              This crawl was cancelled; some pages may be pending.
            </div>
          )}

          {treeQuery.isPending && (
            <Skeleton rows={5} label="Loading crawled page tree..." />
          )}

          {treeQuery.isError && (
            <ErrorBanner
              title="Unable to load page tree"
              message={treeQuery.error.message}
              onRetry={() => void treeQuery.refetch()}
            />
          )}

          {!treeQuery.isPending &&
            !treeQuery.isError &&
            treeQuery.data === null && (
              <EmptyState
                title="No tree available"
                description="No crawled page hierarchy was recorded for this job."
              />
            )}

          {!treeQuery.isPending &&
            !treeQuery.isError &&
            treeQuery.data !== null &&
            treeQuery.data !== undefined && (
              <>
                <SummaryCards root={treeQuery.data} />
                <PageTree root={treeQuery.data} />
              </>
            )}
        </div>
      )}
    </section>
  );
}

