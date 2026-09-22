using System.Text.Json;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Messaging;
using mk8.email.Messaging;

namespace mk8.email.Gateway.ApplicationBridge;

public sealed class GatewayApplicationClient(
    IApplicationRequestClient requests,
    IGatewayTrafficJournal traffic,
    GatewayApplicationOptions options) : IGatewayApplicationClient
{
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<LoginResultDTO> AuthenticateAsync(
        LoginRequestDTO request,
        CancellationToken cancellationToken = default) =>
        SendAsync<LoginRequestDTO, LoginResultDTO>(
            ApplicationOperations.AdminAuthenticate,
            request,
            cancellationToken);

    public Task<AdminDashboardDTO> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, AdminDashboardDTO>(
            ApplicationOperations.AdminDashboardGet,
            new { },
            cancellationToken);

    public Task<IReadOnlyList<MailDomainSummaryDTO>> GetDomainsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<MailDomainSummaryDTO>>(
            ApplicationOperations.AdminDomainsGet,
            new { },
            cancellationToken);

    public Task<AdministrationResult> EnsureDomainAsync(
        AdminEnsureDomainRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminEnsureDomainRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsEnsure,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetCatchAllAsync(
        AdminSetCatchAllRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetCatchAllRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsSetCatchAll,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetDomainActiveAsync(
        AdminSetDomainActiveRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetDomainActiveRequest, AdministrationResult>(
            ApplicationOperations.AdminDomainsSetActive,
            request,
            cancellationToken);

    public Task<IReadOnlyList<MailAccountSummaryDTO>> GetAccountsAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<MailAccountSummaryDTO>>(
            ApplicationOperations.AdminAccountsGet,
            new { },
            cancellationToken);

    public Task<AdministrationResult> CreateAccountAsync(
        AdminCreateAccountRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminCreateAccountRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsCreate,
            request,
            cancellationToken);

    public Task<AdministrationResult> SetAccountActiveAsync(
        AdminSetAccountActiveRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminSetAccountActiveRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsSetActive,
            request,
            cancellationToken);

    public Task<AdministrationResult> ResetPasswordAsync(
        AdminResetPasswordRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<AdminResetPasswordRequest, AdministrationResult>(
            ApplicationOperations.AdminAccountsResetPassword,
            request,
            cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest value,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var requestId = Guid.CreateVersion7();
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gateway-instance"] = options.InstanceId,
            ["operation"] = operation,
        };
        var request = new ApplicationRequest(
            requestId,
            sessionId,
            0,
            "admin",
            operation,
            JsonContentType,
            payload,
            metadata,
            now,
            now.Add(options.RequestTimeout),
            requestId.ToString("N"));
        await traffic.AppendAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                0,
                GatewayTrafficDirections.Inbound,
                "admin",
                JsonContentType,
                payload,
                metadata,
                now,
                requestId),
            cancellationToken);

        ApplicationResponse response;
        try
        {
            response = await requests.SendAsync(request, cancellationToken);
        }
        catch (ApplicationRequestExpiredException exception)
        {
            await AppendFailureAsync(sessionId, requestId, "application-timeout");
            throw new GatewayApplicationException(
                "application-timeout",
                "The application worker did not respond before the request deadline.",
                isUnavailable: true,
                exception);
        }
        catch (ApplicationRequestFailedException exception)
        {
            await AppendFailureAsync(sessionId, requestId, exception.ErrorCode);
            throw new GatewayApplicationException(
                exception.ErrorCode,
                "The application worker could not complete the request.",
                innerException: exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AppendFailureAsync(sessionId, requestId, "gateway-request-cancelled");
            throw;
        }
        catch (Exception exception)
        {
            await AppendFailureAsync(sessionId, requestId, "application-transport-failure");
            throw new GatewayApplicationException(
                "application-transport-failure",
                "The application transport is unavailable.",
                isUnavailable: true,
                exception);
        }

        await AppendOutboundAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                1,
                GatewayTrafficDirections.Outbound,
                "admin",
                response.ContentType,
                response.Payload,
                response.Metadata,
                DateTimeOffset.UtcNow,
                requestId));
        if (response.IsError)
        {
            throw new GatewayApplicationException(
                response.ErrorCode ?? "application-error",
                response.ErrorDetail ?? "The application request was rejected.");
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(response.Payload, JsonOptions)
                ?? throw new JsonException("The application response payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new GatewayApplicationException(
                "invalid-application-response",
                "The application worker returned an invalid response.",
                innerException: exception);
        }
    }

    private async Task AppendFailureAsync(
        Guid sessionId,
        Guid requestId,
        string errorCode)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { code = errorCode },
            JsonOptions);
        await AppendOutboundAsync(
            new GatewayTrafficRecord(
                Guid.CreateVersion7(),
                sessionId,
                1,
                GatewayTrafficDirections.Outbound,
                "admin",
                "application/problem+json",
                payload,
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow,
                requestId));
    }

    private async Task AppendOutboundAsync(GatewayTrafficRecord record)
    {
        using var timeout = new CancellationTokenSource(options.TrafficJournalTimeout);
        try
        {
            await traffic.AppendAsync(record, timeout.Token);
        }
        catch (Exception exception) when (exception is not GatewayApplicationException)
        {
            throw new GatewayApplicationException(
                "traffic-journal-unavailable",
                "The gateway could not durably record the application response.",
                isUnavailable: true,
                exception);
        }
    }
}

public sealed record GatewayApplicationOptions(
    string InstanceId,
    TimeSpan RequestTimeout,
    TimeSpan TrafficJournalTimeout);
