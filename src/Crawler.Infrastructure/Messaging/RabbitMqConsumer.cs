using System.Text;
using System.Text.Json;
using Crawler.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Long-running background service that consumes from crawl.tasks.
///
/// Ack/nack contract:
///   Success            → ACK
///   PermanentFailure   → ACK  (page recorded as Failed in DB by the handler)
///   TransientFailure, attempt &lt; MaxAttempts
///                      → republish to crawl.retry with attempt+1, then ACK original
///   TransientFailure, attempt ≥ MaxAttempts
///                      → NACK requeue=false → broker routes to crawl.tasks.dlq via crawl.dlx
///   Poison (deser. failure, invalid schema, unrecoverable)
///                      → NACK requeue=false → DLQ
///   Unhandled exception from handler → treated as TransientFailure
/// </summary>
public sealed class RabbitMqConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnection _connection;
    private readonly ICrawlTaskHandler _handler;
    private readonly IRabbitMqPublisher _publisher;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConsumer> _logger;

    private IModel? _channel;

    public RabbitMqConsumer(
        IConnection connection,
        ICrawlTaskHandler handler,
        IRabbitMqPublisher publisher,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConsumer> logger)
    {
        _connection = connection;
        _handler = handler;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the blocking consumer setup on a thread-pool thread so it does not
        // block the host startup. The inner loop is event-driven via AsyncEventingBasicConsumer.
        return Task.Run(() => StartConsuming(stoppingToken), stoppingToken);
    }

    private void StartConsuming(CancellationToken stoppingToken)
    {
        _channel = _connection.CreateModel();

        // Prefetch: broker delivers at most N unacked messages at a time.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: _options.PrefetchCount, global: false);

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += (_, ea) => OnMessageReceived(ea, stoppingToken);

        _channel.BasicConsume(
            queue: _options.CrawlTasksQueue,
            autoAck: false, // manual acks — we decide when to ack/nack
            consumer: consumer);

        _logger.LogInformation(
            "RabbitMQ consumer started on queue={Queue}, prefetch={Prefetch}",
            _options.CrawlTasksQueue, _options.PrefetchCount);

        // Block until cancellation is requested.
        stoppingToken.WaitHandle.WaitOne();

        _logger.LogInformation("RabbitMQ consumer stopping — finishing in-flight message.");
    }

    private void OnMessageReceived(BasicDeliverEventArgs ea, CancellationToken stoppingToken)
    {
        var deliveryTag = ea.DeliveryTag;
        CrawlPageTask? task = null;
        int attempt = 1;

        try
        {
            task = JsonSerializer.Deserialize<CrawlPageTask>(ea.Body.Span, JsonOptions);

            if (task is null)
                throw new JsonException("Deserialized message is null.");

            if (task.Attempt > 0)
            {
                attempt = task.Attempt;
            }
            else if (ea.BasicProperties?.Headers != null &&
                     ea.BasicProperties.Headers.TryGetValue("attempt", out var attemptObj))
            {
                attempt = Convert.ToInt32(attemptObj);
            }
        }
        catch (Exception ex)
        {
            // Deserialization failure → Poison: cannot retry a message we cannot read.
            _logger.LogError(ex,
                "Poison message: deserialization failed on delivery tag {DeliveryTag}", deliveryTag);
            NackToDlq(deliveryTag);
            return;
        }

        _logger.LogInformation(
            "Received task jobId={JobId} url={Url} depth={Depth} attempt={Attempt}",
            task.JobId, task.Url, task.Depth, attempt);

        // Handle the task; catch all exceptions and treat as transient.
        ConsumeResult result;
        try
        {
            result = _handler.HandleAsync(task, stoppingToken).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unhandled exception from handler for jobId={JobId} url={Url} — treating as TransientFailure",
                task.JobId, task.Url);
            result = new ConsumeResult.TransientFailure(ex.Message);
        }

        var action = RetryPolicy.Decide(result, attempt, _options.MaxAttempts);

        _logger.LogInformation(
            "Result={Result} Attempt={Attempt}/{MaxAttempts} Action={Action} jobId={JobId}",
            result.GetType().Name, attempt, _options.MaxAttempts, action, task.JobId);

        switch (action)
        {
            case BrokerAction.Ack:
                _channel!.BasicAck(deliveryTag, multiple: false);
                break;

            case BrokerAction.RetryAndAck:
                // Republish with attempt+1 to the retry queue (TTL will delay redelivery),
                // then ACK the original so the broker removes it from crawl.tasks.
                var retryTask = task with { Attempt = attempt + 1 };
                _publisher.PublishRetryAsync(retryTask).GetAwaiter().GetResult();
                _logger.LogWarning(
                    "Retrying jobId={JobId} url={Url} attempt {Next}/{Max}",
                    task.JobId, task.Url, attempt + 1, _options.MaxAttempts);
                _channel!.BasicAck(deliveryTag, multiple: false);
                break;

            case BrokerAction.NackToDlq:
                _logger.LogError(
                    "Routing to DLQ: jobId={JobId} url={Url} attempt={Attempt} result={Result}",
                    task.JobId, task.Url, attempt, result.GetType().Name);
                NackToDlq(deliveryTag);
                break;
        }
    }

    private void NackToDlq(ulong deliveryTag)
    {
        // requeue=false causes the broker to dead-letter the message to crawl.dlx → crawl.tasks.dlq
        _channel!.BasicNack(deliveryTag, multiple: false, requeue: false);
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }
}

