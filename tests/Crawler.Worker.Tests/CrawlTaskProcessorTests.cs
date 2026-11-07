using Crawler.Contracts;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

/// <summary>
/// Unit tests for CrawlTaskProcessor with a transactional in-memory repository
/// (see TestFakes.cs). Real-Postgres ON CONFLICT behaviour is covered in Phase 6.
/// </summary>
public class CrawlTaskProcessorTests
{
    private const string Root = "https://example.com/";
    private const string Child = "https://example.com/about";
    private const string NoLinksHtml = "<html><body>Hello world</body></html>";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CrawlJob MakeJob(
        JobStatus status = JobStatus.Running,
        int maxDepth = 2,
        int maxPages = 200,
        int pendingTasks = 1,
        int pagesCrawled = 0)
        => new()
        {
            Id = Guid.NewGuid(),
            Url = Root,
            MaxDepth = maxDepth,
            MaxPages = maxPages,
            Status = status,
            PendingTasks = pendingTasks,
            PagesCrawled = pagesCrawled,
            CreatedAt = DateTime.UtcNow
        };

    private static CrawlPageTask MakeTask(
        Guid jobId,
        string url = Root,
        int depth = 0,
        string? parentUrl = null,
        Guid? messageId = null)
        => new(
            MessageId: messageId ?? Guid.NewGuid(),
            JobId: jobId,
            Url: url,
            ParentUrl: parentUrl,
            Depth: depth,
            MaxDepth: 2,
            RootHost: "example.com",
            Attempt: 1);

    private static CrawlTaskProcessor MakeProcessor(FakeRepo repo, IPageFetcher fetcher, FakePublisher? publisher = null)
        => new(repo, fetcher, new LinkExtractor(), publisher ?? new FakePublisher(),
            NullLogger<CrawlTaskProcessor>.Instance);

    private static FixedResultFetcher Html(string html, string url = Root)
        => new(new PageFetchResult.Success(html, new Uri(url)));

