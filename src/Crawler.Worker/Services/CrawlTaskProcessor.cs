using Crawler.Contracts;
using Crawler.Domain;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Crawler.Worker.Services;

/// <summary>
/// Orchestrates processing of a single CrawlPageTask.
/// Called by RabbitMqConsumer for every successfully deserialized message.
///
/// Transaction boundary (step e) — all inside ONE transaction:
///   1. INSERT processed_messages ON CONFLICT DO NOTHING  → 0 rows = duplicate → ROLLBACK, ACK
///   2. INSERT crawled_pages ON CONFLICT DO NOTHING       → capture inserted id (or lookup existing)
///   3. INSERT page_edge (parent→this) ON CONFLICT DO NOTHING
///   4. INSERT child page rows ON CONFLICT DO NOTHING     → capture which are NEW
///   5. INSERT child edge rows (this→child) ON CONFLICT DO NOTHING
///   6. UPDATE crawl_jobs counters RETURNING pending_tasks
///   7. If pending_tasks == 0 → UPDATE status = Completed/Failed in same statement
///   COMMIT
///   After commit: publish child tasks (dual-write window — D-009)
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
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["jobId"] = task.JobId,
            ["messageId"] = task.MessageId,
            ["correlationId"] = task.JobId, // jobId doubles as correlationId per SPEC
            ["url"] = task.Url,
            ["depth"] = task.Depth,
            ["attempt"] = task.Attempt
        });

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

        // ── b. Transition Pending → Running (WHERE status='Pending' makes it idempotent) ──
        // This is a small separate transaction; the main write transaction is step e.
        await using (var txRunning = await _repo.BeginTransactionAsync(cancellationToken))
        {
            await _repo.SetJobRunningIfPendingAsync(task.JobId, txRunning, cancellationToken);
            await txRunning.CommitAsync(cancellationToken);
        }

        // ── c. Fetch the page ────────────────────────────────────────────────
        var fetchResult = await _fetcher.FetchAsync(task.Url, cancellationToken);

        if (fetchResult is PageFetchResult.Transient transient)
        {
            _logger.LogWarning("Transient fetch failure for {Url}: {Reason}", task.Url, transient.Reason);
            return new ConsumeResult.TransientFailure(transient.Reason);
        }

        if (fetchResult is PageFetchResult.Permanent permanent)
        {
            _logger.LogWarning("Permanent fetch failure for {Url}: {Reason}", task.Url, permanent.Reason);
            return await HandlePermanentPageFailureAsync(task, permanent.Reason, job, cancellationToken);
        }

        var fetchSuccess = (PageFetchResult.Success)fetchResult;

        // ── d. Extract links + compute domain-link ratio ─────────────────────
        var allLinks = _extractor.Extract(fetchSuccess.Html, fetchSuccess.FinalUrl);
        var ratio = DomainLinkRatioCalculator.Calculate(allLinks, task.RootHost);

        // Children: same host, within maxDepth, de-duplicated
        var childCandidates = allLinks
            .Where(l => IsInternalLink(l, task.RootHost) && task.Depth + 1 <= task.MaxDepth)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Fetched {Url}: {TotalLinks} links, {Internal} internal, ratio {Ratio:F3}, {Candidates} child candidates",
            task.Url, ratio.TotalLinks, ratio.InternalLinks, ratio.Ratio, childCandidates.Count);

        // ── e. Single DB transaction ──────────────────────────────────────────
        await using var mainTx = await _repo.BeginTransactionAsync(cancellationToken);

        // 1. Idempotency guard: if already processed, ACK without side effects
        bool isFirst = await _repo.TryInsertProcessedMessageAsync(task.MessageId, mainTx, cancellationToken);
        if (!isFirst)
        {
            await mainTx.RollbackAsync(cancellationToken);
            _logger.LogInformation("Duplicate delivery of {MessageId} — ACKing idempotently", task.MessageId);
            return new ConsumeResult.Success();
        }

        // 2. Upsert the crawled page; capture whether it's new
        var newPageId = Guid.NewGuid();
        var insertedId = await _repo.UpsertPageAsync(
            newPageId, task.JobId, task.Url,
            task.Depth, ratio.Ratio, ratio.TotalLinks, ratio.InternalLinks,
            PageStatus.Crawled, error: null, mainTx, cancellationToken);

        bool isNewPage = insertedId.HasValue;
        // The page row's id — either the one we just inserted or the pre-existing one.
        var resolvedPageId = insertedId ?? await _repo.GetPageIdAsync(task.JobId, task.Url, cancellationToken);

        // 3. Upsert edge: parent → this page
        if (!string.IsNullOrEmpty(task.ParentUrl) && resolvedPageId.HasValue)
        {
            var parentId = await _repo.GetPageIdAsync(task.JobId, task.ParentUrl, cancellationToken);
            if (parentId.HasValue)
            {
                await _repo.UpsertEdgeAsync(
                    Guid.NewGuid(), task.JobId, parentId.Value, task.Url,
                    mainTx, cancellationToken);
            }
        }

        // 4+5. Insert child pages and edges; MaxPages guard applied here.
        var newlyInserted = new List<string>();

        if (resolvedPageId.HasValue)
        {
            foreach (var childUrl in childCandidates)
            {
                // MaxPages guard: stop once crawled + pending + queued-new-children >= MaxPages
                // We use the snapshot values from the job row; they are updated atomically below.
                if (job.PagesCrawled + job.PendingTasks + newlyInserted.Count >= job.MaxPages)
                    break;

                var childRowId = Guid.NewGuid();
                var childInserted = await _repo.UpsertPageAsync(
                    childRowId, task.JobId, childUrl,
                    task.Depth + 1, 0.0, 0, 0,
                    PageStatus.Crawled, error: null, mainTx, cancellationToken);

                if (childInserted.HasValue)
                {
                    // Insert edge: this page → child
                    await _repo.UpsertEdgeAsync(
                        Guid.NewGuid(), task.JobId, resolvedPageId.Value, childUrl,
                        mainTx, cancellationToken);
                    newlyInserted.Add(childUrl);
                }
            }
        }

        // 6. Adjust counters:
        //      pending_tasks += newlyInserted.Count - 1  (add N children, remove 1 for this task)
        //      pages_crawled += 1  if isNewPage
        int pendingDelta = newlyInserted.Count - 1;
        int newPending = await _repo.AdjustCountersAndReturnPendingAsync(
            task.JobId, pendingDelta, isNewPage, mainTx, cancellationToken);

        // 7. Completion detection: pending_tasks == 0 means no more in-flight tasks
        if (newPending <= 0)
        {
            _logger.LogInformation("Job {JobId} complete — marking Completed", task.JobId);
            await _repo.SetJobCompletedAsync(task.JobId, mainTx, cancellationToken);
        }

        await mainTx.CommitAsync(cancellationToken);

        // ── f. Publish child tasks AFTER commit (dual-write window — D-009) ──
        // A crash here loses child messages → job stalls with pending_tasks > 0.
        // The transactional outbox pattern is the correct fix (see D-009, OI-2).
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

    // ── Permanent page failure handler ────────────────────────────────────────

    private async Task<ConsumeResult> HandlePermanentPageFailureAsync(
        CrawlPageTask task,
        string reason,
        Crawler.Domain.Entities.CrawlJob job,
        CancellationToken ct)
    {
        await using var tx = await _repo.BeginTransactionAsync(ct);

        // Idempotency guard on the failure path too
        bool isFirst = await _repo.TryInsertProcessedMessageAsync(task.MessageId, tx, ct);
        if (!isFirst)
        {
            await tx.RollbackAsync(ct);
            return new ConsumeResult.PermanentFailure(reason);
        }

        // Upsert page as Failed (depth, ratio = 0)
        await _repo.UpsertPageAsync(
            Guid.NewGuid(), task.JobId, task.Url, task.Depth,
            0.0, 0, 0, PageStatus.Failed, reason, tx, ct);

        // Decrement pending_tasks (this task finishes, adds no children)
        int newPending = await _repo.AdjustCountersAndReturnPendingAsync(
            task.JobId, pendingDelta: -1, isNewPage: false, tx, ct);

        bool isRoot = task.Depth == 0;

        if (newPending <= 0)
        {
            if (isRoot)
            {
                _logger.LogError("Root page failed permanently ({Reason}) — marking job Failed", reason);
                await _repo.SetJobFailedAsync(task.JobId, reason, tx, ct);
            }
            else
            {
                _logger.LogInformation("Job {JobId} complete (child permanent failure, pending=0)", task.JobId);
                await _repo.SetJobCompletedAsync(task.JobId, tx, ct);
            }
        }
        else if (isRoot)
        {
            // Root failed but other tasks still in-flight (edge case: shouldn't happen with fan-out
            // from a single root task, but guard anyway).
            _logger.LogError("Root page failed permanently ({Reason})", reason);
            await _repo.SetJobFailedAsync(task.JobId, reason, tx, ct);
        }

        await tx.CommitAsync(ct);
        return new ConsumeResult.PermanentFailure(reason);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool IsInternalLink(string url, string rootHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, rootHost, StringComparison.OrdinalIgnoreCase);
    }
}
