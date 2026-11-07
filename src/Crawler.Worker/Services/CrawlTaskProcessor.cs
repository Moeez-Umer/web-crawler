using Crawler.Contracts;
using Crawler.Domain;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Crawler.Worker.Services;

/// <summary>
/// Orchestrates processing of a single CrawlPageTask.
///
/// Idempotency key for EVERY path that touches the counter (success, permanent
/// failure, dead-letter): processed_messages.message_id (PK). MessageId is stable
/// across retries (retries republish the same task with Attempt+1), and transient
/// failures never insert it, so each published message decrements pending_tasks
/// exactly once, whichever path finishes it.
///
/// Success-path transaction (step e):
///   1. INSERT processed_messages ON CONFLICT DO NOTHING  → 0 rows = duplicate → ROLLBACK
///   2. Record this page (insert, or upgrade its Pending placeholder) → isNewPage
///   3. INSERT page_edge (parent→this) ON CONFLICT DO NOTHING
///   4. INSERT child placeholders (status Pending) ON CONFLICT DO NOTHING → capture NEW ones
///   5. INSERT child edges (this→child) ON CONFLICT DO NOTHING
///   6. UPDATE crawl_jobs counters RETURNING pending_tasks
///   7. pending_tasks == 0 → Completed
///   COMMIT, then publish children (dual-write window — D-009)
/// </summary>
public class CrawlTaskProcessor : ICrawlTaskHandler
{
    private readonly ICrawlRepository _repo;
    private readonly IPageFetcher _fetcher;
    private readonly ILinkExtractor _extractor;
    private readonly IRabbitMqPublisher _publisher;
    private readonly ILogger<CrawlTaskProcessor> _logger;

    public CrawlTaskProcessor(
        ICrawlRepository repo,
        IPageFetcher fetcher,
        ILinkExtractor extractor,
        IRabbitMqPublisher publisher,
        ILogger<CrawlTaskProcessor> logger)
    {
        _repo = repo;
        _fetcher = fetcher;
        _extractor = extractor;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<ConsumeResult> HandleAsync(CrawlPageTask task, CancellationToken cancellationToken)
    {
        using var scope = BeginTaskScope(task);

        // ── a. Canceled / missing job → ACK and skip ─────────────────────────
        var job = await _repo.GetJobAsync(task.JobId, cancellationToken);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} not found — skipping task", task.JobId);
            return new ConsumeResult.Success();
        }
        if (job.Status == JobStatus.Canceled)
        {
            _logger.LogInformation("Job {JobId} is Canceled — ACKing and skipping", task.JobId);
            return new ConsumeResult.Success();
        }

        // ── b. Pending → Running (WHERE status='Pending' makes it idempotent) ──
        await using (var txRunning = await _repo.BeginTransactionAsync(cancellationToken))
        {
            await _repo.SetJobRunningIfPendingAsync(task.JobId, txRunning, cancellationToken);
            await txRunning.CommitAsync(cancellationToken);
        }

        // ── c. Fetch ─────────────────────────────────────────────────────────
        var fetchResult = await _fetcher.FetchAsync(task.Url, cancellationToken);

        if (fetchResult is PageFetchResult.Transient transient)
        {
            _logger.LogWarning("Transient fetch failure for {Url}: {Reason}", task.Url, transient.Reason);
            return new ConsumeResult.TransientFailure(transient.Reason);
        }

        if (fetchResult is PageFetchResult.Permanent permanent)
        {
            _logger.LogWarning("Permanent fetch failure for {Url}: {Reason}", task.Url, permanent.Reason);
            await RecordFailedPageAsync(task, permanent.Reason, cancellationToken);
            return new ConsumeResult.PermanentFailure(permanent.Reason);
        }

        var fetchSuccess = (PageFetchResult.Success)fetchResult;

        // ── d. Extract links + ratio ─────────────────────────────────────────
        var allLinks = _extractor.Extract(fetchSuccess.Html, fetchSuccess.FinalUrl);
        var ratio = DomainLinkRatioCalculator.Calculate(allLinks, task.RootHost);

