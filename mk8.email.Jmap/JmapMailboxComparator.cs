using System.Text.Json.Nodes;
using mk8.email.Configuration;

namespace mk8.email.Jmap;

internal sealed record JmapMailboxComparator(
    string Property,
    bool IsAscending,
    string? Collation);
