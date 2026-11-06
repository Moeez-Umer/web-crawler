using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Raw-SQL helpers for all upsert and atomic counter operations.
/// EF cannot express ON CONFLICT clauses so we drop to ADO-level SQL here.
/// Every method that mutates state accepts a live IDbContextTransaction so it
/// can participate in the caller's single atomic transaction.
/// </summary>
public class CrawlRepository : ICrawlRepository
{
    private readonly CrawlDbContext _db;

    public CrawlRepository(CrawlDbContext db)
    {
        _db = db;
    }

    // ── Transaction helpers ──────────────────────────────────────────────────

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => _db.Database.BeginTransactionAsync(ct);

    // ── Job queries ──────────────────────────────────────────────────────────

    public Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => _db.CrawlJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);

    // ── Idempotency guard ────────────────────────────────────────────────────

    /// <summary>
    /// Inserts the processed-message record inside the caller's open transaction.
    /// Returns true if this is the FIRST time we see the messageId (newly inserted),
    /// false if it already existed (duplicate delivery — caller should ACK and skip).
    /// </summary>
    public async Task<bool> TryInsertProcessedMessageAsync(Guid messageId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        // ON CONFLICT DO NOTHING means rows-affected = 0 on duplicate.
        int rows = await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO processed_messages (message_id, processed_at) VALUES ({0}, now()) ON CONFLICT DO NOTHING",
            new object[] { messageId }, ct);

        return rows > 0;
    }

    // ── Job state transitions ────────────────────────────────────────────────

    /// <summary>
    /// Atomically moves job Pending → Running only if still Pending (safe on duplicate delivery).
    /// </summary>
    public Task SetJobRunningIfPendingAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Running', started_at = now() WHERE id = {0} AND status = 'Pending'",
            new object[] { jobId }, ct);

    /// <summary>
    /// Marks the job Completed + sets CompletedAt.
    /// </summary>
    public Task SetJobCompletedAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Completed', completed_at = now() WHERE id = {0}",
            new object[] { jobId }, ct);

    /// <summary>
    /// Marks the job Failed + sets FailureReason + CompletedAt.
    /// </summary>
    public Task SetJobFailedAsync(Guid jobId, string reason, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Failed', failure_reason = {1}, completed_at = now() WHERE id = {0}",
            new object[] { jobId, reason }, ct);

    // ── Page upsert ──────────────────────────────────────────────────────────

    /// <summary>
    /// Upserts a crawled-page row.  Returns the row's Id if the row was NEWLY
    /// inserted; null if it already existed (ON CONFLICT DO NOTHING).
    /// </summary>
    public async Task<Guid?> UpsertPageAsync(
        Guid pageId,
        Guid jobId,
        string normalizedUrl,
        int depth,
        double domainLinkRatio,
        int totalLinks,
        int internalLinks,
        PageStatus status,
        string? error,
        IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        // We use RETURNING id to know whether the row was actually inserted.
        // EF's ExecuteSqlRaw does not expose RETURNING, so we use FromSqlRaw on a
        // temporary projection.  Wrapping in a list handles the 0-row case.
        var result = await _db.Database
            .SqlQueryRaw<Guid>(
                @"INSERT INTO crawled_pages
                    (id, job_id, normalized_url, depth, domain_link_ratio,
                     total_links, internal_links, status, error, crawled_at)
                  VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},now())
                  ON CONFLICT (job_id, normalized_url) DO NOTHING
                  RETURNING id",
                pageId, jobId, normalizedUrl, depth, domainLinkRatio,
                totalLinks, internalLinks, status.ToString(), error!)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : null;
    }

    /// <summary>
    /// Updates an existing page's status and error (used to record permanent failures).
    /// </summary>
    public Task UpdatePageStatusAsync(
        Guid jobId,
        string normalizedUrl,
        PageStatus status,
        string? error,
        IDbContextTransaction tx,
        CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawled_pages SET status = {2}, error = {3}, crawled_at = now() WHERE job_id = {0} AND normalized_url = {1}",
            new object[] { jobId, normalizedUrl, status.ToString(), error! }, ct);

    // ── Page read ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Id of a crawled page row identified by (jobId, normalizedUrl),
    /// or null if the row does not exist yet.
    /// Used to look up the parent page's Id before inserting edges.
    /// </summary>
    public async Task<Guid?> GetPageIdAsync(Guid jobId, string normalizedUrl, CancellationToken ct = default)
    {
        var result = await _db.Database
            .SqlQueryRaw<Guid>(
                "SELECT id FROM crawled_pages WHERE job_id = {0} AND normalized_url = {1}",
                jobId, normalizedUrl)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : null;
    }

    // ── Edge upsert ──────────────────────────────────────────────────────────

    public Task UpsertEdgeAsync(
        Guid edgeId,
        Guid jobId,
        Guid parentPageId,
        string childUrl,
        IDbContextTransaction tx,
        CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO page_edges (id, job_id, parent_page_id, child_url)
              VALUES ({0},{1},{2},{3})
              ON CONFLICT (job_id, parent_page_id, child_url) DO NOTHING",
            new object[] { edgeId, jobId, parentPageId, childUrl }, ct);

    // ── Counter update ───────────────────────────────────────────────────────

    /// <summary>
    /// Atomically adjusts pending_tasks by delta (positive = children added,
    /// negative = this task finished) and increments pages_crawled if isNewPage.
    /// Returns the new value of pending_tasks after the update.
    ///
    /// Why RETURNING in the same statement: ensures the check-and-update is
    /// atomic under concurrent workers; no TOCTOU race.
    /// </summary>
    public async Task<int> AdjustCountersAndReturnPendingAsync(
        Guid jobId,
        int pendingDelta,   // newChildCount - 1
        bool isNewPage,
        IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        int pagesDelta = isNewPage ? 1 : 0;

        var result = await _db.Database
            .SqlQueryRaw<int>(
                @"UPDATE crawl_jobs
                  SET pending_tasks  = pending_tasks  + {1},
                      pages_crawled  = pages_crawled  + {2}
                  WHERE id = {0}
                  RETURNING pending_tasks",
                jobId, pendingDelta, pagesDelta)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : 0;
    }
}
