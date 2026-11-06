namespace Crawler.Infrastructure.Messaging;

/// <summary>
/// Pure, stateless policy: maps a handler result + current attempt to a broker action.
/// No RabbitMQ dependency — fully unit-testable.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Returns the broker action to take given the handler's result and how many
    /// times this message has already been attempted (1-based: first delivery = 1).
    /// </summary>
    public static BrokerAction Decide(ConsumeResult result, int attempt, int maxAttempts) =>
        result switch
        {
            // Success: page processed — ACK and move on.
            ConsumeResult.Success => BrokerAction.Ack,

            // Permanent page failure: recorded in DB, nothing to retry — ACK.
            ConsumeResult.PermanentFailure => BrokerAction.Ack,

            // Transient failure: retry if budget remains, else DLQ.
            ConsumeResult.TransientFailure =>
                attempt < maxAttempts ? BrokerAction.RetryAndAck : BrokerAction.NackToDlq,

            // Poison / unknown: unrecoverable — DLQ immediately.
            ConsumeResult.Poison => BrokerAction.NackToDlq,

            // Defensive: treat any unknown result as poison.
            _ => BrokerAction.NackToDlq
        };
}

