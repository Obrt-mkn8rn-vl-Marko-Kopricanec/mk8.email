using System.Globalization;
using System.Text;

namespace mk8.email.Application.Protocol;

public static class Rfc5256
{
    private static readonly TextInfo InvariantTextInfo = CultureInfo.InvariantCulture.TextInfo;

    public static string BaseSubject(string? value) => AnalyzeSubject(value).BaseSubject;

    public static (string BaseSubject, bool IsReplyOrForward) AnalyzeSubject(string? value)
    {
        var subject = NormalizeSubjectWhitespace(value);
        var isReplyOrForward = false;
        while (true)
        {
            subject = RemoveSubjectTrailers(subject, ref isReplyOrForward);

            while (true)
            {
                var leaderEnd = SubjectLeaderEnd(subject, out var removedReplyOrForward);
                if (leaderEnd > 0)
                {
                    isReplyOrForward |= removedReplyOrForward;
                    subject = subject[leaderEnd..];
                    continue;
                }

                if (TryReadSubjectBlob(subject, 0, out var blobEnd)
                    && blobEnd < subject.Length)
                {
                    subject = subject[blobEnd..];
                    continue;
                }

                break;
            }

            if (subject.StartsWith("[fwd:", StringComparison.OrdinalIgnoreCase)
                && subject.EndsWith(']'))
            {
                isReplyOrForward = true;
                subject = subject[5..^1];
                continue;
            }

            return (subject, isReplyOrForward);
        }
    }

    public static int CompareUnicodeCasemap(string left, string right) =>
        UnicodeCasemapSortKey(left).AsSpan().SequenceCompareTo(UnicodeCasemapSortKey(right));

    public static byte[] UnicodeCasemapSortKey(string value)
    {
        var prepared = new StringBuilder(value.Length);
        foreach (var codePoint in value.EnumerateRunes())
        {
            var titlecased = InvariantTextInfo.ToTitleCase(codePoint.ToString());
            prepared.Append(titlecased.Normalize(NormalizationForm.FormKD));
        }
        return Encoding.UTF8.GetBytes(prepared.ToString());
    }

    private static string NormalizeSubjectWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var result = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value)
        {
            var isSpace = character is ' ' or '\t' or '\r' or '\n';
            if (isSpace)
            {
                if (!previousWasSpace)
                    result.Append(' ');
            }
            else
            {
                result.Append(character);
            }
            previousWasSpace = isSpace;
        }
        return result.ToString();
    }

    private static string RemoveSubjectTrailers(string subject, ref bool isReplyOrForward)
    {
        while (true)
        {
            subject = subject.TrimEnd(' ');
            if (!subject.EndsWith("(fwd)", StringComparison.OrdinalIgnoreCase))
                return subject;
            isReplyOrForward = true;
            subject = subject[..^5];
        }
    }

    private static int SubjectLeaderEnd(string subject, out bool isReplyOrForward)
    {
        isReplyOrForward = false;
        var index = 0;
        while (index < subject.Length && subject[index] == ' ')
            index++;
        if (index > 0)
            return index;

        while (TryReadSubjectBlob(subject, index, out var blobEnd))
            index = blobEnd;
        if (!TryReadReplyOrForward(subject, index, out var leaderEnd))
            return 0;
        isReplyOrForward = true;
        return leaderEnd;
    }

    private static bool TryReadReplyOrForward(string subject, int start, out int end)
    {
        end = start;
        var tokenLength = subject.AsSpan(start).StartsWith("fwd", StringComparison.OrdinalIgnoreCase)
            ? 3
            : subject.AsSpan(start).StartsWith("re", StringComparison.OrdinalIgnoreCase)
                || subject.AsSpan(start).StartsWith("fw", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 0;
        if (tokenLength == 0)
            return false;

        var index = start + tokenLength;
        while (index < subject.Length && subject[index] == ' ')
            index++;
        if (TryReadSubjectBlob(subject, index, out var blobEnd))
            index = blobEnd;
        if (index >= subject.Length || subject[index] != ':')
            return false;
        end = index + 1;
        return true;
    }

    private static bool TryReadSubjectBlob(string subject, int start, out int end)
    {
        end = start;
        if (start >= subject.Length || subject[start] != '[')
            return false;

        for (var index = start + 1; index < subject.Length; index++)
        {
            if (subject[index] is '\0' or '[')
                return false;
            if (subject[index] != ']')
                continue;

            index++;
            while (index < subject.Length && subject[index] == ' ')
                index++;
            end = index;
            return true;
        }
        return false;
    }
}
