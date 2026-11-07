using System.Text;
using System.Text.Json;
using Crawler.Contracts;
using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Crawler.Infrastructure.Messaging;
using Crawler.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crawler.Worker.Tests;

/// <summary>
/// Tests the consumer's per-delivery logic (deserialize → handle → settle) without a broker.
/// </summary>
public class DeliveryProcessorTests
{
    private const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static CrawlPageTask MakeTask(Guid? jobId = null, int attempt = 1, int depth = 1)
        => new(Guid.NewGuid(), jobId ?? Guid.NewGuid(), "https://example.com/about", "https://example.com/",
            depth, 2, "example.com", attempt);

    private static ReadOnlyMemory<byte> Body(CrawlPageTask task) => JsonSerializer.SerializeToUtf8Bytes(task, Camel);

    private static DeliveryProcessor MakeProcessor(ICrawlTaskHandler handler, FakePublisher publisher)
        => new(handler, publisher, MaxAttempts, NullLogger.Instance);

    // ── Deserialization failure: no finalization possible ─────────────────────

    [Fact]
    public async Task Malformed_json_is_nacked_to_DLQ_without_calling_the_handler()
    {
        var handler = new ScriptedHandler(new ConsumeResult.Success());
        var settler = new FakeSettler();

        await MakeProcessor(handler, new FakePublisher())
            .ProcessAsync(Encoding.UTF8.GetBytes("{ not json"), null, settler, CancellationToken.None);

        settler.DlqNacks.Should().Be(1);
        settler.Acks.Should().Be(0);
        handler.HandleCalls.Should().Be(0);
        handler.DeadLetterCalls.Should().Be(0, "there is no jobId to finalize");
    }

    // ── Normal outcomes ───────────────────────────────────────────────────────

    [Fact]
    public async Task Transient_within_budget_republishes_with_next_attempt_and_acks()
    {
        var handler = new ScriptedHandler(new ConsumeResult.TransientFailure("timeout"));
        var publisher = new FakePublisher();
        var settler = new FakeSettler();
        var task = MakeTask(attempt: 2);

        await MakeProcessor(handler, publisher).ProcessAsync(Body(task), null, settler, CancellationToken.None);

        publisher.Retried.Should().ContainSingle().Which.Attempt.Should().Be(3);
        publisher.Retried[0].MessageId.Should().Be(task.MessageId, "retries keep the idempotency key");
        settler.Acks.Should().Be(1);
        settler.DlqNacks.Should().Be(0);
        handler.DeadLetterCalls.Should().Be(0);
    }

    // ── Dead-letter ordering ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]  // retries exhausted
    [InlineData(false)] // poison result
    public async Task Dead_letter_finalizes_before_nacking(bool exhausted)
    {
        ConsumeResult result = exhausted
            ? new ConsumeResult.TransientFailure("HTTP 503")
            : new ConsumeResult.Poison("invalid");
        var settler = new FakeSettler();
        var handler = new ScriptedHandler(result) { Settler = settler };
        var task = MakeTask(attempt: exhausted ? MaxAttempts : 1);

        await MakeProcessor(handler, new FakePublisher()).ProcessAsync(Body(task), null, settler, CancellationToken.None);

        handler.DeadLetterCalls.Should().Be(1);
        handler.NacksSeenAtFinalization.Should().Be(0, "the nack must come after finalization commits");
        handler.LastReason.Should().Contain(exhausted ? "Retries exhausted" : "Poison");
        settler.DlqNacks.Should().Be(1);
        settler.Acks.Should().Be(0);
    }

    [Fact]
    public async Task Finalization_failure_does_not_nack_and_retries_with_same_attempt()
    {
        var handler = new ScriptedHandler(new ConsumeResult.TransientFailure("HTTP 503")) { ThrowOnDeadLetter = true };
        var publisher = new FakePublisher();
        var settler = new FakeSettler();
        var task = MakeTask(attempt: MaxAttempts);

        await MakeProcessor(handler, publisher).ProcessAsync(Body(task), null, settler, CancellationToken.None);

        settler.DlqNacks.Should().Be(0);
        publisher.Retried.Should().ContainSingle().Which.Should().Be(task, "same MessageId, same Attempt");
        settler.Acks.Should().Be(1, "original is acked only after the retry copy is confirmed");
    }

    // ── End to end with the real processor and a failing DB ───────────────────

    [Fact]
    public async Task Finalization_DB_failure_leaves_counter_unchanged_and_message_alive_until_DB_recovers()
    {
        var job = new CrawlJob
        {
            Id = Guid.NewGuid(), Url = "https://example.com/", MaxDepth = 2, MaxPages = 200,
            Status = JobStatus.Running, PendingTasks = 2, CreatedAt = DateTime.UtcNow
        };
        var repo = new FakeRepo(job) { ThrowIn = nameof(FakeRepo.AdjustCountersAndReturnPendingAsync) };
        var publisher = new FakePublisher();
        var crawlProcessor = new CrawlTaskProcessor(repo,
            new FixedResultFetcher(new PageFetchResult.Transient("HTTP 503")),
            new LinkExtractor(), publisher, NullLogger<CrawlTaskProcessor>.Instance);
        var delivery = MakeProcessor(crawlProcessor, publisher);
        var task = MakeTask(job.Id, attempt: MaxAttempts);

        // First delivery: retries exhausted, but the DB fails during finalization.
        var first = new FakeSettler();
        await delivery.ProcessAsync(Body(task), null, first, CancellationToken.None);

        first.DlqNacks.Should().Be(0, "never nack while the counter is un-finalized");
        repo.PendingTasks.Should().Be(2);
        repo.ProcessedMessageCount.Should().Be(0);
        publisher.Retried.Should().ContainSingle();

        // Redelivery of the retry copy after the DB recovers.
        repo.ThrowIn = null;
        var second = new FakeSettler();
        await delivery.ProcessAsync(Body(publisher.Retried[0]), null, second, CancellationToken.None);

        second.DlqNacks.Should().Be(1);
        repo.PendingTasks.Should().Be(1, "decremented exactly once");
        repo.PageStatusOf(task.Url).Should().Be(PageStatus.Failed);
    }

    /// <summary>Handler returning a fixed result and recording dead-letter calls.</summary>
    private sealed class ScriptedHandler : ICrawlTaskHandler
    {
        private readonly ConsumeResult _result;

        public ScriptedHandler(ConsumeResult result) => _result = result;

        public FakeSettler? Settler { get; init; }
        public bool ThrowOnDeadLetter { get; init; }
        public int HandleCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int NacksSeenAtFinalization { get; private set; } = -1;
        public string? LastReason { get; private set; }

        public Task<ConsumeResult> HandleAsync(CrawlPageTask task, CancellationToken cancellationToken)
        {
            HandleCalls++;
            return Task.FromResult(_result);
        }

        public Task OnDeadLetteredAsync(CrawlPageTask task, string reason, CancellationToken cancellationToken)
        {
            DeadLetterCalls++;
            LastReason = reason;
            NacksSeenAtFinalization = Settler?.DlqNacks ?? -1;
            if (ThrowOnDeadLetter) throw new InvalidOperationException("Simulated DB failure");
            return Task.CompletedTask;
        }
    }
}

