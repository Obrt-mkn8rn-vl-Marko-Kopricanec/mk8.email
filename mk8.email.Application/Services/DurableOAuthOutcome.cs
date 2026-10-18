using System.Runtime.ExceptionServices;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Application.Services;

// This one-operation state survives BOTH transaction and nonpooled connection
// retirement. It carries no credential/result payload and is never serialized.
internal sealed class DurableOAuthOutcome
{
    private readonly Guid _operationId;
    private bool _recoverable;

    internal DurableOAuthOutcome(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A durable operation identity is required.", nameof(operationId));
        _operationId = operationId;
    }

    internal void RequireReceiptRecovery() => _recoverable = true;

    internal async Task<OAuthTokenApplicationResult> OwnAsync(
        IAsyncDisposable resource, Func<Task<OAuthTokenApplicationResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Exception? failure = null;
        OAuthTokenApplicationResult? result = null;
        try
        {
            await using (resource.ConfigureAwait(false))
            {
                try
                {
                    ArgumentNullException.ThrowIfNull(execute);
                    result = await execute().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Retain the actual body failure while this independently owned resource settles.
                catch (Exception original) when (!IsFatal(original)) { failure = original; }
#pragma warning restore CA1031
            }
        }
#pragma warning disable CA1031 // Preserve actual body and disposal failures; neither declares successful retirement.
        catch (Exception disposal) when (!IsFatal(disposal))
#pragma warning restore CA1031
        {
            failure = failure is null ? disposal : new AggregateException(failure, disposal);
        }
        if (failure is not null)
        {
            if (_recoverable && failure is not DurableOAuthRecoveryRequiredException)
                throw new DurableOAuthRecoveryRequiredException(_operationId, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result ?? throw new InvalidOperationException("The durable OAuth operation did not return a result.");
    }

    private static bool IsFatal(Exception error) => error is
        OutOfMemoryException or StackOverflowException or AccessViolationException;
}

