namespace mk8.email.Application.Interfaces;

internal sealed record SieveDeliveryInstruction(
    string Folder,
    IReadOnlyList<string> Flags,
    bool Create);
