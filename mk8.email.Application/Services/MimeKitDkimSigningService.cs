using MimeKit;
using MimeKit.Cryptography;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Protocol;
using mk8.email.Configuration;

namespace mk8.email.Application.Services;

public sealed class MimeKitDkimSigningService : IDkimSigningService
{
    private static readonly HeaderId[] SignedHeaders =
    [
        HeaderId.From,
        HeaderId.Sender,
        HeaderId.ReplyTo,
        HeaderId.To,
        HeaderId.Cc,
        HeaderId.Subject,
        HeaderId.Date,
        HeaderId.MessageId,
        HeaderId.MimeVersion,
        HeaderId.ContentType,
        HeaderId.ContentTransferEncoding,
    ];

    public string Sign(string rawMessage, string domain, string selector, string privateKeyPath)
    {
        try
        {
            ValidateIdentity(domain, selector);

            using var input = new MemoryStream(
                MailWireEncoding.Instance.GetBytes(rawMessage),
                writable: false);
            using var message = MimeMessage.Load(input);
            if (message.Headers.IndexOf(HeaderId.From) < 0)
                throw new FormatException("The message requires a From header for DKIM signing.");
            var options = FormatOptions.Default.Clone();
            options.NewLineFormat = NewLineFormat.Dos;

            var signer = new DkimSigner(
                privateKeyPath,
                domain,
                selector,
                DkimSignatureAlgorithm.RsaSha256)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                AgentOrUserIdentifier = $"@{domain}",
                QueryMethod = "dns/txt",
            };

            message.Prepare(EncodingConstraint.EightBit);
            signer.Sign(options, message, SignedHeaders);

            using var output = new MemoryStream();
            message.WriteTo(options, output);
            return MailWireEncoding.Instance.GetString(output.ToArray());
        }
        catch (Exception exception) when (exception is not DkimSigningException)
        {
            throw new DkimSigningException("DKIM signing failed.", exception);
        }
    }

    private static void ValidateIdentity(string domain, string selector)
    {
        if (!DkimIdentityValidator.IsValidDomain(domain))
            throw new ArgumentException("The DKIM domain must be a fully qualified DNS name.", nameof(domain));
        if (!DkimIdentityValidator.IsValidSelector(selector))
            throw new ArgumentException("The DKIM selector must be a DNS label.", nameof(selector));
    }
}
