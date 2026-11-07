using Crawler.Contracts;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Messaging;
using Crawler.Infrastructure.Persistence;
using Crawler.Worker.Services;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crawler.Worker.Tests;

/// <summary>
/// In-memory ICrawlRepository for a single job. Mirrors the SQL semantics of
/// CrawlRepository and models transactions: state is snapshotted on Begin and
/// restored on Rollback or on Dispose without Commit (as Postgres/EF would).
/// </summary>
internal sealed class FakeRepo : ICrawlRepository
{
    private readonly Guid? _jobId;
    private readonly CrawlJob? _template;
    private State _state;
    private State? _snapshot;

    /// <summary>Name of a repository method that should throw (simulated DB failure).</summary>
    public string? ThrowIn { get; set; }

    public FakeRepo(CrawlJob? job)
    {
        _template = job;
        _jobId = job?.Id;
        _state = new State
        {
            Status = job?.Status ?? JobStatus.Pending,
            FailureReason = job?.FailureReason,
            PendingTasks = job?.PendingTasks ?? 0,
            PagesCrawled = job?.PagesCrawled ?? 0
        };
    }

    // ── Observable state ────────────────────────────────────────────────────
    public JobStatus JobStatus => _state.Status;
    public string? FailureReason => _state.FailureReason;
    public int PendingTasks => _state.PendingTasks;
    public int PagesCrawled => _state.PagesCrawled;
    public int CounterUpdates => _state.CounterUpdates;
    public int ProcessedMessageCount => _state.Processed.Count;
    public int PageRowCount => _state.Pages.Count;
    public PageStatus? PageStatusOf(string url) => _state.Pages.TryGetValue(url, out var p) ? p.Status : null;
    public string? PageErrorOf(string url) => _state.Pages.TryGetValue(url, out var p) ? p.Error : null;

    public void SeedPage(string url, PageStatus status, int depth = 1)
        => _state.Pages[url] = new FakePage(Guid.NewGuid(), status, null, depth);

