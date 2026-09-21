using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Environment;

namespace mk8.email.Dav;

public static class DavEndpointRouteBuilderExtensions
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    private static readonly XNamespace AppleIcal = "http://apple.com/ns/ical/";
    private static readonly string[] DavMethods =
    [
        "OPTIONS", "PROPFIND", "PROPPATCH", "REPORT", "GET", "HEAD", "PUT",
        "DELETE", "MKCOL", "MKCALENDAR",
    ];

    public static IEndpointRouteBuilder MapDavEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/.well-known/caldav", DavMethods, RedirectToDavAsync);
        endpoints.MapMethods("/.well-known/carddav", DavMethods, RedirectToDavAsync);
        endpoints.MapMethods("/dav/{**path}", DavMethods, HandleDavAsync);
        return endpoints;
    }

    private static Task RedirectToDavAsync(HttpContext context)
    {
        SetDavHeaders(context.Response);
        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        context.Response.Headers.Location = "/dav/";
        return Task.CompletedTask;
    }

    private static async Task HandleDavAsync(
        HttpContext context,
        IMailAuthenticator authenticator,
        DavStore store,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        SetDavHeaders(context.Response);
        if (context.Request.Path == "/dav")
        {
            await RedirectToDavAsync(context);
            return;
        }
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var user = await DavHttpAuthentication.AuthenticateAsync(
            context,
            authenticator,
            cancellationToken);
        if (user is null)
        {
            await DavHttpAuthentication.WriteUnauthorizedAsync(context, cancellationToken);
            return;
        }

        if (!TryParsePath(context.Request.Path, out var path)
            || (path.UserId is not null && path.UserId != user.Id))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (context.Request.Method.ToUpperInvariant())
        {
            case "PROPFIND":
                await HandlePropfindAsync(context, user, path, store, environment, cancellationToken);
                break;
            case "PROPPATCH":
                await HandleProppatchAsync(context, user, path, store, cancellationToken);
                break;
            case "REPORT":
                await HandleReportAsync(context, user, path, store, cancellationToken);
                break;
            case "GET":
                await HandleGetAsync(context, user, path, store, writeBody: true, cancellationToken);
                break;
            case "HEAD":
                await HandleGetAsync(context, user, path, store, writeBody: false, cancellationToken);
                break;
            case "PUT":
                await HandlePutAsync(context, user, path, store, environment, cancellationToken);
                break;
            case "DELETE":
                await HandleDeleteAsync(context, user, path, store, cancellationToken);
                break;
            case "MKCOL":
            case "MKCALENDAR":
                await HandleMakeCollectionAsync(context, user, path, store, cancellationToken);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                break;
        }
    }

    private static async Task HandlePropfindAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        var depth = context.Request.Headers["Depth"].ToString();
        if (string.IsNullOrEmpty(depth))
            depth = "infinity";
        if (depth is not ("0" or "1"))
        {
            await WriteDavErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                Dav + "propfind-finite-depth",
                cancellationToken);
            return;
        }

        var document = await ReadXmlBodyAsync(context.Request, 1_048_576, cancellationToken);
        if (document.IsInvalid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var requested = GetRequestedProperties(document.Value);
        await store.EnsureDefaultCollectionsAsync(user, cancellationToken);
        var responses = new List<XElement>();

        switch (path.Kind)
        {
            case DavPathKind.Root:
                responses.Add(CreatePropertyResponse(
                    RootHref,
                    RootProperties(user),
                    requested));
                if (depth == "1")
                {
                    responses.Add(CreatePropertyResponse(
                        PrincipalHref(user.Id),
                        PrincipalProperties(user),
                        requested));
                    responses.Add(CreatePropertyResponse(
                        HomeHref(DavCollectionKind.Calendar, user.Id),
                        HomeProperties(user, DavCollectionKind.Calendar),
                        requested));
                    responses.Add(CreatePropertyResponse(
                        HomeHref(DavCollectionKind.AddressBook, user.Id),
                        HomeProperties(user, DavCollectionKind.AddressBook),
                        requested));
                }
                break;

            case DavPathKind.Principal:
                responses.Add(CreatePropertyResponse(
                    PrincipalHref(user.Id),
                    PrincipalProperties(user),
                    requested));
                break;

            case DavPathKind.Home:
                responses.Add(CreatePropertyResponse(
                    HomeHref(path.CollectionKind!.Value, user.Id),
                    HomeProperties(user, path.CollectionKind.Value),
                    requested));
                if (depth == "1")
                {
                    var collections = await store.GetCollectionsAsync(
                        user,
                        path.CollectionKind.Value,
                        cancellationToken);
                    foreach (var collection in collections)
                    {
                        responses.Add(CreatePropertyResponse(
                            CollectionHref(collection),
                            CollectionProperties(collection, environment),
                            requested));
                    }
                }
                break;

            case DavPathKind.Collection:
                {
                    var collection = await store.GetCollectionAsync(
                        user,
                        path.CollectionKind!.Value,
                        path.Slug!,
                        cancellationToken);
                    if (collection is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    responses.Add(CreatePropertyResponse(
                        CollectionHref(collection),
                        CollectionProperties(collection, environment),
                        requested));
                    if (depth == "1")
                    {
                        var resources = await store.GetResourcesAsync(
                            collection.Id,
                            cancellationToken);
                        foreach (var resource in resources)
                        {
                            responses.Add(CreatePropertyResponse(
                                ResourceHref(collection, resource.ResourceName),
                                ResourceProperties(resource, includeData: false),
                                requested));
                        }
                    }
                    break;
                }

            case DavPathKind.Resource:
                {
                    var resolved = await ResolveResourceAsync(user, path, store, cancellationToken);
                    if (resolved is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    responses.Add(CreatePropertyResponse(
                        ResourceHref(resolved.Value.Collection, path.ResourceName!),
                        ResourceProperties(resolved.Value.Resource, includeData: true),
                        requested));
                    break;
                }
        }

        await WriteMultiStatusAsync(context, responses, null, cancellationToken);
    }

    private static async Task HandleReportAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        CancellationToken cancellationToken)
    {
        if (path.Kind != DavPathKind.Collection)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var collection = await store.GetCollectionAsync(
            user,
            path.CollectionKind!.Value,
            path.Slug!,
            cancellationToken);
        if (collection is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var parsed = await ReadXmlBodyAsync(context.Request, 1_048_576, cancellationToken);
        if (parsed.IsInvalid || parsed.Value?.Root is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var root = parsed.Value.Root;
        if (root.Name == Dav + "sync-collection")
        {
            await HandleSyncCollectionReportAsync(
                context,
                collection,
                root,
                store,
                cancellationToken);
            return;
        }
        if (root.Name == CalDav + "calendar-multiget"
            || root.Name == CardDav + "addressbook-multiget")
        {
            await HandleMultigetReportAsync(
                context,
                collection,
                root,
                store,
                cancellationToken);
            return;
        }
        if (root.Name == CalDav + "calendar-query"
            || root.Name == CardDav + "addressbook-query")
        {
            await HandleQueryReportAsync(
                context,
                collection,
                root,
                store,
                cancellationToken);
            return;
        }

        await WriteDavErrorAsync(
            context,
            StatusCodes.Status403Forbidden,
            Dav + "supported-report",
            cancellationToken);
    }

    private static async Task HandleSyncCollectionReportAsync(
        HttpContext context,
        DavCollection collection,
        XElement root,
        DavStore store,
        CancellationToken cancellationToken)
    {
        var tokenValue = root.Element(Dav + "sync-token")?.Value.Trim() ?? string.Empty;
        long since;
        if (tokenValue.Length == 0)
        {
            since = 0;
        }
        else if (!TryParseSyncToken(collection, tokenValue, out since)
            || since > collection.SyncToken)
        {
            await WriteDavErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                Dav + "valid-sync-token",
                cancellationToken);
            return;
        }

        var requested = GetRequestedProperties(new XDocument(root));
        if (requested is { Count: 0 })
            requested = new HashSet<XName> { Dav + "getetag" };
        var responses = new List<XElement>();
        if (since == 0)
        {
            foreach (var resource in await store.GetResourcesAsync(collection.Id, cancellationToken))
            {
                responses.Add(CreatePropertyResponse(
                    ResourceHref(collection, resource.ResourceName),
                    ResourceProperties(resource, includeData: true),
                    requested));
            }
        }
        else
        {
            var changes = await store.GetChangesAsync(collection.Id, since, cancellationToken);
            var latestChanges = changes
                .GroupBy(change => change.ResourceName, StringComparer.Ordinal)
                .Select(group => group.MaxBy(change => change.Sequence)!)
                .OrderBy(change => change.Sequence);
            foreach (var change in latestChanges)
            {
                var href = ResourceHref(collection, change.ResourceName);
                if (change.IsDeleted)
                {
                    responses.Add(CreateStatusResponse(href, StatusCodes.Status404NotFound));
                    continue;
                }
                var resource = await store.GetResourceAsync(
                    collection.Id,
                    change.ResourceName,
                    cancellationToken);
                responses.Add(resource is null
                    ? CreateStatusResponse(href, StatusCodes.Status404NotFound)
                    : CreatePropertyResponse(
                        href,
                        ResourceProperties(resource, includeData: true),
                        requested));
            }
        }

        await WriteMultiStatusAsync(
            context,
            responses,
            SyncToken(collection),
            cancellationToken);
    }

    private static async Task HandleMultigetReportAsync(
        HttpContext context,
        DavCollection collection,
        XElement root,
        DavStore store,
        CancellationToken cancellationToken)
    {
        var requested = GetRequestedProperties(new XDocument(root));
        var responses = new List<XElement>();
        foreach (var hrefElement in root.Elements(Dav + "href").Take(10_000))
        {
            if (!TryGetResourceNameFromHref(collection, hrefElement.Value, out var resourceName))
                continue;
            var href = ResourceHref(collection, resourceName);
            var resource = await store.GetResourceAsync(
                collection.Id,
                resourceName,
                cancellationToken);
            responses.Add(resource is null
                ? CreateStatusResponse(href, StatusCodes.Status404NotFound)
                : CreatePropertyResponse(
                    href,
                    ResourceProperties(resource, includeData: true),
                    requested));
        }
        await WriteMultiStatusAsync(context, responses, null, cancellationToken);
    }

    private static async Task HandleQueryReportAsync(
        HttpContext context,
        DavCollection collection,
        XElement root,
        DavStore store,
        CancellationToken cancellationToken)
    {
        var requested = GetRequestedProperties(new XDocument(root));
        var resources = await store.GetResourcesAsync(collection.Id, cancellationToken);
        var responses = new List<XElement>();
        foreach (var resource in resources)
        {
            if (!MatchesQuery(collection.Kind, resource, root))
                continue;
            responses.Add(CreatePropertyResponse(
                ResourceHref(collection, resource.ResourceName),
                ResourceProperties(resource, includeData: true),
                requested));
        }
        await WriteMultiStatusAsync(context, responses, null, cancellationToken);
    }

    private static async Task HandleGetAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        bool writeBody,
        CancellationToken cancellationToken)
    {
        if (path.Kind != DavPathKind.Resource)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var resolved = await ResolveResourceAsync(user, path, store, cancellationToken);
        if (resolved is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var resource = resolved.Value.Resource;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = resource.ContentType;
        context.Response.ContentLength = resource.SizeBytes;
        context.Response.Headers.ETag = DavStore.QuoteEtag(resource.Etag);
        context.Response.Headers.LastModified = resource.UpdatedAt.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "private, no-cache";
        if (writeBody)
            await context.Response.Body.WriteAsync(resource.Content, cancellationToken);
    }

    private static async Task HandlePutAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        EnvironmentConfig environment,
        CancellationToken cancellationToken)
    {
        if (path.Kind != DavPathKind.Resource
            || !HasExpectedExtension(path.CollectionKind!.Value, path.ResourceName!))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        if (context.Request.ContentLength > environment.Dav.MaxResourceSizeBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        var body = await ReadBodyAsync(
            context.Request,
            environment.Dav.MaxResourceSizeBytes,
            cancellationToken);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (!DavContent.TryValidate(
                path.CollectionKind.Value,
                context.Request.ContentType,
                body,
                out var contentInfo,
                out var failure))
        {
            await WriteTextErrorAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                failure,
                cancellationToken);
            return;
        }

        var collection = await store.GetCollectionAsync(
            user,
            path.CollectionKind.Value,
            path.Slug!,
            cancellationToken);
        if (collection is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        if (collection.Kind == DavCollectionKind.Calendar
            && contentInfo!.Components.Any(component =>
                component != "VFREEBUSY" && !collection.Components.Contains(component, StringComparer.Ordinal)))
        {
            await WriteDavErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                CalDav + "supported-calendar-component",
                cancellationToken);
            return;
        }

        var result = await store.PutResourceAsync(
            user,
            path.CollectionKind.Value,
            path.Slug!,
            path.ResourceName!,
            contentInfo!.Uid,
            contentInfo.ContentType,
            body,
            context.Request.Headers.IfMatch.ToString(),
            context.Request.Headers.IfNoneMatch.Any(value => value == "*"),
            cancellationToken);
        switch (result.Status)
        {
            case DavResourceWriteStatus.Created:
                context.Response.StatusCode = StatusCodes.Status201Created;
                break;
            case DavResourceWriteStatus.Updated:
            case DavResourceWriteStatus.Unchanged:
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case DavResourceWriteStatus.PreconditionFailed:
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            case DavResourceWriteStatus.UidConflict:
                await WriteDavErrorAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    path.CollectionKind == DavCollectionKind.Calendar
                        ? CalDav + "no-uid-conflict"
                        : CardDav + "no-uid-conflict",
                    cancellationToken);
                return;
            case DavResourceWriteStatus.LimitExceeded:
                context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
        }
        context.Response.Headers.ETag = DavStore.QuoteEtag(result.Resource!.Etag);
        context.Response.Headers.Location = ResourceHref(collection, path.ResourceName!);
    }

    private static async Task HandleDeleteAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        CancellationToken cancellationToken)
    {
        if (path.Kind == DavPathKind.Resource)
        {
            var result = await store.DeleteResourceAsync(
                user,
                path.CollectionKind!.Value,
                path.Slug!,
                path.ResourceName!,
                context.Request.Headers.IfMatch.ToString(),
                cancellationToken);
            context.Response.StatusCode = result.Status switch
            {
                DavResourceWriteStatus.Updated => StatusCodes.Status204NoContent,
                DavResourceWriteStatus.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
                _ => StatusCodes.Status404NotFound,
            };
            return;
        }
        if (path.Kind == DavPathKind.Collection)
        {
            var result = await store.DeleteCollectionAsync(
                user,
                path.CollectionKind!.Value,
                path.Slug!,
                cancellationToken);
            context.Response.StatusCode = result.Status switch
            {
                DavCollectionWriteStatus.Updated => StatusCodes.Status204NoContent,
                DavCollectionWriteStatus.Protected => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status404NotFound,
            };
            return;
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    private static async Task HandleMakeCollectionAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        CancellationToken cancellationToken)
    {
        if (path.Kind != DavPathKind.Collection)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        if (context.Request.Method == "MKCALENDAR"
            && path.CollectionKind != DavCollectionKind.Calendar)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var parsed = await ReadXmlBodyAsync(context.Request, 1_048_576, cancellationToken);
        if (parsed.IsInvalid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var properties = ParseCollectionProperties(
            parsed.Value,
            path.CollectionKind!.Value,
            path.Slug!);
        var result = await store.CreateCollectionAsync(
            user,
            path.CollectionKind.Value,
            path.Slug!,
            properties,
            cancellationToken);
        context.Response.StatusCode = result.Status switch
        {
            DavCollectionWriteStatus.Created => StatusCodes.Status201Created,
            DavCollectionWriteStatus.AlreadyExists => StatusCodes.Status405MethodNotAllowed,
            DavCollectionWriteStatus.LimitExceeded => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status409Conflict,
        };
        if (result.Collection is not null)
            context.Response.Headers.Location = CollectionHref(result.Collection);
    }

    private static async Task HandleProppatchAsync(
        HttpContext context,
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        CancellationToken cancellationToken)
    {
        if (path.Kind != DavPathKind.Collection)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var collection = await store.GetCollectionAsync(
            user,
            path.CollectionKind!.Value,
            path.Slug!,
            cancellationToken);
        if (collection is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var parsed = await ReadXmlBodyAsync(context.Request, 1_048_576, cancellationToken);
        if (parsed.IsInvalid || parsed.Value is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var properties = ParseCollectionProperties(
            parsed.Value,
            collection.Kind,
            collection.DisplayName,
            collection);
        var result = await store.UpdateCollectionAsync(
            user,
            collection.Kind,
            collection.Slug,
            properties,
            cancellationToken);
        if (result.Status != DavCollectionWriteStatus.Updated)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var changedNames = parsed.Value.Descendants(Dav + "prop")
            .Elements()
            .Select(element => element.Name)
            .Distinct()
            .ToHashSet();
        var response = CreatePropertyResponse(
            CollectionHref(result.Collection!),
            CollectionProperties(result.Collection!, null),
            changedNames);
        await WriteMultiStatusAsync(context, [response], null, cancellationToken);
    }

    private static IReadOnlyDictionary<XName, XElement> RootProperties(AuthenticatedMailUser user) =>
        BuildProperties(
            new XElement(Dav + "resourcetype", new XElement(Dav + "collection")),
            new XElement(Dav + "displayname", "mk8.email DAV"),
            HrefProperty(Dav + "current-user-principal", PrincipalHref(user.Id)),
            HrefProperty(Dav + "principal-URL", PrincipalHref(user.Id)),
            HrefProperty(CalDav + "calendar-home-set", HomeHref(DavCollectionKind.Calendar, user.Id)),
            HrefProperty(CardDav + "addressbook-home-set", HomeHref(DavCollectionKind.AddressBook, user.Id)));

    private static IReadOnlyDictionary<XName, XElement> PrincipalProperties(AuthenticatedMailUser user) =>
        BuildProperties(
            new XElement(Dav + "resourcetype", new XElement(Dav + "collection"), new XElement(Dav + "principal")),
            new XElement(Dav + "displayname", user.Username),
            HrefProperty(Dav + "current-user-principal", PrincipalHref(user.Id)),
            HrefProperty(Dav + "principal-URL", PrincipalHref(user.Id)),
            HrefProperty(CalDav + "calendar-home-set", HomeHref(DavCollectionKind.Calendar, user.Id)),
            HrefProperty(CardDav + "addressbook-home-set", HomeHref(DavCollectionKind.AddressBook, user.Id)),
            new XElement(CalDav + "calendar-user-address-set",
                new XElement(Dav + "href", $"mailto:{user.Username}")));

    private static IReadOnlyDictionary<XName, XElement> HomeProperties(
        AuthenticatedMailUser user,
        DavCollectionKind kind) => BuildProperties(
        new XElement(Dav + "resourcetype", new XElement(Dav + "collection")),
        new XElement(Dav + "displayname", kind == DavCollectionKind.Calendar ? "Calendars" : "Address Books"),
        HrefProperty(Dav + "current-user-principal", PrincipalHref(user.Id)),
        HrefProperty(Dav + "owner", PrincipalHref(user.Id)),
        CurrentUserPrivilegeSet());

    private static IReadOnlyDictionary<XName, XElement> CollectionProperties(
        DavCollection collection,
        EnvironmentConfig? environment) => BuildProperties(
        new XElement(
            Dav + "resourcetype",
            new XElement(Dav + "collection"),
            new XElement(collection.Kind == DavCollectionKind.Calendar
                ? CalDav + "calendar"
                : CardDav + "addressbook")),
        new XElement(Dav + "displayname", collection.DisplayName),
        new XElement(Dav + "sync-token", SyncToken(collection)),
        new XElement(CalendarServer + "getctag", collection.SyncToken.ToString(CultureInfo.InvariantCulture)),
        new XElement(Dav + "getlastmodified", collection.UpdatedAt.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)),
        HrefProperty(Dav + "owner", PrincipalHref(collection.UserId)),
        CurrentUserPrivilegeSet(),
        SupportedReportSet(collection.Kind),
        new XElement(
            collection.Kind == DavCollectionKind.Calendar
                ? CalDav + "calendar-description"
                : CardDav + "addressbook-description",
            collection.Description ?? string.Empty),
        new XElement(AppleIcal + "calendar-color", collection.Color ?? string.Empty),
        new XElement(AppleIcal + "calendar-order", collection.SortOrder.ToString(CultureInfo.InvariantCulture)),
        new XElement(
            CalDav + "supported-calendar-component-set",
            collection.Components.Select(component =>
                new XElement(CalDav + "comp", new XAttribute("name", component)))),
        new XElement(
            collection.Kind == DavCollectionKind.Calendar
                ? CalDav + "max-resource-size"
                : CardDav + "max-resource-size",
            (environment?.Dav.MaxResourceSizeBytes ?? 0).ToString(CultureInfo.InvariantCulture)));

    private static IReadOnlyDictionary<XName, XElement> ResourceProperties(
        DavResource resource,
        bool includeData) => BuildProperties(
        new XElement(Dav + "resourcetype"),
        new XElement(Dav + "getetag", DavStore.QuoteEtag(resource.Etag)),
        new XElement(Dav + "getcontenttype", resource.ContentType),
        new XElement(Dav + "getcontentlength", resource.SizeBytes.ToString(CultureInfo.InvariantCulture)),
        new XElement(Dav + "getlastmodified", resource.UpdatedAt.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)),
        includeData
            ? new XElement(
                resource.ContentType == "text/calendar"
                    ? CalDav + "calendar-data"
                    : CardDav + "address-data",
                Encoding.UTF8.GetString(resource.Content))
            : null);

    private static XElement CreatePropertyResponse(
        string href,
        IReadOnlyDictionary<XName, XElement> available,
        IReadOnlySet<XName>? requested)
    {
        var found = new List<XElement>();
        var missing = new List<XElement>();
        var names = requested ?? available.Keys.ToHashSet();
        foreach (var name in names)
        {
            if (available.TryGetValue(name, out var value))
                found.Add(new XElement(value));
            else
                missing.Add(new XElement(name));
        }

        var response = new XElement(Dav + "response", new XElement(Dav + "href", href));
        if (found.Count > 0)
            response.Add(PropertyStatus(found, StatusCodes.Status200OK));
        if (missing.Count > 0)
            response.Add(PropertyStatus(missing, StatusCodes.Status404NotFound));
        return response;
    }

    private static XElement CreateStatusResponse(string href, int status) =>
        new(Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(Dav + "status", HttpStatus(status)));

    private static XElement PropertyStatus(IEnumerable<XElement> properties, int status) =>
        new(Dav + "propstat",
            new XElement(Dav + "prop", properties),
            new XElement(Dav + "status", HttpStatus(status)));

    private static XElement SupportedReportSet(DavCollectionKind kind)
    {
        var reports = new List<XName>
        {
            Dav + "sync-collection",
            kind == DavCollectionKind.Calendar
                ? CalDav + "calendar-query"
                : CardDav + "addressbook-query",
            kind == DavCollectionKind.Calendar
                ? CalDav + "calendar-multiget"
                : CardDav + "addressbook-multiget",
        };
        return new XElement(
            Dav + "supported-report-set",
            reports.Select(report => new XElement(
                Dav + "supported-report",
                new XElement(Dav + "report", new XElement(report)))));
    }

    private static XElement CurrentUserPrivilegeSet() =>
        new(Dav + "current-user-privilege-set",
            Privilege(Dav + "read"),
            Privilege(Dav + "write"),
            Privilege(Dav + "write-content"),
            Privilege(Dav + "write-properties"),
            Privilege(Dav + "bind"),
            Privilege(Dav + "unbind"));

    private static XElement Privilege(XName name) =>
        new(Dav + "privilege", new XElement(name));

    private static XElement HrefProperty(XName name, string href) =>
        new(name, new XElement(Dav + "href", href));

    private static IReadOnlyDictionary<XName, XElement> BuildProperties(params XElement?[] values) =>
        values.Where(value => value is not null).ToDictionary(value => value!.Name, value => value!);

    private static IReadOnlySet<XName>? GetRequestedProperties(XDocument? document)
    {
        if (document?.Root is null
            || document.Root.Element(Dav + "allprop") is not null
            || document.Root.Name == Dav + "allprop")
        {
            return null;
        }
        return document.Root.Descendants(Dav + "prop")
            .Elements()
            .Select(element => element.Name)
            .ToHashSet();
    }

    private static DavCollectionProperties ParseCollectionProperties(
        XDocument? document,
        DavCollectionKind kind,
        string fallbackDisplayName,
        DavCollection? existing = null)
    {
        var properties = document?.Descendants(Dav + "prop").Elements().ToList() ?? [];
        var displayName = PropertyValue(properties, Dav + "displayname")
            ?? existing?.DisplayName
            ?? fallbackDisplayName;
        var descriptionName = kind == DavCollectionKind.Calendar
            ? CalDav + "calendar-description"
            : CardDav + "addressbook-description";
        var description = PropertyValue(properties, descriptionName) ?? existing?.Description;
        var color = PropertyValue(properties, AppleIcal + "calendar-color") ?? existing?.Color;
        var orderText = PropertyValue(properties, AppleIcal + "calendar-order");
        var sortOrder = int.TryParse(orderText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order)
            ? order
            : existing?.SortOrder ?? 0;
        var components = properties
            .Where(property => property.Name == CalDav + "supported-calendar-component-set")
            .Descendants(CalDav + "comp")
            .Select(component => component.Attribute("name")?.Value ?? string.Empty)
            .Where(component => component.Length > 0)
            .ToArray();
        if (components.Length == 0)
            components = existing?.Components ?? ["VEVENT", "VTODO", "VJOURNAL"];
        return new DavCollectionProperties(
            Limit(displayName, 255),
            LimitNullable(description, 1024),
            LimitNullable(color, 32),
            sortOrder,
            components);
    }

    private static string? PropertyValue(IEnumerable<XElement> properties, XName name) =>
        properties.LastOrDefault(property => property.Name == name)?.Value;

    private static bool MatchesQuery(
        DavCollectionKind kind,
        DavResource resource,
        XElement report)
    {
        if (!DavContent.TryValidate(
                kind,
                resource.ContentType,
                resource.Content,
                out var content,
                out _))
        {
            return false;
        }
        if (kind == DavCollectionKind.Calendar)
        {
            var requestedComponents = report
                .Descendants(CalDav + "comp-filter")
                .Select(filter => filter.Attribute("name")?.Value?.ToUpperInvariant())
                .Where(name => name is "VEVENT" or "VTODO" or "VJOURNAL" or "VFREEBUSY")
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
            return requestedComponents.Count == 0 || content!.Components.Overlaps(requestedComponents);
        }

        foreach (var filter in report.Descendants(CardDav + "prop-filter"))
        {
            var propertyName = filter.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(propertyName))
                continue;
            var values = content!.Properties.TryGetValue(propertyName, out var found)
                ? found
                : [];
            var textMatch = filter.Element(CardDav + "text-match");
            if (textMatch is null)
            {
                if (values.Count == 0)
                    return false;
                continue;
            }
            var match = values.Any(value => TextMatches(
                value,
                textMatch.Value,
                textMatch.Attribute("match-type")?.Value));
            if (textMatch.Attribute("negate-condition")?.Value == "yes")
                match = !match;
            if (!match)
                return false;
        }
        return true;
    }

    private static bool TextMatches(string value, string pattern, string? matchType) =>
        matchType?.ToLowerInvariant() switch
        {
            "equals" => value.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            "starts-with" => value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            "ends-with" => value.EndsWith(pattern, StringComparison.OrdinalIgnoreCase),
            _ => value.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        };

    private static async Task<(DavCollection Collection, DavResource Resource)?> ResolveResourceAsync(
        AuthenticatedMailUser user,
        DavPath path,
        DavStore store,
        CancellationToken cancellationToken)
    {
        var collection = await store.GetCollectionAsync(
            user,
            path.CollectionKind!.Value,
            path.Slug!,
            cancellationToken);
        if (collection is null)
            return null;
        var resource = await store.GetResourceAsync(
            collection.Id,
            path.ResourceName!,
            cancellationToken);
        return resource is null ? null : (collection, resource);
    }

    private static async Task WriteMultiStatusAsync(
        HttpContext context,
        IEnumerable<XElement> responses,
        string? syncToken,
        CancellationToken cancellationToken)
    {
        var root = new XElement(
            Dav + "multistatus",
            new XAttribute(XNamespace.Xmlns + "D", Dav),
            new XAttribute(XNamespace.Xmlns + "C", CalDav),
            new XAttribute(XNamespace.Xmlns + "A", CardDav),
            new XAttribute(XNamespace.Xmlns + "CS", CalendarServer),
            new XAttribute(XNamespace.Xmlns + "I", AppleIcal),
            responses);
        if (syncToken is not null)
            root.Add(new XElement(Dav + "sync-token", syncToken));
        await WriteXmlAsync(context, StatusCodes.Status207MultiStatus, new XDocument(root), cancellationToken);
    }

    private static async Task WriteDavErrorAsync(
        HttpContext context,
        int status,
        XName errorName,
        CancellationToken cancellationToken)
    {
        var document = new XDocument(new XElement(
            Dav + "error",
            new XAttribute(XNamespace.Xmlns + "D", Dav),
            new XAttribute(XNamespace.Xmlns + "C", CalDav),
            new XAttribute(XNamespace.Xmlns + "A", CardDav),
            new XElement(errorName)));
        await WriteXmlAsync(context, status, document, cancellationToken);
    }

    private static async Task WriteXmlAsync(
        HttpContext context,
        int status,
        XDocument document,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(
            document.ToString(SaveOptions.DisableFormatting),
            cancellationToken);
    }

    private static async Task WriteTextErrorAsync(
        HttpContext context,
        int status,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message, cancellationToken);
    }

    private static async Task<(XDocument? Value, bool IsInvalid)> ReadXmlBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBodyAsync(request, maximumBytes, cancellationToken);
        if (bytes is null)
            return (null, true);
        if (bytes.Length == 0)
            return (null, false);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = maximumBytes,
                XmlResolver = null,
            });
            return (XDocument.Load(reader, LoadOptions.None), false);
        }
        catch (XmlException)
        {
            return (null, true);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
            return null;
        await using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(block, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                return null;
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool TryParsePath(PathString requestPath, out DavPath path)
    {
        path = default;
        var value = requestPath.Value ?? string.Empty;
        if (!value.StartsWith("/dav/", StringComparison.Ordinal))
            return false;
        var segments = value[5..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0)
        {
            path = new DavPath(DavPathKind.Root, null, null, null, null);
            return true;
        }
        if (segments.Length == 2
            && segments[0] == "principals"
            && Guid.TryParseExact(segments[1], "N", out var principalId))
        {
            path = new DavPath(DavPathKind.Principal, null, principalId, null, null);
            return true;
        }

        var kind = segments[0] switch
        {
            "calendars" => DavCollectionKind.Calendar,
            "addressbooks" => DavCollectionKind.AddressBook,
            _ => (DavCollectionKind?)null,
        };
        if (kind is null
            || segments.Length < 2
            || !Guid.TryParseExact(segments[1], "N", out var userId))
        {
            return false;
        }
        if (segments.Length == 2)
        {
            path = new DavPath(DavPathKind.Home, kind, userId, null, null);
            return true;
        }
        if (!IsValidSegment(segments[2], 128))
            return false;
        if (segments.Length == 3)
        {
            path = new DavPath(DavPathKind.Collection, kind, userId, segments[2], null);
            return true;
        }
        if (segments.Length == 4 && IsValidSegment(segments[3], 255))
        {
            path = new DavPath(DavPathKind.Resource, kind, userId, segments[2], segments[3]);
            return true;
        }
        return false;
    }

    private static bool IsValidSegment(string value, int maximumLength) =>
        value.Length is > 0
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.');

    private static bool HasExpectedExtension(DavCollectionKind kind, string resourceName) =>
        resourceName.EndsWith(
            kind == DavCollectionKind.Calendar ? ".ics" : ".vcf",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryGetResourceNameFromHref(
        DavCollection collection,
        string value,
        out string resourceName)
    {
        resourceName = string.Empty;
        var path = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.AbsolutePath
            : value;
        if (!path.StartsWith(CollectionHref(collection), StringComparison.Ordinal))
            return false;
        var remainder = path[CollectionHref(collection).Length..];
        if (remainder.Contains('/', StringComparison.Ordinal))
            return false;
        resourceName = Uri.UnescapeDataString(remainder);
        return IsValidSegment(resourceName, 255);
    }

    private static string SyncToken(DavCollection collection) =>
        $"urn:mk8.email:dav-sync:{collection.Id:N}:{collection.SyncToken}";

    private static bool TryParseSyncToken(
        DavCollection collection,
        string value,
        out long sequence)
    {
        sequence = 0;
        var prefix = $"urn:mk8.email:dav-sync:{collection.Id:N}:";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(
                value.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out sequence)
            && sequence >= 0;
    }

    private static string CollectionHref(DavCollection collection) =>
        $"{HomeHref(collection.Kind, collection.UserId)}{Uri.EscapeDataString(collection.Slug)}/";

    private static string ResourceHref(DavCollection collection, string resourceName) =>
        $"{CollectionHref(collection)}{Uri.EscapeDataString(resourceName)}";

    private static string HomeHref(DavCollectionKind kind, Guid userId) =>
        $"/dav/{(kind == DavCollectionKind.Calendar ? "calendars" : "addressbooks")}/{userId:N}/";

    private static string PrincipalHref(Guid userId) => $"/dav/principals/{userId:N}/";
    private const string RootHref = "/dav/";

    private static string HttpStatus(int status) =>
        $"HTTP/1.1 {status} {ReasonPhrase(status)}";

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        404 => "Not Found",
        _ => "Status",
    };

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? LimitNullable(string? value, int maximumLength) =>
        value is null ? null : Limit(value, maximumLength);

    private static void SetDavHeaders(HttpResponse response)
    {
        response.Headers["DAV"] = "1, 2, 3, calendar-access, addressbook, sync-collection";
        response.Headers.Allow = string.Join(", ", DavMethods);
        response.Headers["MS-Author-Via"] = "DAV";
        response.Headers.CacheControl = "no-store";
    }

    private enum DavPathKind { Root, Principal, Home, Collection, Resource }

    private readonly record struct DavPath(
        DavPathKind Kind,
        DavCollectionKind? CollectionKind,
        Guid? UserId,
        string? Slug,
        string? ResourceName);
}
