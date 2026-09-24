using mk8.email.Application.Interfaces;

namespace mk8.email.Dav;

internal sealed record DavCalendarRecipient(
    string Address,
    bool IsLocal,
    AuthenticatedMailUser? User);