    // ── Transactions ────────────────────────────────────────────────────────
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        _snapshot = _state.Clone();
        return Task.FromResult<IDbContextTransaction>(new FakeTransaction(this));
    }

    internal void CommitTx() => _snapshot = null;

    internal void RollbackTx()
    {
        if (_snapshot is null) return;
        _state = _snapshot;
        _snapshot = null;
    }

    // ── ICrawlRepository ────────────────────────────────────────────────────
    public Task<CrawlJob?> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        MaybeThrow(nameof(GetJobAsync));
        if (_template is null || jobId != _jobId) return Task.FromResult<CrawlJob?>(null);

        return Task.FromResult<CrawlJob?>(new CrawlJob
        {
            Id = _template.Id,
            Url = _template.Url,
            MaxDepth = _template.MaxDepth,
            MaxPages = _template.MaxPages,
            CreatedAt = _template.CreatedAt,
            Status = _state.Status,
            FailureReason = _state.FailureReason,
            PendingTasks = _state.PendingTasks,
            PagesCrawled = _state.PagesCrawled
        });
    }

    public Task<bool> TryInsertProcessedMessageAsync(Guid messageId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(TryInsertProcessedMessageAsync));
        return Task.FromResult(_state.Processed.Add(messageId));
    }

    public Task SetJobRunningIfPendingAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(SetJobRunningIfPendingAsync));
        if (_state.Status == JobStatus.Pending) _state.Status = JobStatus.Running;
        return Task.CompletedTask;
    }

    public Task SetJobCompletedAsync(Guid jobId, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(SetJobCompletedAsync));
        if (IsActive) _state.Status = JobStatus.Completed;
        return Task.CompletedTask;
    }

    public Task SetJobFailedAsync(Guid jobId, string reason, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(SetJobFailedAsync));
        if (IsActive)
        {
            _state.Status = JobStatus.Failed;
            _state.FailureReason = reason;
        }
        return Task.CompletedTask;
    }

    public Task<Guid?> UpsertPageAsync(
        Guid pageId, Guid jobId, string normalizedUrl, int depth,
        double domainLinkRatio, int totalLinks, int internalLinks,
        PageStatus status, string? error, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(UpsertPageAsync));
        if (_state.Pages.ContainsKey(normalizedUrl)) return Task.FromResult<Guid?>(null);

        _state.Pages[normalizedUrl] = new FakePage(pageId, status, error, depth);
        return Task.FromResult<Guid?>(pageId);
    }

    public Task<Guid?> RecordPageResultAsync(
        Guid pageId, Guid jobId, string normalizedUrl, int depth,
        double domainLinkRatio, int totalLinks, int internalLinks,
        PageStatus status, string? error, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(RecordPageResultAsync));
        if (!_state.Pages.TryGetValue(normalizedUrl, out var existing))
        {
            _state.Pages[normalizedUrl] = new FakePage(pageId, status, error, depth);
            return Task.FromResult<Guid?>(pageId);
        }
        if (existing.Status != PageStatus.Pending) return Task.FromResult<Guid?>(null);

        _state.Pages[normalizedUrl] = existing with { Status = status, Error = error };
        return Task.FromResult<Guid?>(existing.Id);
    }

    public Task UpdatePageStatusAsync(
        Guid jobId, string normalizedUrl, PageStatus status, string? error,
        IDbContextTransaction tx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Guid?> GetPageIdAsync(Guid jobId, string normalizedUrl, CancellationToken ct = default)
        => Task.FromResult(_state.Pages.TryGetValue(normalizedUrl, out var p) ? p.Id : (Guid?)null);

    public Task UpsertEdgeAsync(
        Guid edgeId, Guid jobId, Guid parentPageId, string childUrl,
        IDbContextTransaction tx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> AdjustCountersAndReturnPendingAsync(
        Guid jobId, int pendingDelta, bool isNewPage, IDbContextTransaction tx, CancellationToken ct = default)
    {
        MaybeThrow(nameof(AdjustCountersAndReturnPendingAsync));
        // No clamping: a double decrement must be visible to the tests.
        _state.PendingTasks += pendingDelta;
        if (isNewPage) _state.PagesCrawled++;
        _state.CounterUpdates++;
        return Task.FromResult(_state.PendingTasks);
    }

    private bool IsActive => _state.Status is JobStatus.Pending or JobStatus.Running;

    private void MaybeThrow(string method)
    {
        if (ThrowIn == method)
            throw new InvalidOperationException($"Simulated DB failure in {method}");
    }

    private sealed record FakePage(Guid Id, PageStatus Status, string? Error, int Depth);

    private sealed class State
    {
        public JobStatus Status;
        public string? FailureReason;
        public int PendingTasks;
        public int PagesCrawled;
        public int CounterUpdates;
        public HashSet<Guid> Processed = new();
        public Dictionary<string, FakePage> Pages = new();

        public State Clone() => new()
        {
            Status = Status,
            FailureReason = FailureReason,
            PendingTasks = PendingTasks,
            PagesCrawled = PagesCrawled,
            CounterUpdates = CounterUpdates,
            Processed = new HashSet<Guid>(Processed),
            Pages = new Dictionary<string, FakePage>(Pages)
        };
    }
}

/// <summary>Transaction bound to a FakeRepo; disposing without commit rolls back.</summary>
internal sealed class FakeTransaction : IDbContextTransaction
{
    private readonly FakeRepo _repo;
    private bool _completed;

    public FakeTransaction(FakeRepo repo) => _repo = repo;

    public Guid TransactionId { get; } = Guid.NewGuid();

    public void Commit() { _repo.CommitTx(); _completed = true; }
    public void Rollback() { _repo.RollbackTx(); _completed = true; }
    public Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
    public Task RollbackAsync(CancellationToken cancellationToken = default) { Rollback(); return Task.CompletedTask; }

    public void Dispose()
    {
        if (!_completed) Rollback();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Captures published messages without hitting a broker.</summary>
internal sealed class FakePublisher : IRabbitMqPublisher
{
    public List<CrawlPageTask> Published { get; } = new();
    public List<CrawlPageTask> Retried { get; } = new();

    public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        Published.Add(task);
        return Task.CompletedTask;
    }

    public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        Retried.Add(task);
        return Task.CompletedTask;
    }
}

/// <summary>Records how a delivery was settled.</summary>
internal sealed class FakeSettler : IDeliverySettler
{
    public int Acks { get; private set; }
    public int DlqNacks { get; private set; }

    public void Ack() => Acks++;
    public void NackToDlq() => DlqNacks++;
}

/// <summary>Fetcher that throws — used to assert fetch is never called.</summary>
internal sealed class FailingFetcher : IPageFetcher
{
    public bool WasCalled { get; private set; }

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        throw new InvalidOperationException("FetchAsync should not have been called in this test");
    }
}

/// <summary>Fetcher that always returns a configured result.</summary>
internal sealed class FixedResultFetcher : IPageFetcher
{
    private readonly PageFetchResult _result;

    public FixedResultFetcher(PageFetchResult result) => _result = result;

    public Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default)
        => Task.FromResult(_result);
}

