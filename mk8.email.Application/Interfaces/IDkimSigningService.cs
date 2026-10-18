namespace mk8.email.Application.Interfaces;

public interface IDkimSigningService
{
    string Sign(string rawMessage, string domain, string selector, string privateKeyPath);
    string SignWithPrivateKey(string rawMessage, string domain, string selector, string privateKeyPem) =>
        throw new NotSupportedException("The signing implementation must accept protected snapshot key material.");
}
