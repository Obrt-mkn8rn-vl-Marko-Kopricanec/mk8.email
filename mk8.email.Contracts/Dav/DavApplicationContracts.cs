using mk8.email.Contracts.Messaging;

namespace mk8.email.Dav;

public sealed record DavUser(Guid Id, string Username);

public sealed record DavLookupResult<T>(T? Value) where T : class;

public sealed record DavAcknowledgement(bool Succeeded);

public sealed record DavAuthenticationRequest(ProtocolAuthentication Authentication);

public sealed record DavEnsureCollectionsRequest(DavUser User);

public sealed record DavCollectionListRequest(DavUser User, DavCollectionKind Kind);

public sealed record DavCollectionLookupRequest(
    DavUser User,
    DavCollectionKind Kind,
    Guid HrefUserId,
    string HrefSlug);

public sealed record DavCollectionCreateRequest(
    DavUser User,
    DavCollectionKind Kind,
    string Slug,
    DavCollectionProperties Properties);

public sealed record DavCollectionUpdateRequest(
    DavUser User,
    DavCollection Current,
    DavCollectionProperties Properties);

public sealed record DavCollectionDeleteRequest(DavUser User, DavCollection Current);

public sealed record DavPrincipalListRequest(DavUser User);

public sealed record DavPrincipalLookupRequest(DavUser User, Guid PrincipalId);

public sealed record DavShareReplaceRequest(
    DavUser User,
    Guid CollectionId,
    IReadOnlyList<DavShareGrant> Grants);

public sealed record DavCollectionReference(DavUser User, DavCollection Collection);

public sealed record DavResourceLookupRequest(
    DavCollectionReference Reference,
    string ResourceName);

public sealed record DavChangesRequest(
    DavCollectionReference Reference,
    long SinceSequence);

public sealed record DavResourcePutRequest(
    DavUser User,
    Guid CollectionId,
    string ResourceName,
    string Uid,
    string ContentType,
    byte[] Content,
    string? IfMatch,
    bool IfNoneMatchStar);

public sealed record DavResourceDeleteRequest(
    DavUser User,
    Guid CollectionId,
    string ResourceName,
    string? IfMatch);

public sealed record DavScheduleSubmitRequest(
    DavUser User,
    string[] Originators,
    string[] Recipients,
    string? ContentType,
    byte[] Body,
    string? ClientIp);
