using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Dav;

internal sealed class DavApplicationService(
    DavStore store,
    DavSchedulingService scheduling,
    IMailAuthenticator authenticator,
    IServiceProvider services) : IDavApplicationService
{
    public async Task<DavLookupResult<DavUser>> AuthenticateAsync(
        DavAuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        var authentication = request.Authentication;
        AuthenticatedMailUser? user = authentication.Kind switch
        {
            ProtocolAuthenticationKinds.Password when authentication.Username is not null =>
                await authenticator.AuthenticateAsync(
                    authentication.Username, authentication.Secret, cancellationToken).ConfigureAwait(false),
            ProtocolAuthenticationKinds.BearerToken =>
                await AuthenticateBearerAsync(authentication.Secret, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
        return new DavLookupResult<DavUser>(user is null
            ? null
            : new DavUser(user.Id, user.Username));
    }

    public async Task<DavAcknowledgement> EnsureCollectionsAsync(
        DavEnsureCollectionsRequest request,
        CancellationToken cancellationToken)
    {
        await store.EnsureDefaultCollectionsAsync(ToAuthenticatedUser(request.User), cancellationToken).ConfigureAwait(false);
        return new DavAcknowledgement(true);
    }

    public Task<IReadOnlyList<DavCollection>> GetCollectionsAsync(
        DavCollectionListRequest request,
        CancellationToken cancellationToken) =>
        store.GetCollectionsAsync(ToAuthenticatedUser(request.User), request.Kind, cancellationToken);

    public async Task<DavLookupResult<DavCollection>> GetCollectionAsync(
        DavCollectionLookupRequest request,
        CancellationToken cancellationToken) =>
        new(await store.GetCollectionAsync(
            ToAuthenticatedUser(request.User),
            request.Kind,
            request.HrefUserId,
            request.HrefSlug,
            cancellationToken).ConfigureAwait(false));

    public Task<DavCollectionWriteResult> CreateCollectionAsync(
        DavCollectionCreateRequest request,
        CancellationToken cancellationToken) =>
        store.CreateCollectionAsync(
            ToAuthenticatedUser(request.User),
            request.Kind,
            request.Slug,
            request.Properties,
            cancellationToken);

    public Task<DavCollectionWriteResult> UpdateCollectionAsync(
        DavCollectionUpdateRequest request,
        CancellationToken cancellationToken) =>
        store.UpdateCollectionAsync(
            ToAuthenticatedUser(request.User),
            request.Current,
            request.Properties,
            cancellationToken);

    public Task<DavCollectionWriteResult> DeleteCollectionAsync(
        DavCollectionDeleteRequest request,
        CancellationToken cancellationToken) =>
        store.DeleteCollectionAsync(
            ToAuthenticatedUser(request.User),
            request.Current,
            cancellationToken);

    public Task<IReadOnlyList<DavPrincipal>> GetPrincipalsAsync(
        DavPrincipalListRequest request,
        CancellationToken cancellationToken) =>
        store.GetPrincipalsAsync(ToAuthenticatedUser(request.User), cancellationToken);

    public async Task<DavLookupResult<DavPrincipal>> GetPrincipalAsync(
        DavPrincipalLookupRequest request,
        CancellationToken cancellationToken) =>
        new(await store.GetPrincipalAsync(
            ToAuthenticatedUser(request.User), request.PrincipalId, cancellationToken).ConfigureAwait(false));

    public Task<DavAclWriteResult> ReplaceSharesAsync(
        DavShareReplaceRequest request,
        CancellationToken cancellationToken) =>
        store.ReplaceSharesAsync(
            ToAuthenticatedUser(request.User),
            request.CollectionId,
            request.Grants,
            cancellationToken);

    public async Task<IReadOnlyList<DavResource>> GetResourcesAsync(
        DavCollectionReference request,
        CancellationToken cancellationToken) =>
        await ResolveCollectionAsync(request, cancellationToken).ConfigureAwait(false) is null
            ? []
            : await store.GetResourcesAsync(request.Collection.Id, cancellationToken).ConfigureAwait(false);

    public async Task<DavLookupResult<DavResource>> GetResourceAsync(
        DavResourceLookupRequest request,
        CancellationToken cancellationToken) =>
        new(await ResolveCollectionAsync(request.Reference, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await store.GetResourceAsync(
                request.Reference.Collection.Id,
                request.ResourceName,
                cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<DavChange>> GetChangesAsync(
        DavChangesRequest request,
        CancellationToken cancellationToken) =>
        await ResolveCollectionAsync(request.Reference, cancellationToken).ConfigureAwait(false) is null
            ? []
            : await store.GetChangesAsync(
                request.Reference.Collection.Id,
                request.SinceSequence,
                cancellationToken).ConfigureAwait(false);

    public Task<DavResourceWriteResult> PutResourceAsync(
        DavResourcePutRequest request,
        CancellationToken cancellationToken) =>
        store.PutResourceAsync(
            ToAuthenticatedUser(request.User),
            request.CollectionId,
            request.ResourceName,
            request.Uid,
            request.ContentType,
            request.Content,
            request.IfMatch,
            request.IfNoneMatchStar,
            cancellationToken);

    public Task<DavResourceWriteResult> DeleteResourceAsync(
        DavResourceDeleteRequest request,
        CancellationToken cancellationToken) =>
        store.DeleteResourceAsync(
            ToAuthenticatedUser(request.User),
            request.CollectionId,
            request.ResourceName,
            request.IfMatch,
            cancellationToken);

    public Task<DavScheduleSubmissionResult> SubmitScheduleAsync(
        DavScheduleSubmitRequest request,
        CancellationToken cancellationToken) =>
        scheduling.SubmitAsync(
            ToAuthenticatedUser(request.User),
            request.Originators,
            request.Recipients,
            request.ContentType,
            request.Body,
            request.ClientIp,
            cancellationToken);

    private async Task<DavCollection?> ResolveCollectionAsync(
        DavCollectionReference reference,
        CancellationToken cancellationToken)
    {
        var requested = reference.Collection;
        var accessible = await store.GetCollectionAsync(
            ToAuthenticatedUser(reference.User),
            requested.Kind,
            requested.HrefUserId,
            requested.HrefSlug,
            cancellationToken).ConfigureAwait(false);
        return accessible?.Id == requested.Id ? accessible : null;
    }

    private static AuthenticatedMailUser ToAuthenticatedUser(DavUser user) =>
        new(user.Id, user.Username);

    private Task<AuthenticatedMailUser?> AuthenticateBearerAsync(
        string token,
        CancellationToken cancellationToken) =>
        services.GetService(typeof(IOAuthTokenService)) is IOAuthTokenService tokens
            ? tokens.AuthenticateAccessTokenAsync(token, "dav", cancellationToken)
            : Task.FromResult<AuthenticatedMailUser?>(null);
}
