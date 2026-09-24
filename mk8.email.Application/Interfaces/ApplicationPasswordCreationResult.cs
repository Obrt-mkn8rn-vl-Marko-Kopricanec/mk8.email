namespace mk8.email.Application.Interfaces;

public sealed record ApplicationPasswordCreationResult(
    bool Succeeded,
    string Message,
    Guid? Id = null,
    string? Password = null);
