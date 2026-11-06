using RabbitMQ.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Declares all exchanges and queues required by the crawl system.
/// All declarations are idempotent: calling Declare() on an already-existing
/// topology with identical arguments is a no-op at the AMQP level.
///
/// Topology:
///   crawl.exchange  (direct) ──binding key "crawl"──► crawl.tasks  [x-dead-letter-exchange=crawl.dlx]
///   crawl.dlx       (fanout) ────────────────────────► crawl.tasks.dlq
///   crawl.retry     (queue, x-message-ttl, x-dead-letter-exchange=crawl.exchange,
///                            x-dead-letter-routing-key=crawl)
///                   When a retry message's TTL expires, the broker routes it back to
///                   crawl.exchange with routing key "crawl" → crawl.tasks.
/// </summary>
public sealed class RabbitMqTopology
{
    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTopology> _logger;

    public RabbitMqTopology(
        IConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTopology> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    public void Declare()
    {
        using var channel = _connection.CreateModel();

        // ── Main exchange ───────────────────────────────────────────────────
        channel.ExchangeDeclare(
            exchange: _options.CrawlExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        // ── Dead-letter exchange (fanout → DLQ) ────────────────────────────
        channel.ExchangeDeclare(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false);

        // ── Main task queue (DLX set so nack/requeue=false → DLQ) ─────────
        channel.QueueDeclare(
            queue: _options.CrawlTasksQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = _options.DeadLetterExchange
            });

        channel.QueueBind(
            queue: _options.CrawlTasksQueue,
            exchange: _options.CrawlExchange,
            routingKey: _options.CrawlRoutingKey);

        // ── Dead-letter queue (receives poison / exhausted messages) ───────
        channel.QueueDeclare(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        channel.QueueBind(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: string.Empty); // fanout ignores routing key

        // ── Retry queue (TTL + DLX back to main exchange) ──────────────────
        // Messages published here expire after RetryDelayMs and are routed back
        // to crawl.tasks via crawl.exchange with the original crawl routing key.
        channel.QueueDeclare(
            queue: _options.RetryQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-message-ttl"] = _options.RetryDelayMs,
                ["x-dead-letter-exchange"] = _options.CrawlExchange,
                ["x-dead-letter-routing-key"] = _options.CrawlRoutingKey
            });

        _logger.LogInformation(
            "RabbitMQ topology declared: exchange={Exchange}, dlx={Dlx}, " +
            "queues={Tasks},{Dlq},{Retry}, retryDelayMs={Delay}",
            _options.CrawlExchange, _options.DeadLetterExchange,
            _options.CrawlTasksQueue, _options.DeadLetterQueue, _options.RetryQueue,
            _options.RetryDelayMs);
    }
}

