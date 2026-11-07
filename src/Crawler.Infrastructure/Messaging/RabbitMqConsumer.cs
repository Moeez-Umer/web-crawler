using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Long-running background service that consumes from crawl.tasks.
/// Per-delivery logic lives in <see cref="DeliveryProcessor"/>; this class only owns the channel.
///
/// Ack/nack contract:
///   Success            → ACK
///   PermanentFailure   → ACK  (page recorded as Failed in DB by the handler)
///   TransientFailure, attempt &lt; MaxAttempts
///                      → republish to crawl.retry with attempt+1, then ACK original
///   TransientFailure, attempt ≥ MaxAttempts, or Poison result
///                      → handler.OnDeadLetteredAsync (DB commit) → NACK requeue=false → DLQ
///                        (if finalization throws: no nack; republish to crawl.retry, same attempt)
///   Deserialization failure → NACK requeue=false → DLQ (no finalization: no jobId)
///   Unhandled exception from handler → treated as TransientFailure
/// </summary>
public sealed class RabbitMqConsumer : BackgroundService
{
    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConsumer> _logger;
    private readonly DeliveryProcessor _processor;

    private IModel? _channel;

    public RabbitMqConsumer(
        IConnection connection,
        ICrawlTaskHandler handler,
        IRabbitMqPublisher publisher,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConsumer> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
        _processor = new DeliveryProcessor(handler, publisher, _options.MaxAttempts, logger);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the blocking consumer setup on a thread-pool thread so it does not block host startup.
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
        var settler = new ChannelSettler(_channel!, ea.DeliveryTag);
        try
        {
            // Sync-over-async, once per delivery (D-020): EventingBasicConsumer callbacks are
            // synchronous in RabbitMQ.Client 6.x. Blocking here is also what keeps ea.Body valid —
            // the client recycles the body buffer as soon as this callback returns.
            _processor.ProcessAsync(ea.Body, ea.BasicProperties?.Headers, settler, stoppingToken)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Only reachable when the broker side failed (e.g. retry publish not confirmed).
            // Requeue so the message is not lost; the processed_messages guard makes redelivery safe.
            _logger.LogError(ex, "Delivery {DeliveryTag} could not be settled normally — requeueing", ea.DeliveryTag);
            settler.NackRequeueIfUnsettled(_logger);
        }
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }

    private sealed class ChannelSettler : IDeliverySettler
    {
        private readonly IModel _channel;
        private readonly ulong _deliveryTag;
        private bool _settled;

        public ChannelSettler(IModel channel, ulong deliveryTag)
        {
            _channel = channel;
            _deliveryTag = deliveryTag;
        }

        public void Ack()
        {
            _channel.BasicAck(_deliveryTag, multiple: false);
            _settled = true;
        }

        public void NackToDlq()
        {
            // requeue=false → broker dead-letters via crawl.dlx → crawl.tasks.dlq
            _channel.BasicNack(_deliveryTag, multiple: false, requeue: false);
            _settled = true;
        }

        public void NackRequeueIfUnsettled(ILogger logger)
        {
            if (_settled) return;
            try
            {
                _channel.BasicNack(_deliveryTag, multiple: false, requeue: true);
                _settled = true;
            }
            catch (Exception ex)
            {
                // Channel is gone; the broker will redeliver the unacked message on its own.
                logger.LogError(ex, "Requeue nack failed for delivery {DeliveryTag}; relying on broker redelivery", _deliveryTag);
            }
        }
    }
}
