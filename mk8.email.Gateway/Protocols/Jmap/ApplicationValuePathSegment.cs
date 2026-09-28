namespace mk8.email.Gateway.Protocols.Jmap;

// Gateway interprets response shape; Worker never sees these selectors.
internal sealed record ApplicationValuePathSegment(
    string Property,
    int? ArrayIndex = null,
    bool AllArrayItems = false);
