using mk8.email.Contracts.Messaging;
using mk8.email.Dav;
using mk8.email.Gateway.ApplicationBridge;

namespace mk8.email.Gateway.Protocols.Dav;

internal sealed class GatewayDavStore(IGatewayApplicationTransport transport)
{
    public const string SchedulingInboxSlug = "schedule-inbox";
    public const string SchedulingOutboxSlug = "schedule-outbox";

    public async Task<DavUser?> AuthenticateAsync(
        ProtocolAuthentication authentication,
        CancellationToken cancellationToken) =>
        (await SendAsync<DavAuthenticationRequest, DavLookupResult<DavUser>>(
            ApplicationOperations.DavAuthenticate,
            new DavAuthenticationRequest(authentication),
            cancellationToken).ConfigureAwait(false)).Value;

    public async Task EnsureDefaultCollectionsAsync(DavUser user, CancellationToken cancellationToken) =>
        _ = await SendAsync<DavEnsureCollectionsRequest, DavAcknowledgement>(
            ApplicationOperations.DavEnsureCollections,
            new DavEnsureCollectionsRequest(user),
            cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<DavCollection>> GetCollectionsAsync(
        DavUser user,
        DavCollectionKind kind,
        CancellationToken cancellationToken) =>
        SendAsync<DavCollectionListRequest, IReadOnlyList<DavCollection>>(
            ApplicationOperations.DavCollectionsGet,
            new DavCollectionListRequest(user, kind),
            cancellationToken);

    public async Task<DavCollection?> GetCollectionAsync(
        DavUser user,
        DavCollectionKind kind,
        Guid hrefUserId,
        string hrefSlug,
        CancellationToken cancellationToken) =>
        (await SendAsync<DavCollectionLookupRequest, DavLookupResult<DavCollection>>(
            ApplicationOperations.DavCollectionGet,
            new DavCollectionLookupRequest(user, kind, hrefUserId, hrefSlug),
            cancellationToken).ConfigureAwait(false)).Value;

    public Task<DavCollectionWriteResult> CreateCollectionAsync(
        DavUser user,
        DavCollectionKind kind,
        string slug,
        DavCollectionProperties properties,
        CancellationToken cancellationToken) =>
        SendAsync<DavCollectionCreateRequest, DavCollectionWriteResult>(
            ApplicationOperations.DavCollectionCreate,
            new DavCollectionCreateRequest(user, kind, slug, properties),
            cancellationToken);

    public Task<DavCollectionWriteResult> UpdateCollectionAsync(
        DavUser user,
        DavCollection current,
        DavCollectionProperties properties,
        CancellationToken cancellationToken) =>
        SendAsync<DavCollectionUpdateRequest, DavCollectionWriteResult>(
            ApplicationOperations.DavCollectionUpdate,
            new DavCollectionUpdateRequest(user, current, properties),
            cancellationToken);

    public Task<DavCollectionWriteResult> DeleteCollectionAsync(
        DavUser user,
        DavCollection current,
        CancellationToken cancellationToken) =>
        SendAsync<DavCollectionDeleteRequest, DavCollectionWriteResult>(
            ApplicationOperations.DavCollectionDelete,
            new DavCollectionDeleteRequest(user, current),
            cancellationToken);

    public Task<IReadOnlyList<DavPrincipal>> GetPrincipalsAsync(
        DavUser user,
        CancellationToken cancellationToken) =>
        SendAsync<DavPrincipalListRequest, IReadOnlyList<DavPrincipal>>(
            ApplicationOperations.DavPrincipalsGet,
            new DavPrincipalListRequest(user),
            cancellationToken);

    public async Task<DavPrincipal?> GetPrincipalAsync(
        DavUser user,
        Guid principalId,
        CancellationToken cancellationToken) =>
        (await SendAsync<DavPrincipalLookupRequest, DavLookupResult<DavPrincipal>>(
            ApplicationOperations.DavPrincipalGet,
            new DavPrincipalLookupRequest(user, principalId),
            cancellationToken).ConfigureAwait(false)).Value;

    public Task<DavAclWriteResult> ReplaceSharesAsync(
        DavUser user,
        Guid collectionId,
        IReadOnlyList<DavShareGrant> grants,
        CancellationToken cancellationToken) =>
        SendAsync<DavShareReplaceRequest, DavAclWriteResult>(
            ApplicationOperations.DavSharesReplace,
            new DavShareReplaceRequest(user, collectionId, grants),
            cancellationToken);

    public Task<IReadOnlyList<DavResource>> GetResourcesAsync(
        DavUser user,
        DavCollection collection,
        CancellationToken cancellationToken) =>
        SendAsync<DavCollectionReference, IReadOnlyList<DavResource>>(
            ApplicationOperations.DavResourcesGet,
            new DavCollectionReference(user, collection),
            cancellationToken);

    public async Task<DavResource?> GetResourceAsync(
        DavUser user,
        DavCollection collection,
        string resourceName,
        CancellationToken cancellationToken) =>
        (await SendAsync<DavResourceLookupRequest, DavLookupResult<DavResource>>(
            ApplicationOperations.DavResourceGet,
            new DavResourceLookupRequest(
                new DavCollectionReference(user, collection), resourceName),
            cancellationToken).ConfigureAwait(false)).Value;

    public Task<IReadOnlyList<DavChange>> GetChangesAsync(
        DavUser user,
        DavCollection collection,
        long sinceSequence,
        CancellationToken cancellationToken) =>
        SendAsync<DavChangesRequest, IReadOnlyList<DavChange>>(
            ApplicationOperations.DavChangesGet,
            new DavChangesRequest(
                new DavCollectionReference(user, collection), sinceSequence),
            cancellationToken);

    public Task<DavResourceWriteResult> PutResourceAsync(
        DavUser user,
        Guid collectionId,
        string resourceName,
        string uid,
        string contentType,
        byte[] content,
        string? ifMatch,
        bool ifNoneMatchStar,
        CancellationToken cancellationToken) =>
        SendAsync<DavResourcePutRequest, DavResourceWriteResult>(
            ApplicationOperations.DavResourcePut,
            new DavResourcePutRequest(
                user, collectionId, resourceName, uid, contentType,
                content, ifMatch, ifNoneMatchStar),
            cancellationToken);

    public Task<DavResourceWriteResult> DeleteResourceAsync(
        DavUser user,
        Guid collectionId,
        string resourceName,
        string? ifMatch,
        CancellationToken cancellationToken) =>
        SendAsync<DavResourceDeleteRequest, DavResourceWriteResult>(
            ApplicationOperations.DavResourceDelete,
            new DavResourceDeleteRequest(user, collectionId, resourceName, ifMatch),
            cancellationToken);

    public Task<DavScheduleSubmissionResult> SubmitAsync(
        DavUser user,
        IEnumerable<string> originators,
        IEnumerable<string> recipients,
        string? contentType,
        byte[] body,
        string? clientIp,
        CancellationToken cancellationToken) =>
        SendAsync<DavScheduleSubmitRequest, DavScheduleSubmissionResult>(
            ApplicationOperations.DavScheduleSubmit,
            new DavScheduleSubmitRequest(
                user, originators.ToArray(), recipients.ToArray(), contentType, body, clientIp),
            cancellationToken);

    public static string QuoteEtag(string etag) => $"\"{etag}\"";

    public static bool IsSchedulingCollection(string slug) =>
        string.Equals(slug, SchedulingInboxSlug, StringComparison.Ordinal)
        || string.Equals(slug, SchedulingOutboxSlug, StringComparison.Ordinal);

    private Task<TResponse> SendAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TRequest, TResponse>("dav", operation, request, cancellationToken);
}
