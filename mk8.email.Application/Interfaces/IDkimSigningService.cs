namespace mk8.email.Application.Interfaces;

public interface IDkimSigningService
{
    string Sign(string rawMessage, string domain, string selector, string privateKeyPath);
}
