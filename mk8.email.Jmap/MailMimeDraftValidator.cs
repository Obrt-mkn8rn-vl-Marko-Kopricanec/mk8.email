using MimeKit;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal static class MailMimeDraftValidator
{
    public static bool IsValid(MailMessageDraft draft, JmapInvocationContext context)
    {
        if (!Enum.IsDefined(draft.FolderIssue) || !Enum.IsDefined(draft.KeywordIssue)
            || draft.Keywords is null || draft.Keywords.Any(value => value is null)
            || draft.BlobReferences is null || draft.BlobReferences.Any(value => value is null)
            || draft.FolderIssue == MailMessageMailboxIssue.None && draft.FolderReference is null
            || draft.ReceivedAt is { Kind: not DateTimeKind.Utc }
            || draft.Failure is { } failure && (!Enum.IsDefined(failure.Error)
                || failure.Error is MailMessageMutationError.None)
            || draft.BlobReferences.Distinct(StringComparer.Ordinal).Count() != draft.BlobReferences.Count)
            return false;
        if (draft.KeywordIssue == MailMessageKeywordIssue.None
            && (draft.Keywords.Count > 128 || draft.Keywords.Any(keyword => !JmapEmailCodec.IsValidKeyword(keyword)
                || !string.Equals(keyword, keyword.ToProtocolLowerInvariant(), StringComparison.Ordinal))
                || draft.Keywords.Distinct(StringComparer.Ordinal).Count() != draft.Keywords.Count))
            return false;
        if (draft.Failure is not null) return draft.Mime is null;
        return draft.CheckBlobsBeforeFailure && draft.Mime is { } mime
            && ValidParts(mime, draft.BlobReferences, context);
    }

    private static bool ValidParts(MailMimeDraft mime, IReadOnlyList<string> references,
        JmapInvocationContext context)
    {
        if (mime.Headers is null || !ValidHeaders(mime.Headers) || mime.Parts is null
            || mime.Parts.Count == 0 || mime.RootPart != mime.Parts.Count - 1)
            return false;
        var parents = new int[mime.Parts.Count];
        for (var index = 0; index < mime.Parts.Count; index++)
        {
            var part = mime.Parts[index];
            if (part is null || part.Headers is null || !ValidHeaders(part.Headers)
                || part.Children is null || !ContentType.TryParse(part.MediaType, out var type))
                return false;
            var multipart = string.Equals(type.MediaType, "multipart", StringComparison.OrdinalIgnoreCase);
            if (multipart ? part.Text is not null || part.BlobReference is not null
                : part.Children.Count != 0 || (part.Text is null) == (part.BlobReference is null))
                return false;
            if (part.Text is not null && !string.Equals(type.MediaType, "text", StringComparison.OrdinalIgnoreCase)
                || part.BlobReference is { } reference && (!references.Contains(reference, StringComparer.Ordinal)
                    || !JmapMethodHelpers.AreValidIdReferences([reference], context)))
                return false;
            foreach (var child in part.Children)
                if (child < 0 || child >= index || ++parents[child] != 1) return false;
        }
        return parents.Take(parents.Length - 1).All(count => count == 1) && parents[^1] == 0;
    }

    private static bool ValidHeaders(IReadOnlyList<MailMimeHeaderSnapshot> headers) =>
        headers.All(header => header is not null && !header.RawField.IsEmpty);
}
