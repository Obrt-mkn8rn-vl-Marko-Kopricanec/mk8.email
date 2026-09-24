namespace mk8.email.Application.Protocol;

internal sealed record SieveDelivery(string Folder, IReadOnlyList<string> Flags, bool Create);
