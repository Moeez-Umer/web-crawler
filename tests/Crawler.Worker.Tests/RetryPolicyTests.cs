using Crawler.Infrastructure.Messaging;

namespace Crawler.Worker.Tests;

public class RetryPolicyTests
{
    private const int Max = 5;

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public void Success_gives_Ack()
        => RetryPolicy.Decide(new ConsumeResult.Success(), attempt: 1, maxAttempts: Max)
            .Should().Be(BrokerAction.Ack);

    // ── PermanentFailure ──────────────────────────────────────────────────────

    [Fact]
    public void PermanentFailure_gives_Ack()
        => RetryPolicy.Decide(new ConsumeResult.PermanentFailure("404"), attempt: 1, maxAttempts: Max)
            .Should().Be(BrokerAction.Ack);

    // ── TransientFailure within budget ────────────────────────────────────────

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(4, 5)]
    public void TransientFailure_within_budget_gives_RetryAndAck(int attempt, int max)
        => RetryPolicy.Decide(new ConsumeResult.TransientFailure("timeout"), attempt, max)
            .Should().Be(BrokerAction.RetryAndAck);

    // ── TransientFailure budget exhausted ─────────────────────────────────────

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)] // defensive: attempt > max treated as exhausted
    [InlineData(10, 5)]
    public void TransientFailure_exhausted_gives_NackToDlq(int attempt, int max)
        => RetryPolicy.Decide(new ConsumeResult.TransientFailure("timeout"), attempt, max)
            .Should().Be(BrokerAction.NackToDlq);

    // ── Poison ────────────────────────────────────────────────────────────────

    [Fact]
    public void Poison_gives_NackToDlq_regardless_of_attempt()
    {
        RetryPolicy.Decide(new ConsumeResult.Poison("bad json"), attempt: 1, maxAttempts: Max)
            .Should().Be(BrokerAction.NackToDlq);
        RetryPolicy.Decide(new ConsumeResult.Poison("bad json"), attempt: 3, maxAttempts: Max)
            .Should().Be(BrokerAction.NackToDlq);
    }

    // ── Boundary: exactly at max attempt ─────────────────────────────────────

    [Fact]
    public void TransientFailure_at_exactly_max_gives_NackToDlq()
        => RetryPolicy.Decide(new ConsumeResult.TransientFailure("5xx"), attempt: Max, maxAttempts: Max)
            .Should().Be(BrokerAction.NackToDlq);

    [Fact]
    public void TransientFailure_one_before_max_gives_RetryAndAck()
        => RetryPolicy.Decide(new ConsumeResult.TransientFailure("5xx"), attempt: Max - 1, maxAttempts: Max)
            .Should().Be(BrokerAction.RetryAndAck);
}

