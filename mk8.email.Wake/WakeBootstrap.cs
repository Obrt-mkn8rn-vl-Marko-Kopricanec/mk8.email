namespace mk8.email.Wake;

internal static class WakeBootstrap
{
    internal static WakeMode? ParseMode(string[] arguments) => arguments switch
    {
        ["--serve"] or ["--serve-worker"] => WakeMode.Serve,
        ["--probe"] or ["--probe-db"] => WakeMode.Probe,
        _ => null,
    };
}
