using System.Text.Json;
using Crawler.Contracts;
using Microsoft.Extensions.Logging;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Processes one delivery end to end: deserialize → handle → decide → settle.
/// Contains no RabbitMQ types so it can be unit-tested with fakes.
/// </summary>
public sealed class DeliveryProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICrawlTaskHandler _handler;
    private readonly IRabbitMqPublisher _publisher;
    private readonly int _maxAttempts;
    private readonly ILogger _logger;

    public DeliveryProcessor(
        ICrawlTaskHandler handler,
        IRabbitMqPublisher publisher,
        int maxAttempts,
        ILogger logger)
    {
        _handler = handler;
        _publisher = publisher;
        _maxAttempts = maxAttempts;
        _logger = logger;
    }

    public async Task ProcessAsync(
        ReadOnlyMemory<byte> body,
        IDictionary<string, object>? headers,
        IDeliverySettler settler,
        CancellationToken ct)
    {
        CrawlPageTask task;
        try
        {
            task = JsonSerializer.Deserialize<CrawlPageTask>(body.Span, JsonOptions)
                   ?? throw new JsonException("Deserialized message is null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // No readable jobId/messageId → nothing to finalize in the DB. Log and dead-letter.
            // Consequence: if this was a real task, its job keeps pending_tasks > 0 (see Flow.md).
            _logger.LogError(ex, "Poison message: deserialization failed — nacking to DLQ without finalization");
            settler.NackToDlq();
            return;
        }

        int attempt = ResolveAttempt(task, headers);

        ConsumeResult result;
        try
        {
            result = await _handler.HandleAsync(task, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unhandled exception from handler for jobId={JobId} url={Url} — treating as TransientFailure",
                task.JobId, task.Url);
            result = new ConsumeResult.TransientFailure(ex.Message);
        }

        var action = RetryPolicy.Decide(result, attempt, _maxAttempts);

        _logger.LogInformation(
            "Result={Result} Attempt={Attempt}/{MaxAttempts} Action={Action} jobId={JobId} messageId={MessageId}",
            result.GetType().Name, attempt, _maxAttempts, action, task.JobId, task.MessageId);

        switch (action)
        {
            case BrokerAction.Ack:
                settler.Ack();
                break;

            case BrokerAction.RetryAndAck:
                _logger.LogWarning("Retrying jobId={JobId} url={Url} attempt {Next}/{Max}",
                    task.JobId, task.Url, attempt + 1, _maxAttempts);
                await RepublishToRetryAndAckAsync(task with { Attempt = attempt + 1 }, settler, ct);
                break;

            case BrokerAction.NackToDlq:
                await DeadLetterAsync(task, attempt, result, settler, ct);
                break;
        }
    }

    private async Task DeadLetterAsync(
        CrawlPageTask task, int attempt, ConsumeResult result,
        IDeliverySettler settler, CancellationToken ct)
    {
        string reason = DescribeDeadLetterReason(result, attempt);

        // DB first, broker second: the nack is only sent after the finalization
        // transaction has committed, so a dead-lettered task can never leave
        // pending_tasks un-decremented. Finalization failure → no nack (D-018).
        try
        {
            await _handler.OnDeadLetteredAsync(task, reason, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dead-letter finalization failed for jobId={JobId} messageId={MessageId} — not nacking; " +
                "treating as transient and republishing to crawl.retry with unchanged attempt",
                task.JobId, task.MessageId);

            // Same Attempt: a DB outage is not the page's fault, so it does not consume a retry.
            await RepublishToRetryAndAckAsync(task, settler, ct);
            return;
        }

        _logger.LogError("Routing to DLQ: jobId={JobId} url={Url} attempt={Attempt} reason={Reason}",
            task.JobId, task.Url, attempt, reason);
        settler.NackToDlq();
    }

    private async Task RepublishToRetryAndAckAsync(CrawlPageTask task, IDeliverySettler settler, CancellationToken ct)
    {
        await _publisher.PublishRetryAsync(task, ct); // publisher confirms: throws if not confirmed
        settler.Ack();
    }

    private static int ResolveAttempt(CrawlPageTask task, IDictionary<string, object>? headers)
    {
        if (task.Attempt > 0)
            return task.Attempt;

        if (headers != null && headers.TryGetValue("attempt", out var attemptObj))
            return Convert.ToInt32(attemptObj);

        return 1;
    }

    private static string DescribeDeadLetterReason(ConsumeResult result, int attempt) => result switch
    {
        ConsumeResult.TransientFailure t => $"Retries exhausted after {attempt} attempt(s): {t.Reason}",
        ConsumeResult.Poison p => $"Poison: {p.Reason}",
        _ => $"Dead-lettered ({result.GetType().Name})"
    };
}
