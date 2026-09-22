using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.DTOs;
using mk8.email.Contracts.Messaging;
using mk8.email.Dav;

namespace mk8.email.Application.Services;

public sealed class ApplicationRequestDispatcher(IServiceProvider services) : IApplicationRequestDispatcher
{
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IDavApplicationService Dav => services.GetRequiredService<IDavApplicationService>();

    public async Task<ApplicationResponse> DispatchAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ContentType, JsonContentType, StringComparison.OrdinalIgnoreCase))
            return Error(request.Id, "unsupported-media-type", "Application requests must use application/json.");

        try
        {
            return request.Operation switch
            {
                ApplicationOperations.SystemPing => Success(
                    request.Id,
                    new SystemPingResult(DateTimeOffset.UtcNow)),
                ApplicationOperations.AdminAuthenticate => Success(
                    request.Id,
                    await services.GetRequiredService<IAuthService>().LoginAsync(
                        Deserialize<LoginRequestDTO>(request))),
                ApplicationOperations.AdminDashboardGet => await GetDashboardAsync(
                    request.Id,
                    cancellationToken),
                ApplicationOperations.AdminDomainsGet => Success(
                    request.Id,
                    await services.GetRequiredService<IMailAdministrationService>()
                        .GetDomainsAsync(cancellationToken)),
                ApplicationOperations.AdminDomainsEnsure => await EnsureDomainAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.AdminDomainsSetCatchAll => await SetCatchAllAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.AdminDomainsSetActive => await SetDomainActiveAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.AdminAccountsGet => Success(
                    request.Id,
                    await services.GetRequiredService<IMailAdministrationService>()
                        .GetAccountsAsync(cancellationToken)),
                ApplicationOperations.AdminAccountsCreate => await CreateAccountAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.AdminAccountsSetActive => await SetAccountActiveAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.AdminAccountsResetPassword => await ResetPasswordAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.OAuthPublicKeyGet => Success(
                    request.Id,
                    await services.GetRequiredService<IOAuthApplicationService>()
                        .GetPublicKeyAsync(cancellationToken)),
                ApplicationOperations.OAuthIdentityAuthenticate => Success(
                    request.Id,
                    await services.GetRequiredService<IOAuthApplicationService>()
                        .AuthenticateIdentityAsync(
                            Deserialize<OAuthIdentityLookupRequest>(request),
                            cancellationToken)),
                ApplicationOperations.OAuthAuthorize => Success(
                    request.Id,
                    await services.GetRequiredService<IOAuthApplicationService>()
                        .AuthorizeAsync(
                            Deserialize<OAuthAuthorizeApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.OAuthAuthorizationCodeRedeem => Success(
                    request.Id,
                    await services.GetRequiredService<IOAuthApplicationService>()
                        .RedeemAuthorizationCodeAsync(
                            Deserialize<OAuthAuthorizationCodeRedeemRequest>(request),
                            cancellationToken)),
                ApplicationOperations.OAuthTokenRefresh => Success(
                    request.Id,
                    await services.GetRequiredService<IOAuthApplicationService>()
                        .RefreshTokenAsync(
                            Deserialize<OAuthRefreshTokenRequest>(request),
                            cancellationToken)),
                ApplicationOperations.OAuthTokenRevoke => await RevokeOAuthTokenAsync(
                    request,
                    cancellationToken),
                ApplicationOperations.JmapSessionGet => Success(
                    request.Id,
                    await services.GetRequiredService<IJmapApplicationService>()
                        .GetSessionAsync(
                            Deserialize<JmapSessionApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.JmapApiProcess => Success(
                    request.Id,
                    await services.GetRequiredService<IJmapApplicationService>()
                        .ProcessApiRequestAsync(
                            Deserialize<JmapApiApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.JmapUpload => Success(
                    request.Id,
                    await services.GetRequiredService<IJmapApplicationService>()
                        .UploadAsync(
                            Deserialize<JmapUploadApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.JmapDownload => Success(
                    request.Id,
                    await services.GetRequiredService<IJmapApplicationService>()
                        .DownloadAsync(
                            Deserialize<JmapDownloadApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.JmapEventPoll => Success(
                    request.Id,
                    await services.GetRequiredService<IJmapApplicationService>()
                        .PollEventAsync(
                            Deserialize<JmapEventApplicationRequest>(request),
                            cancellationToken)),
                ApplicationOperations.DavAuthenticate => Success(request.Id,
                    await Dav.AuthenticateAsync(
                        Deserialize<DavAuthenticationRequest>(request), cancellationToken)),
                ApplicationOperations.DavEnsureCollections => Success(request.Id,
                    await Dav.EnsureCollectionsAsync(
                        Deserialize<DavEnsureCollectionsRequest>(request), cancellationToken)),
                ApplicationOperations.DavCollectionsGet => Success(request.Id,
                    await Dav.GetCollectionsAsync(
                        Deserialize<DavCollectionListRequest>(request), cancellationToken)),
                ApplicationOperations.DavCollectionGet => Success(request.Id,
                    await Dav.GetCollectionAsync(
                        Deserialize<DavCollectionLookupRequest>(request), cancellationToken)),
                ApplicationOperations.DavCollectionCreate => Success(request.Id,
                    await Dav.CreateCollectionAsync(
                        Deserialize<DavCollectionCreateRequest>(request), cancellationToken)),
                ApplicationOperations.DavCollectionUpdate => Success(request.Id,
                    await Dav.UpdateCollectionAsync(
                        Deserialize<DavCollectionUpdateRequest>(request), cancellationToken)),
                ApplicationOperations.DavCollectionDelete => Success(request.Id,
                    await Dav.DeleteCollectionAsync(
                        Deserialize<DavCollectionDeleteRequest>(request), cancellationToken)),
                ApplicationOperations.DavPrincipalsGet => Success(request.Id,
                    await Dav.GetPrincipalsAsync(
                        Deserialize<DavPrincipalListRequest>(request), cancellationToken)),
                ApplicationOperations.DavPrincipalGet => Success(request.Id,
                    await Dav.GetPrincipalAsync(
                        Deserialize<DavPrincipalLookupRequest>(request), cancellationToken)),
                ApplicationOperations.DavSharesReplace => Success(request.Id,
                    await Dav.ReplaceSharesAsync(
                        Deserialize<DavShareReplaceRequest>(request), cancellationToken)),
                ApplicationOperations.DavResourcesGet => Success(request.Id,
                    await Dav.GetResourcesAsync(
                        Deserialize<DavCollectionReference>(request), cancellationToken)),
                ApplicationOperations.DavResourceGet => Success(request.Id,
                    await Dav.GetResourceAsync(
                        Deserialize<DavResourceLookupRequest>(request), cancellationToken)),
                ApplicationOperations.DavChangesGet => Success(request.Id,
                    await Dav.GetChangesAsync(
                        Deserialize<DavChangesRequest>(request), cancellationToken)),
                ApplicationOperations.DavResourcePut => Success(request.Id,
                    await Dav.PutResourceAsync(
                        Deserialize<DavResourcePutRequest>(request), cancellationToken)),
                ApplicationOperations.DavResourceDelete => Success(request.Id,
                    await Dav.DeleteResourceAsync(
                        Deserialize<DavResourceDeleteRequest>(request), cancellationToken)),
                ApplicationOperations.DavScheduleSubmit => Success(request.Id,
                    await Dav.SubmitScheduleAsync(
                        Deserialize<DavScheduleSubmitRequest>(request), cancellationToken)),
                _ => Error(request.Id, "unknown-operation", "The application operation is not supported."),
            };
        }
        catch (JsonException)
        {
            return Error(request.Id, "invalid-arguments", "The application request payload is invalid.");
        }
    }

    private async Task<ApplicationResponse> GetDashboardAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var administration = services.GetRequiredService<IMailAdministrationService>();
        var domains = await administration.GetDomainsAsync(cancellationToken);
        var accounts = await administration.GetAccountsAsync(cancellationToken);
        var status = await services.GetRequiredService<IMailSystemStatusService>()
            .GetStatusAsync(cancellationToken);
        return Success(
            requestId,
            new AdminDashboardDTO(
                domains,
                accounts,
                status));
    }

    private async Task<ApplicationResponse> EnsureDomainAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminEnsureDomainRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .EnsureDomainAsync(value.CompanyName, value.Domain, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> SetCatchAllAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminSetCatchAllRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .SetCatchAllAsync(value.Domain, value.TargetAddress, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> SetDomainActiveAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminSetDomainActiveRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .SetDomainActiveAsync(value.Domain, value.IsActive, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> CreateAccountAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminCreateAccountRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .CreateAccountAsync(value.Address, value.Password, value.Role, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> SetAccountActiveAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminSetAccountActiveRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .SetAccountActiveAsync(value.UserId, value.IsActive, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> ResetPasswordAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var value = Deserialize<AdminResetPasswordRequest>(request);
        var result = await services.GetRequiredService<IMailAdministrationService>()
            .ResetPasswordAsync(value.UserId, value.Password, cancellationToken);
        return Success(request.Id, result);
    }

    private async Task<ApplicationResponse> RevokeOAuthTokenAsync(
        ApplicationRequest request,
        CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IOAuthApplicationService>()
            .RevokeTokenAsync(
                Deserialize<OAuthRevokeTokenRequest>(request),
                cancellationToken);
        return Success(request.Id, new OAuthTokenRevocationResult(true));
    }

    private static T Deserialize<T>(ApplicationRequest request) =>
        JsonSerializer.Deserialize<T>(request.Payload, JsonOptions)
        ?? throw new JsonException("The application request payload is empty.");

    private static ApplicationResponse Success<T>(Guid requestId, T value) => new(
        requestId,
        JsonContentType,
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions),
        new Dictionary<string, string>());

    private static ApplicationResponse Error(Guid requestId, string code, string detail) => new(
        requestId,
        "application/problem+json",
        JsonSerializer.SerializeToUtf8Bytes(new { code, detail }, JsonOptions),
        new Dictionary<string, string>(),
        IsError: true,
        ErrorCode: code,
        ErrorDetail: detail);
}
