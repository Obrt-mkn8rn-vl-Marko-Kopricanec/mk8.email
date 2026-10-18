namespace mk8.email.Application.Services;

// Shared with the already-authorized Worker friend assembly, not a public API
// or a wire error. The operation-bound signal forbids terminal bus failure.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1064", Justification = "This implementation-only signal crosses the existing Worker friend boundary; it is not a public package or wire exception contract.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "A nonempty operation identity and the retained original failure are mandatory. Identity-free or user-message constructors cannot safely request same-operation recovery.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RCS1194", Justification = "Lower-priority duplicate of CA1032; this internal recovery signal requires the exact operation identity and original failure, not general exception constructors.")]
internal sealed class DurableOAuthRecoveryRequiredException : Exception
{
    internal DurableOAuthRecoveryRequiredException(Guid operationId, Exception original)
        : base("The durable OAuth outcome requires recovery of its original operation receipt.", original)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A durable operation identity is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(original);
        OperationId = operationId;
    }

    internal Guid OperationId { get; }
}
