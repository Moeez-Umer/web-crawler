using Crawler.Contracts;

namespace Crawler.Infrastructure.Messaging;

/// <summary>Publishes CrawlPageTask messages to RabbitMQ.</summary>
public interface IRabbitMqPublisher
{
    /// <summary>Publish to the main crawl exchange (crawl.tasks queue).</summary>
    Task PublishCrawlTaskAsync(CrawlPageTask task, CancellationToken cancellationToken = default);

    /// <summary>Publish to the retry queue (with TTL, dead-letters back to crawl.tasks).</summary>
    Task PublishRetryAsync(CrawlPageTask task, CancellationToken cancellationToken = default);
}

