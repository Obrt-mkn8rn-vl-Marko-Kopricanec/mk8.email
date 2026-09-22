using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;

namespace mk8.email.Jmap;

public sealed class JmapBlobTransactionEffects(
    ILargeObjectStore objects,
    ILogger<JmapBlobTransactionEffects> logger)
{
    private readonly List<Effect> effects = [];

    internal int Mark() => effects.Count;

    internal void DeleteOnCommit(LargeObjectReference reference) =>
        effects.Add(new Effect(reference, DeleteAfterCommit: true));

    internal void DeleteOnRollback(LargeObjectReference reference) =>
        effects.Add(new Effect(reference, DeleteAfterCommit: false));

    internal Task CommitAsync(int marker) => CompleteAsync(marker, committed: true);

    internal Task RollbackAsync(int marker) => CompleteAsync(marker, committed: false);

    internal void Discard(int marker)
    {
        ValidateMarker(marker);
        effects.RemoveRange(marker, effects.Count - marker);
    }

    private async Task CompleteAsync(int marker, bool committed)
    {
        ValidateMarker(marker);
        var completed = effects.Skip(marker).ToArray();
        effects.RemoveRange(marker, effects.Count - marker);
        foreach (var effect in completed.Where(candidate =>
                     candidate.DeleteAfterCommit == committed))
        {
            try
            {
                await objects.DeleteIfMatchAsync(effect.Reference, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not delete transactional JMAP object {ObjectName}",
                    effect.Reference.ObjectName);
            }
        }
    }

    private void ValidateMarker(int marker)
    {
        if (marker < 0 || marker > effects.Count)
            throw new ArgumentOutOfRangeException(nameof(marker));
    }

    private sealed record Effect(
        LargeObjectReference Reference,
        bool DeleteAfterCommit);
}
