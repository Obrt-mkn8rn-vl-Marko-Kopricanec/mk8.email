using System.Text;
using mk8.email.Application.Protocol;

namespace mk8.email.Jmap;

internal static class JmapCollation
{
    public static readonly IReadOnlyList<string> SupportedIdentifiers =
    [
        "i;ascii-numeric",
        "i;ascii-casemap",
    ];

    public static bool IsSupported(string value) =>
        SupportedIdentifiers.Contains(value, StringComparer.Ordinal);

    public static int Compare(string left, string right, string? collation) =>
        collation switch
        {
            "i;ascii-numeric" => CompareAsciiNumeric(left, right),
            "i;ascii-casemap" => CompareAsciiCasemap(left, right),
            _ => Rfc5256.CompareUnicodeCasemap(left, right),
        };

    private static int CompareAsciiNumeric(string left, string right)
    {
        var leftDigits = LeadingDigits(left).TrimStart('0');
        var rightDigits = LeadingDigits(right).TrimStart('0');
        var leftIsInfinity = left.Length == 0 || !char.IsAsciiDigit(left[0]);
        var rightIsInfinity = right.Length == 0 || !char.IsAsciiDigit(right[0]);
        if (leftIsInfinity || rightIsInfinity)
            return leftIsInfinity.CompareTo(rightIsInfinity);

        var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);
        return lengthComparison != 0
            ? lengthComparison
            : leftDigits.SequenceCompareTo(rightDigits);

        static ReadOnlySpan<char> LeadingDigits(string value)
        {
            var length = 0;
            while (length < value.Length && char.IsAsciiDigit(value[length]))
                length++;
            return value.AsSpan(0, length);
        }
    }

    private static int CompareAsciiCasemap(string left, string right) =>
        CompareUtf8(
            Encoding.UTF8.GetBytes(FoldAscii(left)),
            Encoding.UTF8.GetBytes(FoldAscii(right)));

    private static string FoldAscii(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'a' and <= 'z')
                characters[index] = (char)(characters[index] - ('a' - 'A'));
        }
        return new string(characters);
    }

    private static int CompareUtf8(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.SequenceCompareTo(right);
}
