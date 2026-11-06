using Crawler.Contracts;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Implemented by the Worker's CrawlTaskProcessor.
/// The consumer calls this for every successfully deserialized message.
/// </summary>
public interface ICrawlTaskHandler
{
    Task<ConsumeResult> HandleAsync(CrawlPageTask task, CancellationToken cancellationToken);
}