    // ── Skip paths ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Canceled_job_returns_success_without_fetching()
    {
        var job = MakeJob(status: JobStatus.Canceled);
        var repo = new FakeRepo(job);
        var fetcher = new FailingFetcher();
        var publisher = new FakePublisher();

        var result = await MakeProcessor(repo, fetcher, publisher).HandleAsync(MakeTask(job.Id), CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.Success>();
        fetcher.WasCalled.Should().BeFalse("canceled job should skip fetching");
        publisher.Published.Should().BeEmpty();
        repo.PendingTasks.Should().Be(1);
    }

    [Fact]
    public async Task Missing_job_returns_success_without_fetching()
    {
        var repo = new FakeRepo(null);
        var fetcher = new FailingFetcher();

        var result = await MakeProcessor(repo, fetcher).HandleAsync(MakeTask(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.Success>();
        fetcher.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Transient_fetch_returns_TransientFailure_and_does_not_consume_the_guard()
    {
        var job = MakeJob();
        var repo = new FakeRepo(job);

        var result = await MakeProcessor(repo, new FixedResultFetcher(new PageFetchResult.Transient("timeout")))
            .HandleAsync(MakeTask(job.Id), CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.TransientFailure>().Which.Reason.Should().Contain("timeout");
        repo.ProcessedMessageCount.Should().Be(0, "transient failures must leave the messageId free for a later path");
        repo.PendingTasks.Should().Be(1);
    }

    // ── Permanent failures ────────────────────────────────────────────────────

    [Fact]
    public async Task Permanent_root_failure_marks_job_Failed()
    {
        var job = MakeJob(pendingTasks: 1);
        var repo = new FakeRepo(job);

        var result = await MakeProcessor(repo, new FixedResultFetcher(new PageFetchResult.Permanent("HTTP 404")))
            .HandleAsync(MakeTask(job.Id, depth: 0), CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.PermanentFailure>();
        repo.JobStatus.Should().Be(JobStatus.Failed);
        repo.FailureReason.Should().Contain("404");
        repo.PendingTasks.Should().Be(0);
    }

    [Fact]
    public async Task Permanent_child_failure_marks_job_Completed_not_Failed()
    {
        var job = MakeJob(pendingTasks: 1, pagesCrawled: 1);
        var repo = new FakeRepo(job);
        repo.SeedPage(Child, PageStatus.Pending);

        var result = await MakeProcessor(repo, new FixedResultFetcher(new PageFetchResult.Permanent("HTTP 404")))
            .HandleAsync(MakeTask(job.Id, Child, depth: 1, parentUrl: Root), CancellationToken.None);

        result.Should().BeOfType<ConsumeResult.PermanentFailure>();
        repo.JobStatus.Should().Be(JobStatus.Completed);
        repo.PageStatusOf(Child).Should().Be(PageStatus.Failed);
    }

    // ── Success path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_delivery_is_idempotent()
    {
        var job = MakeJob(pendingTasks: 1);
        var repo = new FakeRepo(job);
        var processor = MakeProcessor(repo, Html(NoLinksHtml));
        var task = MakeTask(job.Id);

        var r1 = await processor.HandleAsync(task, CancellationToken.None);
        var r2 = await processor.HandleAsync(task, CancellationToken.None);

        r1.Should().BeOfType<ConsumeResult.Success>();
        r2.Should().BeOfType<ConsumeResult.Success>();
        repo.CounterUpdates.Should().Be(1, "duplicate delivery must not adjust counters a second time");
        repo.PendingTasks.Should().Be(0);
        repo.PageRowCount.Should().Be(1);
    }

    [Fact]
    public async Task Child_task_upgrades_its_Pending_placeholder_to_Crawled()
    {
        var job = MakeJob(pendingTasks: 1);
        var repo = new FakeRepo(job);
        var publisher = new FakePublisher();

        await MakeProcessor(repo, Html("<a href='/about'>a</a>"), publisher)
            .HandleAsync(MakeTask(job.Id), CancellationToken.None);

        repo.PageStatusOf(Child).Should().Be(PageStatus.Pending, "children are placeholders until their own task runs");
        repo.PendingTasks.Should().Be(1);

        var childTask = publisher.Published.Should().ContainSingle().Subject;
        await MakeProcessor(repo, Html(NoLinksHtml, Child)).HandleAsync(childTask, CancellationToken.None);

        repo.PageStatusOf(Child).Should().Be(PageStatus.Crawled);
        repo.PagesCrawled.Should().Be(2);
        repo.PendingTasks.Should().Be(0);
        repo.JobStatus.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task MaxPages_cap_prevents_publishing_excess_children()
    {
        var job = MakeJob(pendingTasks: 1, pagesCrawled: 199, maxPages: 200);
        var publisher = new FakePublisher();

        await MakeProcessor(new FakeRepo(job), Html("<a href='/a'>a</a><a href='/b'>b</a><a href='/c'>c</a>"), publisher)
            .HandleAsync(MakeTask(job.Id), CancellationToken.None);

        publisher.Published.Should().BeEmpty("MaxPages cap must prevent any child tasks from being published");
    }

    // ── Dead-letter finalization ──────────────────────────────────────────────

    [Fact]
    public async Task DeadLetter_child_marks_page_Failed_decrements_counter_and_completes_job()
    {
        var job = MakeJob(pendingTasks: 1, pagesCrawled: 1);
        var repo = new FakeRepo(job);
        repo.SeedPage(Child, PageStatus.Pending);

        await MakeProcessor(repo, new FailingFetcher())
            .OnDeadLetteredAsync(MakeTask(job.Id, Child, depth: 1, parentUrl: Root),
                "Retries exhausted after 5 attempt(s): HTTP 503", CancellationToken.None);

        repo.PageStatusOf(Child).Should().Be(PageStatus.Failed);
        repo.PageErrorOf(Child).Should().Contain("Dead-lettered").And.Contain("HTTP 503");
        repo.PendingTasks.Should().Be(0);
        repo.JobStatus.Should().Be(JobStatus.Completed, "only a child failed");
    }

    [Fact]
    public async Task DeadLetter_child_with_other_tasks_in_flight_keeps_job_Running()
    {
        var job = MakeJob(pendingTasks: 3);
        var repo = new FakeRepo(job);

        await MakeProcessor(repo, new FailingFetcher())
            .OnDeadLetteredAsync(MakeTask(job.Id, Child, depth: 1), "Poison: bad", CancellationToken.None);

        repo.PendingTasks.Should().Be(2);
        repo.JobStatus.Should().Be(JobStatus.Running);
    }

    [Fact]
    public async Task DeadLetter_root_marks_job_Failed_with_reason()
    {
        var job = MakeJob(pendingTasks: 1);
        var repo = new FakeRepo(job);

        await MakeProcessor(repo, new FailingFetcher())
            .OnDeadLetteredAsync(MakeTask(job.Id, depth: 0), "Retries exhausted after 5 attempt(s): timeout",
                CancellationToken.None);

        repo.JobStatus.Should().Be(JobStatus.Failed);
        repo.FailureReason.Should().Contain("Retries exhausted").And.Contain("timeout");
        repo.PendingTasks.Should().Be(0);
    }

    [Fact]
    public async Task DeadLetter_does_not_overwrite_a_Crawled_page()
    {
        var job = MakeJob(pendingTasks: 2);
        var repo = new FakeRepo(job);
        repo.SeedPage(Child, PageStatus.Crawled);

        await MakeProcessor(repo, new FailingFetcher())
            .OnDeadLetteredAsync(MakeTask(job.Id, Child, depth: 1), "Poison: bad", CancellationToken.None);

        repo.PageStatusOf(Child).Should().Be(PageStatus.Crawled);
        repo.PendingTasks.Should().Be(1, "the message itself is still finished exactly once");
    }

    [Fact]
    public async Task DeadLetter_on_canceled_job_changes_nothing()
    {
        var job = MakeJob(status: JobStatus.Canceled, pendingTasks: 2);
        var repo = new FakeRepo(job);

        await MakeProcessor(repo, new FailingFetcher())
            .OnDeadLetteredAsync(MakeTask(job.Id, Child, depth: 1), "Poison: bad", CancellationToken.None);

        repo.PendingTasks.Should().Be(2);
        repo.JobStatus.Should().Be(JobStatus.Canceled);
        repo.ProcessedMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task DeadLetter_finalization_failure_rolls_back_and_can_be_retried()
    {
        var job = MakeJob(pendingTasks: 1);
        var repo = new FakeRepo(job) { ThrowIn = nameof(FakeRepo.SetJobFailedAsync) };
        var processor = MakeProcessor(repo, new FailingFetcher());
        var task = MakeTask(job.Id, depth: 0);

        var act = () => processor.OnDeadLetteredAsync(task, "Poison: bad", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        repo.PendingTasks.Should().Be(1, "the decrement ran inside the transaction that rolled back");
        repo.ProcessedMessageCount.Should().Be(0, "the guard rolled back too, so a retry can still finalize");
        repo.PageStatusOf(Root).Should().BeNull();
        repo.JobStatus.Should().Be(JobStatus.Running);

        repo.ThrowIn = null;
        await processor.OnDeadLetteredAsync(task, "Poison: bad", CancellationToken.None);

        repo.PendingTasks.Should().Be(0);
        repo.JobStatus.Should().Be(JobStatus.Failed);
    }

    // ── One messageId, one decrement — across every finishing path ────────────

    public enum FinishOrder
    {
        SuccessThenDeadLetter,
        DeadLetterThenSuccess,
        PermanentThenDeadLetter,
        DeadLetterTwice
    }

    [Theory]
    [InlineData(FinishOrder.SuccessThenDeadLetter)]
    [InlineData(FinishOrder.DeadLetterThenSuccess)]
    [InlineData(FinishOrder.PermanentThenDeadLetter)]
    [InlineData(FinishOrder.DeadLetterTwice)]
    public async Task One_messageId_decrements_pending_tasks_exactly_once_across_paths(FinishOrder order)
    {
        // pending starts at 3 so a double decrement (→ 1) cannot be confused with completion.
        var job = MakeJob(pendingTasks: 3);
        var repo = new FakeRepo(job);
        repo.SeedPage(Child, PageStatus.Pending);
        var task = MakeTask(job.Id, Child, depth: 1, parentUrl: Root);

        var success = MakeProcessor(repo, Html(NoLinksHtml, Child));
        var permanent = MakeProcessor(repo, new FixedResultFetcher(new PageFetchResult.Permanent("HTTP 404")));
        Func<Task> deadLetter = () => success.OnDeadLetteredAsync(task, "Retries exhausted", CancellationToken.None);

        switch (order)
        {
            case FinishOrder.SuccessThenDeadLetter:
                await success.HandleAsync(task, CancellationToken.None);
                await deadLetter();
                repo.PageStatusOf(Child).Should().Be(PageStatus.Crawled);
                break;
            case FinishOrder.DeadLetterThenSuccess:
                await deadLetter();
                await success.HandleAsync(task, CancellationToken.None);
                repo.PageStatusOf(Child).Should().Be(PageStatus.Failed);
                break;
            case FinishOrder.PermanentThenDeadLetter:
                await permanent.HandleAsync(task, CancellationToken.None);
                await deadLetter();
                repo.PageErrorOf(Child).Should().Be("HTTP 404");
                break;
            case FinishOrder.DeadLetterTwice:
                await deadLetter();
                await deadLetter();
                break;
        }

        repo.PendingTasks.Should().Be(2);
        repo.CounterUpdates.Should().Be(1);
        repo.ProcessedMessageCount.Should().Be(1);
        repo.JobStatus.Should().Be(JobStatus.Running);
    }
}
