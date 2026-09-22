using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Storage;

namespace mk8.email.Application.Services;

public sealed class LargeObjectTransactionEffects(
    ILargeObjectStore objects,
    ILogger<LargeObjectTransactionEffects> logger)
{
    private readonly List<Effect> effects = [];

    public int Mark() => effects.Count;

    public void DeleteOnCommit(LargeObjectReference reference) =>
        effects.Add(new Effect(reference, DeleteAfterCommit: true));

    public void DeleteOnRollback(LargeObjectReference reference) =>
        effects.Add(new Effect(reference, DeleteAfterCommit: false));

    public Task CommitAsync(int marker) => CompleteAsync(marker, committed: true);

    public Task RollbackAsync(int marker) => CompleteAsync(marker, committed: false);

    public void Discard(int marker)
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
                    "Could not delete transactional large object {ObjectName}",
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
