namespace mk8.email.Application.Protocol;

internal sealed record SieveFileInto(string Folder, bool Copy, bool Create, IReadOnlyList<string>? Flags) : SieveStatement;
