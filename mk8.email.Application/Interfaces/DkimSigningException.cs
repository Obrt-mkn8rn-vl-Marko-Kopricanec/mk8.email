namespace mk8.email.Application.Interfaces;

public sealed class DkimSigningException(string message, Exception innerException)
    : Exception(message, innerException);
