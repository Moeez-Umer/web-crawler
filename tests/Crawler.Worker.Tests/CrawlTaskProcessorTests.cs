using Crawler.Contracts;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Data;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

/// <summary>
/// Unit tests for CrawlTaskProcessor.
/// Uses an in-memory SQLite substitute via a fake repository so we don't
/// need a real Postgres in unit tests (integration tests in Phase 6 will
/// use a real DB for ON CONFLICT semantics).
/// Here we fake the repository entirely to control state precisely.
/// </summary>
public class CrawlTaskProcessorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CrawlJob MakeJob(
        Guid? id = null,
        JobStatus status = JobStatus.Pending,
        int maxDepth = 2,
        int maxPages = 200,
        int pendingTasks = 1,
        int pagesCrawled = 0,
        string? failureReason = null)
        => new CrawlJob
        {
            Id = id ?? Guid.NewGuid(),
            Url = "https://example.com/",
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = status,
            PendingTasks = pendingTasks,
            PagesCrawled = pagesCrawled,
            CreatedAt = DateTime.UtcNow,
            FailureReason = failureReason
        };

    private static CrawlPageTask MakeTask(
        Guid? jobId = null,
        string url = "https://example.com/",
        int depth = 0,
        int maxDepth = 2,
        string rootHost = "example.com",
        string? parentUrl = null,
        int attempt = 1,
        Guid? messageId = null)
        => new CrawlPageTask(
            MessageId: messageId ?? Guid.NewGuid(),
            JobId: jobId ?? Guid.NewGuid(),
            Url: url,
            ParentUrl: parentUrl,
            Depth: depth,
            MaxDepth: maxDepth,
            RootHost: rootHost,
            Attempt: attempt);

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Canceled_job_returns_success_without_fetching()
    {
        var job = MakeJob(status: JobStatus.Canceled);
        var task = MakeTask(jobId: job.Id);
        var repo = new FakeRepo(job);
        var fetcher = new FailingFetcher(); // must NOT be called
        var publisher = new FakePublisher();

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), publisher, NullLogger<CrawlTaskProcessor>.Instance);

        var result = await processor.HandleAsync(task, CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.Success>();
        fetcher.WasCalled.Should().BeFalse("canceled job should skip fetching");
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_job_returns_success_without_fetching()
    {
        var task = MakeTask();
        var repo = new FakeRepo(null); // no job in DB
        var fetcher = new FailingFetcher();

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), new FakePublisher(), NullLogger<CrawlTaskProcessor>.Instance);

        var result = await processor.HandleAsync(task, CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.Success>();
        fetcher.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Transient_fetch_returns_TransientFailure()
    {
        var job = MakeJob();
        var task = MakeTask(jobId: job.Id);
        var repo = new FakeRepo(job);
        var fetcher = new FixedResultFetcher(new PageFetchResult.Transient("timeout"));

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), new FakePublisher(), NullLogger<CrawlTaskProcessor>.Instance);

        var result = await processor.HandleAsync(task, CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.TransientFailure>()
            .Which.Reason.Should().Contain("timeout");
    }

    [Fact]
    public async Task Permanent_root_failure_marks_job_Failed()
    {
        var job = MakeJob(pendingTasks: 1);
        var task = MakeTask(jobId: job.Id, depth: 0);
        var repo = new FakeRepo(job);
        var fetcher = new FixedResultFetcher(new PageFetchResult.Permanent("HTTP 404"));

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), new FakePublisher(), NullLogger<CrawlTaskProcessor>.Instance);

        var result = await processor.HandleAsync(task, CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.PermanentFailure>();
        repo.JobStatus.Should().Be(JobStatus.Failed);
        repo.FailureReason.Should().Contain("404");
    }

    [Fact]
    public async Task Permanent_child_failure_marks_job_Completed_not_Failed()
    {
        var job = MakeJob(pendingTasks: 1, pagesCrawled: 1); // root already crawled
        // depth=1 → child page
        var task = MakeTask(jobId: job.Id, depth: 1, parentUrl: "https://example.com/");
        var repo = new FakeRepo(job);
        var fetcher = new FixedResultFetcher(new PageFetchResult.Permanent("HTTP 404"));

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), new FakePublisher(), NullLogger<CrawlTaskProcessor>.Instance);

        var result = await processor.HandleAsync(task, CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.PermanentFailure>();
        // Child failure with pending=0 → Completed (not Failed)
        repo.JobStatus.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task Duplicate_delivery_is_idempotent()
    {
        var job = MakeJob(pendingTasks: 1);
        var messageId = Guid.NewGuid();
        var task = MakeTask(jobId: job.Id, messageId: messageId);
        var repo = new FakeRepo(job);
        var publisher = new FakePublisher();
        // No child links — simple page
        var fetcher = new FixedResultFetcher(new PageFetchResult.Success(
            "<html><body>Hello world</body></html>",
            new Uri("https://example.com/")));

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), publisher, NullLogger<CrawlTaskProcessor>.Instance);

        // First delivery
        var r1 = await processor.HandleAsync(task, CancellationToken.None);
        int counterAfterFirst = repo.PendingTasksAdjustmentTotal;

        // Second delivery of same message
        var r2 = await processor.HandleAsync(task, CancellationToken.None);
        int counterAfterSecond = repo.PendingTasksAdjustmentTotal;

        r1.Should().BeOfType<ConsumeResult.Success>();
        r2.Should().BeOfType<ConsumeResult.Success>();

        // Counter must NOT be decremented twice
        counterAfterSecond.Should().Be(counterAfterFirst,
            "duplicate delivery must not adjust counters a second time");

        // Page upserted only once
        repo.UpsertPageCallCount.Should().Be(1,
            "second delivery should be short-circuited by ProcessedMessages guard");
    }

    [Fact]
    public async Task MaxPages_cap_prevents_publishing_excess_children()
    {
        // Job already at the limit: pagesCrawled + pendingTasks = maxPages
        var job = MakeJob(pendingTasks: 1, pagesCrawled: 199, maxPages: 200);
        var html = @"<html><body>
            <a href='/a'>a</a><a href='/b'>b</a><a href='/c'>c</a>
        </body></html>";
        var task = MakeTask(jobId: job.Id, url: "https://example.com/");
        var repo = new FakeRepo(job);
        var publisher = new FakePublisher();
        var fetcher = new FixedResultFetcher(new PageFetchResult.Success(html, new Uri("https://example.com/")));

        var processor = new CrawlTaskProcessor(
            repo, fetcher, new LinkExtractor(), publisher, NullLogger<CrawlTaskProcessor>.Instance);

        await processor.HandleAsync(task, CancellationToken.None);

        // 199 crawled + 1 pending = 200 = maxPages → no room for new children
        publisher.Published.Should().BeEmpty("MaxPages cap must prevent any child tasks from being published");
    }
}

