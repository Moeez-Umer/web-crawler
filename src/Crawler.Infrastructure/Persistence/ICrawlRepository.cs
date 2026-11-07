using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crawler.Infrastructure.Persistence;

public interface ICrawlRepository
{
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

    Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    Task<bool> TryInsertProcessedMessageAsync(Guid messageId, IDbContextTransaction tx, CancellationToken ct = default);

    Task SetJobRunningIfPendingAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default);

    Task SetJobCompletedAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default);

    Task SetJobFailedAsync(Guid jobId, string reason, IDbContextTransaction tx, CancellationToken ct = default);

    Task<Guid?> UpsertPageAsync(
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
        CancellationToken ct = default);

    /// <summary>
    /// Insert the page row, or upgrade it only if it is still a Pending placeholder.
    /// Returns the id if a row was written; null if the row was already Crawled/Failed.
    /// </summary>
    Task<Guid?> RecordPageResultAsync(
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
        CancellationToken ct = default);

    Task UpdatePageStatusAsync(
        Guid jobId,
        string normalizedUrl,
        PageStatus status,
        string? error,
        IDbContextTransaction tx,
        CancellationToken ct = default);

    Task<Guid?> GetPageIdAsync(Guid jobId, string normalizedUrl, CancellationToken ct = default);

    Task UpsertEdgeAsync(
        Guid edgeId,
        Guid jobId,
        Guid parentPageId,
        string childUrl,
        IDbContextTransaction tx,
        CancellationToken ct = default);

    Task<int> AdjustCountersAndReturnPendingAsync(
        Guid jobId,
        int pendingDelta,
        bool isNewPage,
        IDbContextTransaction tx,
        CancellationToken ct = default);
}

