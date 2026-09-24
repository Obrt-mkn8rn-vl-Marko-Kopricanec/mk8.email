namespace mk8.email.Application.Interfaces;

public sealed class DkimSigningException : Exception
{
    public DkimSigningException()
    {
    }

    public DkimSigningException(string message) : base(message)
    {
    }

    public DkimSigningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
