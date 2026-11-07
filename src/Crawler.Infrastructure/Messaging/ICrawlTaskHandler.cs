using Crawler.Contracts;

namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Implemented by the Worker's CrawlTaskProcessor.
/// The consumer calls this for every successfully deserialized message.
/// </summary>
public interface ICrawlTaskHandler
{
    Task<ConsumeResult> HandleAsync(CrawlPageTask task, CancellationToken cancellationToken);

    /// <summary>
    /// Called before a deserialized task is nacked to the DLQ (retries exhausted or poison).
    /// Must durably finalize the task (page Failed, pending_tasks − 1, job finalization)
    /// and return only after commit. Must be idempotent per MessageId.
    /// If it throws, the consumer does NOT nack (see D-018).
    /// </summary>
    Task OnDeadLetteredAsync(CrawlPageTask task, string reason, CancellationToken cancellationToken);
}
