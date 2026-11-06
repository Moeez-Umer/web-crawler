using System.Text;
using System.Text.Json;
using Crawler.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Publishes CrawlPageTask messages to RabbitMQ.
/// Uses publisher confirms so a failed publish is detected and thrown.
/// Messages are persistent (delivery mode 2) so they survive broker restarts.
/// </summary>
public sealed class RabbitMqPublisher : IRabbitMqPublisher, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly IConnection _connection;
    private readonly IModel _channel;

    public RabbitMqPublisher(
        IConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connection = connection;
        _channel = _connection.CreateModel();

        // Publisher confirms: the broker acks each publish so we know it was persisted.
        _channel.ConfirmSelect();
    }

    /// <inheritdoc/>
    public Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        Publish(task, _options.CrawlExchange, _options.CrawlRoutingKey);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default)
    {
        // Retry queue is reached via the default exchange (empty string), routing key = queue name.
        // The queue has x-message-ttl; when the TTL expires the broker dead-letters the message
        // back to crawl.exchange with routing key "crawl", which routes it to crawl.tasks.
        Publish(task, exchange: string.Empty, routingKey: _options.RetryQueue);
        return Task.CompletedTask;
    }

    private void Publish(CrawlPageTask task, string exchange, string routingKey)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(task, JsonOptions);

        var props = _channel.CreateBasicProperties();
        props.MessageId = task.MessageId.ToString();
        props.CorrelationId = task.JobId.ToString(); // jobId used as correlationId
        props.ContentType = "application/json";
        props.DeliveryMode = 2;  // persistent
        props.Headers = new Dictionary<string, object>
        {
            ["attempt"] = task.Attempt,
            ["jobId"] = task.JobId.ToString(),
            ["depth"] = task.Depth
        };

        _channel.BasicPublish(exchange, routingKey, mandatory: false, props, body);

        // Wait for broker confirm; throws if the publish was nacked or timed out.
        if (!_channel.WaitForConfirms(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException(
                $"RabbitMQ publish confirm timed out for message {task.MessageId}");
        }

        _logger.LogDebug(
            "Published {Exchange}/{RoutingKey} msgId={MessageId} jobId={JobId} depth={Depth} attempt={Attempt}",
            string.IsNullOrEmpty(exchange) ? "(default)" : exchange,
            routingKey, task.MessageId, task.JobId, task.Depth, task.Attempt);
    }

    public void Dispose() => _channel.Dispose();
}