// ── Fakes ─────────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory fake of ICrawlRepository. Tracks state changes without a real DB.
/// </summary>
internal class FakeRepo : ICrawlRepository
{
    private readonly CrawlJob? _job;
    private readonly HashSet<Guid> _processedMessages = new();
    private readonly HashSet<string> _pages = new(); // tracks (jobId, url) as "jobId|url"

    public JobStatus JobStatus { get; private set; }
    public string? FailureReason { get; private set; }
    public int PendingTasksAdjustmentTotal { get; private set; }
    public int UpsertPageCallCount { get; private set; }

    public FakeRepo(CrawlJob? job)
    {
        _job = job;
        if (job is not null)
        {
            JobStatus = job.Status;
            FailureReason = job.FailureReason;
        }
    }

    public Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct = default)
        => Task.FromResult(_job?.Id == jobId ? _job : null);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromResult<IDbContextTransaction>(new FakeTransaction());

    public Task<bool> TryInsertProcessedMessageAsync(Guid messageId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        bool added = _processedMessages.Add(messageId);
        return Task.FromResult(added);
    }

    public Task SetJobRunningIfPendingAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        if (JobStatus == JobStatus.Pending) JobStatus = JobStatus.Running;
        return Task.CompletedTask;
    }

    public Task SetJobCompletedAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        JobStatus = JobStatus.Completed;
        return Task.CompletedTask;
    }

    public Task SetJobFailedAsync(Guid jobId, string reason, IDbContextTransaction tx, CancellationToken ct = default)
    {
        JobStatus = JobStatus.Failed;
        FailureReason = reason;
        return Task.CompletedTask;
    }

    public Task<Guid?> UpsertPageAsync(
        Guid pageId, Guid jobId, string normalizedUrl, int depth,
        double domainLinkRatio, int totalLinks, int internalLinks,
        PageStatus status, string? error, IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        string key = $"{jobId}|{normalizedUrl}";
        if (_pages.Add(key))
        {
            UpsertPageCallCount++;
            return Task.FromResult<Guid?>(pageId);
        }
        return Task.FromResult<Guid?>(null);
    }

    public Task<Guid?> GetPageIdAsync(Guid jobId, string normalizedUrl, CancellationToken ct = default)
        => Task.FromResult<Guid?>(Guid.NewGuid()); // return a stable fake id

    public Task UpsertEdgeAsync(
        Guid edgeId, Guid jobId, Guid parentPageId, string childUrl,
        IDbContextTransaction tx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdatePageStatusAsync(
        Guid jobId, string normalizedUrl, PageStatus status, string? error,
        IDbContextTransaction tx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> AdjustCountersAndReturnPendingAsync(
        Guid jobId, int pendingDelta, bool isNewPage, IDbContextTransaction tx,
        CancellationToken ct = default)
    {
        PendingTasksAdjustmentTotal += pendingDelta;
        int newPending = (_job?.PendingTasks ?? 1) + PendingTasksAdjustmentTotal;
        return Task.FromResult(Math.Max(0, newPending));
    }
}

/// <summary>No-op transaction; no real DB involved.</summary>
internal class FakeTransaction : IDbContextTransaction
{
    public Guid TransactionId => Guid.NewGuid();
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Commit() { }
    public void Rollback() { }
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Captures published messages without hitting a broker.</summary>
internal class FakePublisher : IRabbitMqPublisher
{
    public List<CrawlPageTask> Published { get; } = new();

    public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        Published.Add(task);
        return Task.CompletedTask;
    }

    public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        Published.Add(task);
        return Task.CompletedTask;
    }
}

/// <summary>Fetcher that always throws — used to assert fetch is never called.</summary>
internal class FailingFetcher : IPageFetcher
{
    public bool WasCalled { get; private set; }

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        throw new InvalidOperationException("FetchAsync should not have been called in this test");
    }
}

/// <summary>Fetcher that always returns a configured result.</summary>
internal class FixedResultFetcher : IPageFetcher
{
    private readonly PageFetchResult _result;

    public FixedResultFetcher(PageFetchResult result) => _result = result;

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
        => Task.FromResult(_result);
}

