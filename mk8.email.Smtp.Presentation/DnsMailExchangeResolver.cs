using DnsClient;
using Microsoft.Extensions.Logging;
using mk8.email.Contracts.Mail;

namespace mk8.email.Smtp.Presentation;

public sealed partial class DnsMailExchangeResolver(
    ILookupClient lookupClient,
    ILogger<DnsMailExchangeResolver> logger) : IMailExchangeResolver
{
    public async Task<MailRoutingResult> ResolveAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var response = await lookupClient.QueryAsync(
                domain,
                QueryType.MX,
                QueryClass.IN,
                cancellationToken).ConfigureAwait(false);
            var exchanges = response.Answers.MxRecords()
                .Select(record => (record.Exchange.Value, record.Preference));

            return CreateResult(domain, response.Header.ResponseCode, exchanges);
        }
        catch (Exception exception) when (exception is DnsResponseException or OperationCanceledException)
        {
            LogMxLookupFailure(logger, exception, domain);
            return new MailRoutingResult(MailRoutingStatus.TemporaryFailure, []);
        }
    }

    internal static MailRoutingResult CreateResult(
        string domain,
        DnsHeaderResponseCode responseCode,
        IEnumerable<(string Exchange, ushort Preference)> records)
    {
        if (responseCode == DnsHeaderResponseCode.NotExistentDomain)
            return new MailRoutingResult(MailRoutingStatus.DoesNotAcceptMail, []);
        if (responseCode != DnsHeaderResponseCode.NoError)
            return new MailRoutingResult(MailRoutingStatus.TemporaryFailure, []);

        var materialized = records.ToList();
        if (materialized.Count == 0)
        {
            return new MailRoutingResult(
                MailRoutingStatus.Available,
                [new MailExchangeEndpoint(domain, 0)]);
        }

        if (materialized.Any(record => NormalizeHost(record.Exchange).Length == 0))
            return new MailRoutingResult(MailRoutingStatus.DoesNotAcceptMail, []);

        var endpoints = materialized
            .Select(record => new MailExchangeEndpoint(
                NormalizeHost(record.Exchange),
                record.Preference))
            .GroupBy(endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(endpoint => endpoint.Preference).First())
            .OrderBy(endpoint => endpoint.Preference)
            .ThenBy(endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MailRoutingResult(MailRoutingStatus.Available, endpoints);
    }

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.');

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "MX lookup failed for {Domain}")]
    private static partial void LogMxLookupFailure(ILogger logger, Exception exception, string domain);
}