        var childCandidates = allLinks
            .Where(l => IsInternalLink(l, task.RootHost) && task.Depth + 1 <= task.MaxDepth)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Fetched {Url}: {TotalLinks} links, {Internal} internal, ratio {Ratio:F3}, {Candidates} child candidates",
            task.Url, ratio.TotalLinks, ratio.InternalLinks, ratio.Ratio, childCandidates.Count);

        // ── e. Single DB transaction ──────────────────────────────────────────
        await using var mainTx = await _repo.BeginTransactionAsync(cancellationToken);

        // 1. Idempotency guard
        if (!await _repo.TryInsertProcessedMessageAsync(task.MessageId, mainTx, cancellationToken))
        {
            await mainTx.RollbackAsync(cancellationToken);
            _logger.LogInformation("Duplicate delivery of {MessageId} — ACKing idempotently", task.MessageId);
            return new ConsumeResult.Success();
        }

        // 2. Record this page: inserts the root row, or upgrades the Pending placeholder
        //    that the parent created when it discovered this URL.
        var recordedId = await _repo.RecordPageResultAsync(
            Guid.NewGuid(), task.JobId, task.Url,
            task.Depth, ratio.Ratio, ratio.TotalLinks, ratio.InternalLinks,
            PageStatus.Crawled, error: null, mainTx, cancellationToken);

        bool isNewPage = recordedId.HasValue;
        var pageId = recordedId ?? await _repo.GetPageIdAsync(task.JobId, task.Url, cancellationToken);

        // 3. Edge parent → this (normally already written by the parent; harmless if so)
        if (!string.IsNullOrEmpty(task.ParentUrl) && pageId.HasValue)
        {
            var parentId = await _repo.GetPageIdAsync(task.JobId, task.ParentUrl, cancellationToken);
            if (parentId.HasValue)
            {
                await _repo.UpsertEdgeAsync(Guid.NewGuid(), task.JobId, parentId.Value, task.Url,
                    mainTx, cancellationToken);
            }
        }

        // 4+5. Child placeholders + edges; MaxPages guard (D-016)
        var newlyInserted = new List<string>();
        if (pageId.HasValue)
        {
            foreach (var childUrl in childCandidates)
            {
                if (job.PagesCrawled + job.PendingTasks + newlyInserted.Count >= job.MaxPages)
                    break;

                var childInserted = await _repo.UpsertPageAsync(
                    Guid.NewGuid(), task.JobId, childUrl,
                    task.Depth + 1, 0.0, 0, 0,
                    PageStatus.Pending, error: null, mainTx, cancellationToken);

                if (childInserted.HasValue)
                {
                    await _repo.UpsertEdgeAsync(Guid.NewGuid(), task.JobId, pageId.Value, childUrl,
                        mainTx, cancellationToken);
                    newlyInserted.Add(childUrl);
                }
            }
        }

        // 6. +N children, −1 for this task
        int newPending = await _repo.AdjustCountersAndReturnPendingAsync(
            task.JobId, newlyInserted.Count - 1, isNewPage, mainTx, cancellationToken);

        // 7. Completion
        if (newPending <= 0)
        {
            _logger.LogInformation("Job {JobId} complete — marking Completed", task.JobId);
            await _repo.SetJobCompletedAsync(task.JobId, mainTx, cancellationToken);
        }

        await mainTx.CommitAsync(cancellationToken);

        // ── f. Publish AFTER commit (dual-write window — D-009) ──────────────
        foreach (var childUrl in newlyInserted)
        {
            await _publisher.PublishCrawlTaskAsync(new CrawlPageTask(
                MessageId: Guid.NewGuid(),
                JobId: task.JobId,
                Url: childUrl,
                ParentUrl: task.Url,
                Depth: task.Depth + 1,
                MaxDepth: task.MaxDepth,
                RootHost: task.RootHost,
                Attempt: 1
            ), cancellationToken);
        }

        return new ConsumeResult.Success();
    }

    /// <summary>
    /// Finalizes a task that is about to be dead-lettered. Returns only after commit;
    /// any exception propagates so the consumer does not nack (D-018).
    /// </summary>
    public async Task OnDeadLetteredAsync(CrawlPageTask task, string reason, CancellationToken cancellationToken)
    {
        using var scope = BeginTaskScope(task);
        bool finalized = await RecordFailedPageAsync(task, $"Dead-lettered: {reason}", cancellationToken);

        if (finalized)
            _logger.LogWarning("Dead-letter finalization committed for {Url}: {Reason}", task.Url, reason);
    }

    // ── Shared failure finalization (permanent failure + dead-letter) ──────────

    /// <summary>
    /// One transaction: guard → page Failed (only if absent/Pending) → pending_tasks − 1 →
    /// job finalization. Returns false if nothing was written (duplicate, or job gone/canceled).
    /// </summary>
    private async Task<bool> RecordFailedPageAsync(CrawlPageTask task, string reason, CancellationToken ct)
    {
        await using var tx = await _repo.BeginTransactionAsync(ct);

        // Same guard as the success path: a messageId that already finished via ANY path
        // must not decrement the counter again.
        if (!await _repo.TryInsertProcessedMessageAsync(task.MessageId, tx, ct))
        {
            await tx.RollbackAsync(ct);
            _logger.LogInformation("Message {MessageId} already finalized — no side effects", task.MessageId);
            return false;
        }

        // Canceled jobs stop tracking the counter (HandleAsync skips them too).
        var job = await _repo.GetJobAsync(task.JobId, ct);
        if (job is null || job.Status == JobStatus.Canceled)
        {
            await tx.RollbackAsync(ct);
            _logger.LogInformation("Job {JobId} missing or Canceled — skipping finalization", task.JobId);
            return false;
        }

        // Does nothing if the row is already Crawled: a late failure never overwrites a success.
        await _repo.RecordPageResultAsync(
            Guid.NewGuid(), task.JobId, task.Url, task.Depth,
            0.0, 0, 0, PageStatus.Failed, reason, tx, ct);

        int newPending = await _repo.AdjustCountersAndReturnPendingAsync(
            task.JobId, pendingDelta: -1, isNewPage: false, tx, ct);

        if (task.Depth == 0)
        {
            // Root failed → job Failed, even if other tasks are still in flight.
            // The status filter in SetJobCompletedAsync stops a later child from flipping it back.
            _logger.LogError("Root page failed ({Reason}) — marking job Failed", reason);
            await _repo.SetJobFailedAsync(task.JobId, reason, tx, ct);
        }
        else if (newPending <= 0)
        {
            _logger.LogInformation("Job {JobId} complete (last task failed, pending=0)", task.JobId);
            await _repo.SetJobCompletedAsync(task.JobId, tx, ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private IDisposable? BeginTaskScope(CrawlPageTask task) => _logger.BeginScope(new Dictionary<string, object>
    {
        ["jobId"] = task.JobId,
        ["messageId"] = task.MessageId,
        ["correlationId"] = task.JobId, // jobId doubles as correlationId per SPEC
        ["url"] = task.Url,
        ["depth"] = task.Depth,
        ["attempt"] = task.Attempt
    });

    private static bool IsInternalLink(string url, string rootHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, rootHost, StringComparison.OrdinalIgnoreCase);
    }
}
