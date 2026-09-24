namespace mk8.email.Application.Interfaces;

public interface IMailScanner
{
    Task<MailScanResult> ScanAsync(
        MailScanRequest request,
        CancellationToken cancellationToken = default);
}
