namespace mk8.email.Jmap;

public interface IJmapPushWork
{
    Task ProcessDueAsync(CancellationToken cancellationToken);
}
