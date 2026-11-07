using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crawler.Infrastructure.Persistence;

/// <summary>
/// Raw-SQL helpers for all upsert and atomic counter operations.
/// EF cannot express ON CONFLICT clauses so we drop to raw SQL here.
/// Mutating methods take the caller's IDbContextTransaction to make the
/// transactional contract explicit; they run on the DbContext's current transaction.
/// Scalar queries alias their column as "Value" because EF Core's SqlQueryRaw&lt;T&gt;
/// maps scalar results from a column with that name.
/// </summary>
public class CrawlRepository : ICrawlRepository
{
    private readonly CrawlDbContext _db;

    public CrawlRepository(CrawlDbContext db)
    {
        _db = db;
    }

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => _db.Database.BeginTransactionAsync(ct);

    public Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => _db.CrawlJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);

    /// <summary>
    /// Inserts the processed-message guard. Returns false if the messageId already exists.
    /// This single PK is the idempotency key for every path that changes the counter
    /// (success, permanent failure, dead-letter finalization).
    /// </summary>
    public async Task<bool> TryInsertProcessedMessageAsync(Guid messageId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        int rows = await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO processed_messages (message_id, processed_at) VALUES ({0}, now()) ON CONFLICT DO NOTHING",
            new object[] { messageId }, ct);

        return rows > 0;
    }

    public Task SetJobRunningIfPendingAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Running', started_at = now() WHERE id = {0} AND status = 'Pending'",
            new object[] { jobId }, ct);

    /// <summary>
    /// Pending/Running → Completed. The status filter means a job that is already
    /// Failed (root failed earlier) or Canceled is never overwritten.
    /// </summary>
    public Task SetJobCompletedAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Completed', completed_at = now() WHERE id = {0} AND status IN ('Pending', 'Running')",
            new object[] { jobId }, ct);

    /// <summary>Pending/Running → Failed with reason. Never overwrites a terminal state.</summary>
    public Task SetJobFailedAsync(Guid jobId, string reason, IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawl_jobs SET status = 'Failed', failure_reason = {1}, completed_at = now() WHERE id = {0} AND status IN ('Pending', 'Running')",
            new object[] { jobId, reason }, ct);

    /// <summary>
    /// Inserts a page row only if absent (ON CONFLICT DO NOTHING).
    /// Used for child placeholders (status Pending). Returns the id if newly inserted.
    /// </summary>
    public async Task<Guid?> UpsertPageAsync(
        Guid pageId, Guid jobId, string normalizedUrl, int depth,
        double domainLinkRatio, int totalLinks, int internalLinks,
        PageStatus status, string? error, IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        var result = await _db.Database
            .SqlQueryRaw<Guid>(
                @"INSERT INTO crawled_pages
                    (id, job_id, normalized_url, depth, domain_link_ratio,
                     total_links, internal_links, status, error, crawled_at)
                  VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},now())
                  ON CONFLICT (job_id, normalized_url) DO NOTHING
                  RETURNING id AS ""Value""",
                pageId, jobId, normalizedUrl, depth, domainLinkRatio,
                totalLinks, internalLinks, status.ToString(), (object?)error ?? DBNull.Value)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : null;
    }

    /// <summary>
    /// Records the outcome of a page's own task. Inserts the row if absent, or
    /// upgrades it only while it is still a Pending placeholder. A row that is
    /// already Crawled or Failed is left untouched, so a late failure can never
    /// overwrite a successful crawl. Returns the id if a row was written.
    /// Depth is not updated: the placeholder keeps its first-discovery depth.
    /// </summary>
    public async Task<Guid?> RecordPageResultAsync(
        Guid pageId, Guid jobId, string normalizedUrl, int depth,
        double domainLinkRatio, int totalLinks, int internalLinks,
        PageStatus status, string? error, IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        var result = await _db.Database
            .SqlQueryRaw<Guid>(
                @"INSERT INTO crawled_pages
                    (id, job_id, normalized_url, depth, domain_link_ratio,
                     total_links, internal_links, status, error, crawled_at)
                  VALUES ({0},{1},{2},{3},{4},{5},{6},{7},{8},now())
                  ON CONFLICT (job_id, normalized_url) DO UPDATE SET
                     domain_link_ratio = EXCLUDED.domain_link_ratio,
                     total_links       = EXCLUDED.total_links,
                     internal_links    = EXCLUDED.internal_links,
                     status            = EXCLUDED.status,
                     error             = EXCLUDED.error,
                     crawled_at        = EXCLUDED.crawled_at
                  WHERE crawled_pages.status = 'Pending'
                  RETURNING id AS ""Value""",
                pageId, jobId, normalizedUrl, depth, domainLinkRatio,
                totalLinks, internalLinks, status.ToString(), (object?)error ?? DBNull.Value)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : null;
    }

    public Task UpdatePageStatusAsync(
        Guid jobId, string normalizedUrl, PageStatus status, string? error,
        IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            "UPDATE crawled_pages SET status = {2}, error = {3}, crawled_at = now() WHERE job_id = {0} AND normalized_url = {1}",
            new object[] { jobId, normalizedUrl, status.ToString(), (object?)error ?? DBNull.Value }, ct);

    public async Task<Guid?> GetPageIdAsync(Guid jobId, string normalizedUrl, CancellationToken ct = default)
    {
        var result = await _db.Database
            .SqlQueryRaw<Guid>(
                @"SELECT id AS ""Value"" FROM crawled_pages WHERE job_id = {0} AND normalized_url = {1}",
                jobId, normalizedUrl)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : null;
    }

    public Task UpsertEdgeAsync(
        Guid edgeId, Guid jobId, Guid parentPageId, string childUrl,
        IDbContextTransaction tx, CancellationToken ct = default)
        => _db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO page_edges (id, job_id, parent_page_id, child_url)
              VALUES ({0},{1},{2},{3})
              ON CONFLICT (job_id, parent_page_id, child_url) DO NOTHING",
            new object[] { edgeId, jobId, parentPageId, childUrl }, ct);

    /// <summary>
    /// Atomically adjusts pending_tasks by delta and increments pages_crawled if isNewPage.
    /// RETURNING in the same statement makes check-and-update atomic across workers.
    /// </summary>
    public async Task<int> AdjustCountersAndReturnPendingAsync(
        Guid jobId, int pendingDelta, bool isNewPage,
        IDbContextTransaction tx, CancellationToken ct = default)
    {
        int pagesDelta = isNewPage ? 1 : 0;

        var result = await _db.Database
            .SqlQueryRaw<int>(
                @"UPDATE crawl_jobs
                  SET pending_tasks = pending_tasks + {1},
                      pages_crawled = pages_crawled + {2}
                  WHERE id = {0}
                  RETURNING pending_tasks AS ""Value""",
                jobId, pendingDelta, pagesDelta)
            .ToListAsync(ct);

        return result.Count > 0 ? result[0] : 0;
    }
}
