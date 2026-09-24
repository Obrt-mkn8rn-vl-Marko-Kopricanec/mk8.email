using mk8.email.Application.Interfaces;

namespace mk8.email.Dav;

internal sealed record DavCalendarResourceSet(
    IReadOnlyList<DavResource> Resources,
    bool IsComplete);
