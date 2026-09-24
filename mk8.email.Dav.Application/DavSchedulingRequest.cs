using mk8.email.Application.Interfaces;

namespace mk8.email.Dav;

internal sealed record DavSchedulingRequest(
    string Method,
    string Uid,
    string Organizer,
    HashSet<string> Attendees,
    string? Summary,
    bool IsFreeBusy,
    DateTimeOffset? RangeStart,
    DateTimeOffset? RangeEnd,
    DavContentInfo Content);
