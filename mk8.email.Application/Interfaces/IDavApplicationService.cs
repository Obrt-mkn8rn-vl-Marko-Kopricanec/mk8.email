using mk8.email.Dav;

namespace mk8.email.Application.Interfaces;

public interface IDavApplicationService
{
    Task<DavLookupResult<DavUser>> AuthenticateAsync(
        DavAuthenticationRequest request, CancellationToken cancellationToken);

    Task<DavAcknowledgement> EnsureCollectionsAsync(
        DavEnsureCollectionsRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DavCollection>> GetCollectionsAsync(
        DavCollectionListRequest request, CancellationToken cancellationToken);

    Task<DavLookupResult<DavCollection>> GetCollectionAsync(
        DavCollectionLookupRequest request, CancellationToken cancellationToken);

    Task<DavCollectionWriteResult> CreateCollectionAsync(
        DavCollectionCreateRequest request, CancellationToken cancellationToken);

    Task<DavCollectionWriteResult> UpdateCollectionAsync(
        DavCollectionUpdateRequest request, CancellationToken cancellationToken);

    Task<DavCollectionWriteResult> DeleteCollectionAsync(
        DavCollectionDeleteRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DavPrincipal>> GetPrincipalsAsync(
        DavPrincipalListRequest request, CancellationToken cancellationToken);

    Task<DavLookupResult<DavPrincipal>> GetPrincipalAsync(
        DavPrincipalLookupRequest request, CancellationToken cancellationToken);

    Task<DavAclWriteResult> ReplaceSharesAsync(
        DavShareReplaceRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DavResource>> GetResourcesAsync(
        DavCollectionReference request, CancellationToken cancellationToken);

    Task<DavLookupResult<DavResource>> GetResourceAsync(
        DavResourceLookupRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DavChange>> GetChangesAsync(
        DavChangesRequest request, CancellationToken cancellationToken);

    Task<DavResourceWriteResult> PutResourceAsync(
        DavResourcePutRequest request, CancellationToken cancellationToken);

    Task<DavResourceWriteResult> DeleteResourceAsync(
        DavResourceDeleteRequest request, CancellationToken cancellationToken);

    Task<DavScheduleSubmissionResult> SubmitScheduleAsync(
        DavScheduleSubmitRequest request, CancellationToken cancellationToken);
}
