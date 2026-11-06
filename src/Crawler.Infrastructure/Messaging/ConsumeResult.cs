namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// The result a handler returns to tell the consumer how to dispose of the message.
/// </summary>
public abstract record ConsumeResult
{
    private ConsumeResult() { } // sealed hierarchy

    /// <summary>Page processed successfully.</summary>
    public sealed record Success : ConsumeResult;

    /// <summary>
    /// Permanent page-level error (404, 403, non-HTML, bad URL).
    /// The page has been recorded as Failed. ACK and move on.
    /// </summary>
    public sealed record PermanentFailure(string Reason) : ConsumeResult;

    /// <summary>
    /// Transient error (timeout, 5xx, 429, DB deadlock).
    /// Retry up to MaxAttempts, then route to DLQ.
    /// </summary>
    public sealed record TransientFailure(string Reason) : ConsumeResult;

    /// <summary>
    /// Unrecoverable message (deserialization error, invalid schema).
    /// NACK with requeue=false → DLQ immediately.
    /// </summary>
    public sealed record Poison(string Reason) : ConsumeResult;
}

