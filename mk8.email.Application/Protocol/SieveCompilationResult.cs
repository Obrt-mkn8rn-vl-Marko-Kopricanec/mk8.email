namespace mk8.email.Application.Protocol;

internal sealed record SieveCompilationResult(
    SieveProgram? Program,
    IReadOnlyList<SieveDiagnostic> Diagnostics)
{
    public bool Succeeded => Program is not null && Diagnostics.Count == 0;
}
