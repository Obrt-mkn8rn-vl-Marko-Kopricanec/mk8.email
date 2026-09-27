namespace mk8.email.Application.Services;

public sealed record ApplicationReceiptKey(Guid OperationId, int StepNumber, Guid UserId, string Purpose, string InputHash);
