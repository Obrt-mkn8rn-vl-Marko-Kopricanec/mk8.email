using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Ews;

internal static class GatewayEwsBodyVisibility
{
    internal static MailMessageSnapshot Project(MailMessageSnapshot snapshot)
    {
        ValidateGraph(snapshot);
        if (snapshot.RootPart is not { } root) return snapshot;
        var visible = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(root);
        while (pending.TryPop(out var index))
        {
            var part = snapshot.Parts[index];
            // RFC 2183 §§2.8–2.9: disposition applies to the whole subtree;
            // unknown dispositions, like attachments, require separate presentation.
            if (part.Disposition is not null && !string.Equals(part.Disposition, "inline", StringComparison.OrdinalIgnoreCase)) continue;
            if (!visible.Add(index)) continue;
            foreach (var child in part.Children) pending.Push(child);
        }
        var remap = new Dictionary<int, int>();
        var parts = new List<MailMimePartSnapshot>(visible.Count);
        for (var index = 0; index < snapshot.Parts.Count; index++)
        {
            if (!visible.Contains(index)) continue;
            var part = snapshot.Parts[index];
            var children = part.Children.Where(visible.Contains).Select(child => remap[child]).ToArray();
            remap.Add(index, parts.Count);
            parts.Add(part with { Children = children });
        }
        return snapshot with { RootPart = remap.TryGetValue(root, out var projectedRoot) ? projectedRoot : null, Parts = parts };
    }

    private static void ValidateGraph(MailMessageSnapshot snapshot)
    {
        if (snapshot.Parts is null || snapshot.RootPart is { } root && (root < 0 || root >= snapshot.Parts.Count)
            || snapshot.RootPart is null && snapshot.Parts.Count != 0)
            throw new InvalidOperationException("The Application returned an invalid MIME graph.");
        for (var index = 0; index < snapshot.Parts.Count; index++)
        {
            var part = snapshot.Parts[index];
            if (part is null || part.Headers is null || part.Children is null || part.MediaType is null
                || part.DecodedSize is < 0 or > int.MaxValue || part.Children.Any(child => child < 0 || child >= index)
                || part.Path is null && part.Children.Count == 0 && !part.MediaType.StartsWith("multipart/", StringComparison.Ordinal))
                throw new InvalidOperationException("The Application returned an invalid MIME part.");
        }
    }
}
