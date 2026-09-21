using System.Globalization;
using System.Text;
using MimeKit;

namespace mk8.email.Application.Protocol;

internal sealed record SieveDiagnostic(int Line, int Column, string Message);

internal sealed record SieveCompilationResult(
    SieveProgram? Program,
    IReadOnlyList<SieveDiagnostic> Diagnostics)
{
    public bool Succeeded => Program is not null && Diagnostics.Count == 0;
}

internal sealed record SieveMessageContext(
    string EnvelopeSender,
    string EnvelopeRecipient,
    string RawMessage,
    string DefaultFolder,
    IReadOnlySet<string> Mailboxes);

internal sealed record SieveDelivery(string Folder, IReadOnlyList<string> Flags, bool Create);

internal sealed record SieveEvaluationResult(
    IReadOnlyList<SieveDelivery> Deliveries,
    IReadOnlyList<string> Redirects,
    string? RejectReason,
    bool Discarded);

internal static class SieveScript
{
    public const int MaximumScriptBytes = 1024 * 1024;
    public const int MaximumTokens = 50_000;
    public const int MaximumNestingDepth = 64;
    public const int MaximumRedirects = 100;
    private const int MaximumActions = 100;

    public static readonly IReadOnlySet<string> SupportedCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "body",
            "comparator-i;ascii-casemap",
            "copy",
            "envelope",
            "fileinto",
            "imap4flags",
            "mailbox",
            "reject",
        };

    public static SieveCompilationResult Compile(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > MaximumScriptBytes)
        {
            return new SieveCompilationResult(
                null,
                [new SieveDiagnostic(1, 1, "The script exceeds the one-megabyte limit.")]);
        }

        try
        {
            var tokens = new Lexer(source).ReadAll();
            var program = new Parser(tokens).ParseProgram();
            return new SieveCompilationResult(program, []);
        }
        catch (SieveParseException exception)
        {
            return new SieveCompilationResult(
                null,
                [new SieveDiagnostic(exception.Line, exception.Column, exception.Message)]);
        }
    }

    public static SieveEvaluationResult Evaluate(
        SieveProgram program,
        SieveMessageContext context)
    {
        var message = ParsedSieveMessage.Parse(context);
        var state = new EvaluationState(context.DefaultFolder);
        foreach (var statement in program.Statements)
        {
            Execute(statement, message, state);
            if (state.Stopped)
                break;
        }

        if (state.ImplicitKeep && state.RejectReason is null)
            state.AddDelivery(context.DefaultFolder, state.Flags, create: false);

        return new SieveEvaluationResult(
            state.Deliveries,
            state.Redirects,
            state.RejectReason,
            !state.ImplicitKeep
                && state.Deliveries.Count == 0
                && state.Redirects.Count == 0
                && state.RejectReason is null);
    }

    private static void Execute(
        SieveStatement statement,
        ParsedSieveMessage message,
        EvaluationState state)
    {
        if (state.Stopped)
            return;
        if (++state.ActionCount > MaximumActions)
            throw new InvalidOperationException("The Sieve action limit was exceeded.");

        switch (statement)
        {
            case SieveIf conditional:
                foreach (var branch in conditional.Branches)
                {
                    if (!EvaluateTest(branch.Test, message, state.Budget))
                        continue;
                    ExecuteBlock(branch.Statements, message, state);
                    return;
                }
                ExecuteBlock(conditional.ElseStatements, message, state);
                break;
            case SieveKeep keep:
                state.AddDelivery(
                    state.DefaultFolder,
                    keep.Flags is null ? state.Flags : keep.Flags,
                    create: false);
                state.ImplicitKeep = false;
                break;
            case SieveFileInto fileInto:
                state.AddDelivery(
                    fileInto.Folder,
                    fileInto.Flags is null ? state.Flags : fileInto.Flags,
                    fileInto.Create);
                if (!fileInto.Copy)
                    state.ImplicitKeep = false;
                break;
            case SieveRedirect redirect:
                state.AddRedirect(redirect.Address);
                if (!redirect.Copy)
                    state.ImplicitKeep = false;
                break;
            case SieveDiscard:
                state.ImplicitKeep = false;
                break;
            case SieveStop:
                state.Stopped = true;
                break;
            case SieveReject reject:
                state.Reject(reject.Reason);
                break;
            case SieveSetFlags setFlags:
                ApplyFlags(state.Flags, setFlags.Flags, FlagOperation.Set);
                break;
            case SieveAddFlags addFlags:
                ApplyFlags(state.Flags, addFlags.Flags, FlagOperation.Add);
                break;
            case SieveRemoveFlags removeFlags:
                ApplyFlags(state.Flags, removeFlags.Flags, FlagOperation.Remove);
                break;
            default:
                throw new InvalidOperationException("The compiled Sieve statement is invalid.");
        }
    }

    private static void ExecuteBlock(
        IReadOnlyList<SieveStatement> statements,
        ParsedSieveMessage message,
        EvaluationState state)
    {
        foreach (var statement in statements)
        {
            Execute(statement, message, state);
            if (state.Stopped)
                return;
        }
    }

    private static bool EvaluateTest(
        SieveTest test,
        ParsedSieveMessage message,
        EvaluationBudget budget)
    {
        budget.Charge(1);
        return test switch
        {
            SieveTrue => true,
            SieveFalse => false,
            SieveNot not => !EvaluateTest(not.Test, message, budget),
            SieveAllOf all => all.Tests.All(item => EvaluateTest(item, message, budget)),
            SieveAnyOf any => any.Tests.Any(item => EvaluateTest(item, message, budget)),
            SieveExists exists => exists.HeaderNames.All(message.HasHeader),
            SieveSize size => size.Over
                ? message.SizeBytes > size.Bytes
                : message.SizeBytes < size.Bytes,
            SieveHeader header => MatchAny(
                header.HeaderNames.SelectMany(message.GetHeaderValues),
                header.Keys,
                header.Options,
                budget),
            SieveAddress address => MatchAny(
                address.HeaderNames
                    .SelectMany(message.GetHeaderValues)
                    .SelectMany(ParseAddresses)
                    .Select(value => SelectAddressPart(value, address.AddressPart)),
                address.Keys,
                address.Options,
                budget),
            SieveEnvelope envelope => MatchAny(
                envelope.Fields.Select(field => message.GetEnvelope(field))
                    .Where(value => value is not null)
                    .Select(value => SelectAddressPart(value!, envelope.AddressPart)),
                envelope.Keys,
                envelope.Options,
                budget),
            SieveBody body => MatchAny(
                message.GetBodyValues(body.Transform, body.ContentTypes),
                body.Keys,
                body.Options,
                budget),
            SieveMailboxExists mailbox => mailbox.Folders.All(message.MailboxExists),
            _ => throw new InvalidOperationException("The compiled Sieve test is invalid."),
        };
    }

    private static bool MatchAny(
        IEnumerable<string> values,
        IReadOnlyList<string> keys,
        MatchOptions options,
        EvaluationBudget budget)
    {
        var octetComparison = options.Comparator.Equals(
            "i;octet",
            StringComparison.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            foreach (var key in keys)
            {
                var comparisonCost = options.MatchType == SieveMatchType.Is
                    ? Math.Max(value.Length, key.Length)
                    : Math.Min(
                        100_000_001L,
                        (long)Math.Max(1, value.Length) * Math.Max(1, key.Length));
                budget.Charge(comparisonCost);
                var matched = options.MatchType switch
                {
                    SieveMatchType.Is => octetComparison
                        ? string.Equals(value, key, StringComparison.Ordinal)
                        : AsciiCasemapEquals(value, key),
                    SieveMatchType.Contains => octetComparison
                        ? value.Contains(key, StringComparison.Ordinal)
                        : AsciiCasemapContains(value, key),
                    SieveMatchType.Matches => GlobMatches(
                        value,
                        key,
                        asciiCasemap: !octetComparison,
                        budget),
                    _ => false,
                };
                if (matched)
                    return true;
            }
        }
        return false;
    }

    private static bool GlobMatches(
        string value,
        string pattern,
        bool asciiCasemap,
        EvaluationBudget budget)
    {
        var valueRunes = value.EnumerateRunes().ToArray();
        var patternRunes = pattern.EnumerateRunes().ToArray();
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var starValueIndex = -1;
        while (valueIndex < valueRunes.Length)
        {
            budget.Charge(1);
            if (patternIndex < patternRunes.Length
                && (patternRunes[patternIndex].Value == '?'
                    || RunesEqual(valueRunes[valueIndex], patternRunes[patternIndex], asciiCasemap)))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }
            if (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
                continue;
            }
            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
                continue;
            }
            return false;
        }

        while (patternIndex < patternRunes.Length && patternRunes[patternIndex].Value == '*')
            patternIndex++;
        return patternIndex == patternRunes.Length;
    }

    private static bool AsciiCasemapEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (FoldAscii(left[index]) != FoldAscii(right[index]))
                return false;
        }
        return true;
    }

    private static bool AsciiCasemapContains(string value, string key)
    {
        if (key.Length == 0)
            return true;
        if (key.Length > value.Length)
            return false;
        for (var start = 0; start <= value.Length - key.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < key.Length; offset++)
            {
                if (FoldAscii(value[start + offset]) == FoldAscii(key[offset]))
                    continue;
                matched = false;
                break;
            }
            if (matched)
                return true;
        }
        return false;
    }

    private static bool RunesEqual(Rune left, Rune right, bool asciiCasemap)
    {
        if (!asciiCasemap)
            return left == right;
        return left.IsAscii && right.IsAscii
            ? FoldAscii((char)left.Value) == FoldAscii((char)right.Value)
            : left == right;
    }

    private static char FoldAscii(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private static IEnumerable<string> ParseAddresses(string value)
    {
        if (InternetAddressList.TryParse(value, out var addresses))
        {
            foreach (var mailbox in addresses.Mailboxes)
                yield return mailbox.Address;
            yield break;
        }

        foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = candidate.Trim().Trim('<', '>');
            if (normalized.Contains('@', StringComparison.Ordinal))
                yield return normalized;
        }
    }

    private static string SelectAddressPart(string address, SieveAddressPart part)
    {
        var separator = address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Length - 1)
            return part == SieveAddressPart.All ? address : string.Empty;
        return part switch
        {
            SieveAddressPart.LocalPart => address[..separator],
            SieveAddressPart.Domain => address[(separator + 1)..],
            _ => address,
        };
    }

    private static void ApplyFlags(
        HashSet<string> target,
        IReadOnlyList<string> flags,
        FlagOperation operation)
    {
        if (operation == FlagOperation.Set)
            target.Clear();
        foreach (var flag in flags)
        {
            if (operation == FlagOperation.Remove)
                target.Remove(flag);
            else
                target.Add(flag);
        }
    }

    private enum FlagOperation { Set, Add, Remove }

    private sealed class EvaluationState(string defaultFolder)
    {
        public string DefaultFolder { get; } = defaultFolder;
        public List<SieveDelivery> Deliveries { get; } = [];
        public List<string> Redirects { get; } = [];
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public EvaluationBudget Budget { get; } = new();
        public bool ImplicitKeep { get; set; } = true;
        public bool Stopped { get; set; }
        public int ActionCount { get; set; }
        public string? RejectReason { get; private set; }

        public void AddDelivery(string folder, IEnumerable<string> flags, bool create)
        {
            var normalizedFlags = flags
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(flag => flag, StringComparer.Ordinal)
                .ToArray();
            var existingIndex = Deliveries.FindIndex(item =>
                string.Equals(item.Folder, folder, StringComparison.Ordinal));
            if (existingIndex < 0)
            {
                Deliveries.Add(new SieveDelivery(folder, normalizedFlags, create));
                return;
            }

            var existing = Deliveries[existingIndex];
            Deliveries[existingIndex] = existing with
            {
                Flags = existing.Flags
                    .Concat(normalizedFlags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ThenBy(flag => flag, StringComparer.Ordinal)
                    .ToArray(),
                Create = existing.Create || create,
            };
        }

        public void AddRedirect(string address)
        {
            if (!Redirects.Contains(address, StringComparer.OrdinalIgnoreCase))
                Redirects.Add(address);
        }

        public void Reject(string reason)
        {
            Deliveries.Clear();
            Redirects.Clear();
            RejectReason = reason;
            ImplicitKeep = false;
            Stopped = true;
        }
    }

    private sealed class EvaluationBudget
    {
        private const long MaximumCost = 100_000_000;
        private long _remaining = MaximumCost;

        public void Charge(long cost)
        {
            if (cost < 0 || cost > _remaining)
                throw new InvalidOperationException("The Sieve evaluation limit was exceeded.");
            _remaining -= cost;
        }
    }

    private sealed class ParsedSieveMessage
    {
        private const int MaximumBodyParts = 2048;
        private const int MaximumDecodedBodyCharacters = 64 * 1024 * 1024;
        private readonly Dictionary<string, List<string>> _headers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<BodyValue> _bodyValues = [];

        private ParsedSieveMessage(SieveMessageContext context)
        {
            EnvelopeSender = context.EnvelopeSender;
            EnvelopeRecipient = context.EnvelopeRecipient;
            Mailboxes = context.Mailboxes;
            RawBody = MailMessageParser.Parse(context.RawMessage).Body;
            SizeBytes = MailWireEncoding.Instance.GetByteCount(context.RawMessage);

            try
            {
                using var stream = new MemoryStream(MailWireEncoding.Instance.GetBytes(context.RawMessage));
                using var message = MimeMessage.Load(stream);
                foreach (var header in message.Headers)
                {
                    if (!_headers.TryGetValue(header.Field, out var values))
                    {
                        values = [];
                        _headers.Add(header.Field, values);
                    }
                    values.Add(header.Value);
                }
                var decodedCharacters = 0;
                var partCount = 0;
                foreach (var part in message.BodyParts)
                {
                    if (++partCount > MaximumBodyParts)
                        throw new InvalidOperationException("The MIME body part limit was exceeded.");
                    string content;
                    if (part is TextPart textPart)
                    {
                        content = textPart.Text;
                    }
                    else if (part is MimePart mimePart && mimePart.Content is not null)
                    {
                        using var decoded = new MemoryStream();
                        mimePart.Content.DecodeTo(decoded);
                        content = MailWireEncoding.Instance.GetString(decoded.ToArray());
                    }
                    else
                    {
                        continue;
                    }

                    decodedCharacters += content.Length;
                    if (decodedCharacters > MaximumDecodedBodyCharacters)
                        throw new InvalidOperationException("The decoded MIME body limit was exceeded.");
                    _bodyValues.Add(new BodyValue(part.ContentType.MimeType, content));
                }
            }
            catch (FormatException)
            {
                var parsed = MailMessageParser.Parse(context.RawMessage);
                foreach (var line in UnfoldHeaders(parsed.Headers))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0)
                        continue;
                    var name = line[..separator];
                    if (!_headers.TryGetValue(name, out var values))
                    {
                        values = [];
                        _headers.Add(name, values);
                    }
                    values.Add(line[(separator + 1)..].Trim());
                }
            }

            if (_bodyValues.Count == 0)
                _bodyValues.Add(new BodyValue("text/plain", RawBody));
        }

        public string EnvelopeSender { get; }
        public string EnvelopeRecipient { get; }
        public string RawBody { get; }
        public long SizeBytes { get; }
        public IReadOnlySet<string> Mailboxes { get; }

        public static ParsedSieveMessage Parse(SieveMessageContext context) => new(context);

        public bool HasHeader(string name) => _headers.ContainsKey(name);

        public IEnumerable<string> GetHeaderValues(string name) =>
            _headers.TryGetValue(name, out var values) ? values : [];

        public string? GetEnvelope(string field) => field.ToLowerInvariant() switch
        {
            "from" => EnvelopeSender,
            "to" => EnvelopeRecipient,
            _ => null,
        };

        public IEnumerable<string> GetBodyValues(
            SieveBodyTransform transform,
            IReadOnlyList<string> contentTypes)
        {
            if (transform == SieveBodyTransform.Raw)
                return [RawBody];
            if (transform == SieveBodyTransform.Text)
                return _bodyValues
                    .Where(value => value.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    .Select(value => value.Content);
            return _bodyValues
                .Where(value => contentTypes.Any(requested =>
                    value.ContentType.Equals(requested, StringComparison.OrdinalIgnoreCase)
                    || requested.EndsWith("/*", StringComparison.Ordinal)
                        && value.ContentType.StartsWith(requested[..^1], StringComparison.OrdinalIgnoreCase)))
                .Select(value => value.Content);
        }

        public bool MailboxExists(string folder) =>
            Mailboxes.Contains(MailboxName.Normalize(folder));

        private static IEnumerable<string> UnfoldHeaders(string headers)
        {
            var current = new StringBuilder();
            foreach (var line in headers.Split('\n'))
            {
                var value = line.TrimEnd('\r');
                if (value.Length > 0 && value[0] is ' ' or '\t' && current.Length > 0)
                {
                    current.Append(' ').Append(value.Trim());
                    continue;
                }
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear().Append(value);
            }
            if (current.Length > 0)
                yield return current.ToString();
        }

        private sealed record BodyValue(string ContentType, string Content);
    }

    private sealed class Lexer(string source)
    {
        private readonly List<Token> _tokens = [];
        private int _position;
        private int _line = 1;
        private int _column = 1;

        public IReadOnlyList<Token> ReadAll()
        {
            while (_position < source.Length)
            {
                SkipWhitespaceAndComments();
                if (_position >= source.Length)
                    break;
                if (_tokens.Count >= MaximumTokens)
                    Throw("The script contains too many tokens.");

                var line = _line;
                var column = _column;
                var value = Peek();
                switch (value)
                {
                    case '[': Add(TokenKind.LeftBracket, "[", line, column); Advance(); break;
                    case ']': Add(TokenKind.RightBracket, "]", line, column); Advance(); break;
                    case '(': Add(TokenKind.LeftParenthesis, "(", line, column); Advance(); break;
                    case ')': Add(TokenKind.RightParenthesis, ")", line, column); Advance(); break;
                    case '{': Add(TokenKind.LeftBrace, "{", line, column); Advance(); break;
                    case '}': Add(TokenKind.RightBrace, "}", line, column); Advance(); break;
                    case ';': Add(TokenKind.Semicolon, ";", line, column); Advance(); break;
                    case ',': Add(TokenKind.Comma, ",", line, column); Advance(); break;
                    case '"': Add(TokenKind.String, ReadQuotedString(), line, column); break;
                    case ':': Add(TokenKind.Tag, ReadTag(), line, column); break;
                    default:
                        if (char.IsAsciiDigit(value))
                            Add(TokenKind.Number, ReadNumber(), line, column);
                        else if (IsIdentifierStart(value))
                        {
                            var identifier = ReadIdentifier();
                            if (identifier.Equals("text", StringComparison.OrdinalIgnoreCase)
                                && PeekOrDefault() == ':')
                            {
                                Advance();
                                Add(TokenKind.String, ReadMultiline(), line, column);
                            }
                            else
                            {
                                Add(TokenKind.Identifier, identifier, line, column);
                            }
                        }
                        else
                            Throw($"Unexpected character '{value}'.");
                        break;
                }
            }

            _tokens.Add(new Token(TokenKind.End, string.Empty, _line, _column));
            return _tokens;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < source.Length)
            {
                if (char.IsWhiteSpace(Peek()))
                {
                    Advance();
                    continue;
                }
                if (Peek() == '#')
                {
                    while (_position < source.Length && Peek() is not '\r' and not '\n')
                        Advance();
                    continue;
                }
                if (Peek() == '/' && PeekOrDefault(1) == '*')
                {
                    Advance();
                    Advance();
                    while (_position < source.Length
                        && !(Peek() == '*' && PeekOrDefault(1) == '/'))
                        Advance();
                    if (_position >= source.Length)
                        Throw("Unterminated block comment.");
                    Advance();
                    Advance();
                    continue;
                }
                break;
            }
        }

        private string ReadQuotedString()
        {
            Advance();
            var value = new StringBuilder();
            while (_position < source.Length)
            {
                var character = Peek();
                if (character == '"')
                {
                    Advance();
                    return value.ToString();
                }
                if (character is '\r' or '\n' or '\0')
                    Throw("Quoted strings cannot contain line breaks or NUL characters.");
                if (character == '\\')
                {
                    Advance();
                    if (_position >= source.Length)
                        Throw("Unterminated quoted string.");
                    character = Peek();
                }
                value.Append(character);
                Advance();
            }
            Throw("Unterminated quoted string.");
            return string.Empty;
        }

        private string ReadMultiline()
        {
            if (PeekOrDefault() == '\r')
                Advance();
            if (PeekOrDefault() != '\n')
                Throw("The text: marker must be followed by a line break.");
            Advance();
            var value = new StringBuilder();
            while (_position < source.Length)
            {
                var line = new StringBuilder();
                while (_position < source.Length && Peek() is not '\r' and not '\n')
                {
                    line.Append(Peek());
                    Advance();
                }
                if (PeekOrDefault() == '\r')
                    Advance();
                if (PeekOrDefault() == '\n')
                    Advance();
                var text = line.ToString();
                if (text == ".")
                    return value.ToString();
                if (text.StartsWith("..", StringComparison.Ordinal))
                    text = text[1..];
                value.Append(text).Append("\r\n");
            }
            Throw("Unterminated multiline string.");
            return string.Empty;
        }

        private string ReadTag()
        {
            Advance();
            if (!IsIdentifierStart(PeekOrDefault()))
                Throw("A tag name is required after ':'.");
            return ReadIdentifier();
        }

        private string ReadNumber()
        {
            var start = _position;
            while (char.IsAsciiDigit(PeekOrDefault()))
                Advance();
            if (PeekOrDefault() is 'K' or 'k' or 'M' or 'm' or 'G' or 'g')
                Advance();
            return source[start.._position];
        }

        private string ReadIdentifier()
        {
            var start = _position;
            while (IsIdentifierPart(PeekOrDefault()))
                Advance();
            return source[start.._position];
        }

        private static bool IsIdentifierStart(char value) =>
            char.IsAsciiLetter(value) || value == '_';

        private static bool IsIdentifierPart(char value) =>
            char.IsAsciiLetterOrDigit(value) || value is '_' or '-';

        private char Peek() => source[_position];
        private char PeekOrDefault(int offset = 0) =>
            _position + offset < source.Length ? source[_position + offset] : '\0';

        private void Advance()
        {
            var value = source[_position++];
            if (value == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
        }

        private void Add(TokenKind kind, string value, int line, int column) =>
            _tokens.Add(new Token(kind, value, line, column));

        private void Throw(string message) => throw new SieveParseException(_line, _column, message);
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private readonly HashSet<string> _required = new(StringComparer.OrdinalIgnoreCase);
        private int _position;

        public SieveProgram ParseProgram()
        {
            var statements = new List<SieveStatement>();
            while (!At(TokenKind.End))
            {
                if (AtIdentifier("require"))
                {
                    if (statements.Count > 0)
                        Throw(Current, "require must precede every other command.");
                    ParseRequire();
                    continue;
                }
                statements.Add(ParseStatement(0));
            }
            return new SieveProgram(statements);
        }

        private void ParseRequire()
        {
            var token = Consume();
            var capabilities = ParseStringList();
            Expect(TokenKind.Semicolon, "Expected ';' after require.");
            foreach (var capability in capabilities)
            {
                if (!SupportedCapabilities.Contains(capability))
                    Throw(token, $"The Sieve capability '{capability}' is not supported.");
                _required.Add(capability);
            }
        }

        private SieveStatement ParseStatement(int depth)
        {
            EnsureDepth(depth);
            var command = Expect(TokenKind.Identifier, "Expected a Sieve command.");
            switch (command.Value.ToLowerInvariant())
            {
                case "if": return ParseIf(depth + 1);
                case "keep":
                {
                    var flags = ParseOptionalFlags(command);
                    ExpectSemicolon(command);
                    return new SieveKeep(flags);
                }
                case "fileinto":
                {
                    Require("fileinto", command);
                    var copy = false;
                    var create = false;
                    IReadOnlyList<string>? flags = null;
                    var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (At(TokenKind.Tag))
                    {
                        var tag = Consume();
                        if (!seenTags.Add(tag.Value))
                            Throw(tag, $"Duplicate fileinto tag ':{tag.Value}'.");
                        switch (tag.Value.ToLowerInvariant())
                        {
                            case "copy": Require("copy", tag); copy = true; break;
                            case "create": Require("mailbox", tag); create = true; break;
                            case "flags":
                                Require("imap4flags", tag);
                                flags = ParseStringList();
                                ValidateFlags(command, flags);
                                break;
                            default: Throw(tag, $"Unknown fileinto tag ':{tag.Value}'."); break;
                        }
                    }
                    var folder = Expect(TokenKind.String, "Expected a mailbox name.").Value;
                    if (!MailboxName.IsValid(folder))
                        Throw(command, "The mailbox name is invalid.");
                    ExpectSemicolon(command);
                    return new SieveFileInto(folder, copy, create, flags);
                }
                case "redirect":
                {
                    var copy = false;
                    if (AtTag("copy"))
                    {
                        Require("copy", Consume());
                        copy = true;
                    }
                    var address = Expect(TokenKind.String, "Expected a redirect address.").Value;
                    if (!SmtpAddress.TryNormalize(address, allowEmpty: false, out address))
                        Throw(command, "The redirect address is invalid.");
                    ExpectSemicolon(command);
                    return new SieveRedirect(address, copy);
                }
                case "discard": ExpectSemicolon(command); return new SieveDiscard();
                case "stop": ExpectSemicolon(command); return new SieveStop();
                case "reject":
                {
                    Require("reject", command);
                    var reason = Expect(TokenKind.String, "Expected a rejection reason.").Value;
                    if (reason.Length is < 1 or > 1024 || reason.Contains('\0'))
                        Throw(command, "The rejection reason must contain from 1 through 1024 characters.");
                    ExpectSemicolon(command);
                    return new SieveReject(reason);
                }
                case "setflag":
                case "addflag":
                case "removeflag":
                {
                    Require("imap4flags", command);
                    var flags = ParseStringList();
                    ValidateFlags(command, flags);
                    ExpectSemicolon(command);
                    return command.Value.ToLowerInvariant() switch
                    {
                        "setflag" => new SieveSetFlags(flags),
                        "addflag" => new SieveAddFlags(flags),
                        _ => new SieveRemoveFlags(flags),
                    };
                }
                default:
                    Throw(command, $"Unknown Sieve command '{command.Value}'.");
                    return null!;
            }
        }

        private SieveIf ParseIf(int depth)
        {
            var branches = new List<SieveBranch>
            {
                new(ParseTest(depth), ParseBlock(depth)),
            };
            while (AtIdentifier("elsif"))
            {
                Consume();
                branches.Add(new SieveBranch(ParseTest(depth), ParseBlock(depth)));
            }

            IReadOnlyList<SieveStatement> elseStatements = [];
            if (AtIdentifier("else"))
            {
                Consume();
                elseStatements = ParseBlock(depth);
            }
            return new SieveIf(branches, elseStatements);
        }

        private IReadOnlyList<SieveStatement> ParseBlock(int depth)
        {
            EnsureDepth(depth);
            Expect(TokenKind.LeftBrace, "Expected '{'.");
            var statements = new List<SieveStatement>();
            while (!At(TokenKind.RightBrace))
            {
                if (At(TokenKind.End))
                    Throw(Current, "Unterminated Sieve block.");
                if (AtIdentifier("require"))
                    Throw(Current, "require is only valid at the top level.");
                statements.Add(ParseStatement(depth));
            }
            Consume();
            return statements;
        }

        private SieveTest ParseTest(int depth)
        {
            EnsureDepth(depth);
            var token = Expect(TokenKind.Identifier, "Expected a Sieve test.");
            switch (token.Value.ToLowerInvariant())
            {
                case "true": return new SieveTrue();
                case "false": return new SieveFalse();
                case "not": return new SieveNot(ParseTest(depth + 1));
                case "allof": return new SieveAllOf(ParseTestList(depth + 1));
                case "anyof": return new SieveAnyOf(ParseTestList(depth + 1));
                case "exists": return new SieveExists(ParseHeaderNameList(token));
                case "size":
                {
                    var tag = Expect(TokenKind.Tag, "Expected :over or :under.");
                    if (!tag.Value.Equals("over", StringComparison.OrdinalIgnoreCase)
                        && !tag.Value.Equals("under", StringComparison.OrdinalIgnoreCase))
                        Throw(tag, "Expected :over or :under.");
                    var number = Expect(TokenKind.Number, "Expected a size.");
                    return new SieveSize(ParseNumber(number), tag.Value.Equals("over", StringComparison.OrdinalIgnoreCase));
                }
                case "header":
                {
                    var options = ParseMatchOptions(token, allowAddressPart: false, allowBodyTransform: false, out _, out _, out _);
                    return new SieveHeader(options, ParseHeaderNameList(token), ParseStringList());
                }
                case "address":
                {
                    var options = ParseMatchOptions(token, allowAddressPart: true, allowBodyTransform: false, out var part, out _, out _);
                    return new SieveAddress(options, part, ParseHeaderNameList(token), ParseStringList());
                }
                case "envelope":
                {
                    Require("envelope", token);
                    var options = ParseMatchOptions(token, allowAddressPart: true, allowBodyTransform: false, out var part, out _, out _);
                    var fields = ParseStringList();
                    if (fields.Any(field => !field.Equals("from", StringComparison.OrdinalIgnoreCase)
                        && !field.Equals("to", StringComparison.OrdinalIgnoreCase)))
                        Throw(token, "Envelope tests support only from and to.");
                    return new SieveEnvelope(options, part, fields, ParseStringList());
                }
                case "body":
                {
                    Require("body", token);
                    var options = ParseMatchOptions(token, allowAddressPart: false, allowBodyTransform: true, out _, out var transform, out var contentTypes);
                    return new SieveBody(options, transform, contentTypes, ParseStringList());
                }
                case "mailboxexists":
                    Require("mailbox", token);
                    var folders = ParseStringList();
                    if (folders.Any(folder => !MailboxName.IsValid(folder)))
                        Throw(token, "A mailbox name is invalid.");
                    return new SieveMailboxExists(folders);
                default:
                    Throw(token, $"Unknown Sieve test '{token.Value}'.");
                    return null!;
            }
        }

        private IReadOnlyList<SieveTest> ParseTestList(int depth)
        {
            Expect(TokenKind.LeftParenthesis, "Expected '('.");
            var tests = new List<SieveTest> { ParseTest(depth) };
            while (At(TokenKind.Comma))
            {
                Consume();
                tests.Add(ParseTest(depth));
            }
            Expect(TokenKind.RightParenthesis, "Expected ')'.");
            return tests;
        }

        private MatchOptions ParseMatchOptions(
            Token command,
            bool allowAddressPart,
            bool allowBodyTransform,
            out SieveAddressPart addressPart,
            out SieveBodyTransform bodyTransform,
            out IReadOnlyList<string> contentTypes)
        {
            var matchType = SieveMatchType.Is;
            var comparator = "i;ascii-casemap";
            addressPart = SieveAddressPart.All;
            bodyTransform = SieveBodyTransform.Text;
            contentTypes = [];
            var hasMatchType = false;
            var hasComparator = false;
            var hasAddressPart = false;
            var hasBodyTransform = false;
            while (At(TokenKind.Tag))
            {
                var tag = Consume();
                switch (tag.Value.ToLowerInvariant())
                {
                    case "is":
                        RejectDuplicateTag(ref hasMatchType, tag);
                        matchType = SieveMatchType.Is;
                        break;
                    case "contains":
                        RejectDuplicateTag(ref hasMatchType, tag);
                        matchType = SieveMatchType.Contains;
                        break;
                    case "matches":
                        RejectDuplicateTag(ref hasMatchType, tag);
                        matchType = SieveMatchType.Matches;
                        break;
                    case "comparator":
                        RejectDuplicateTag(ref hasComparator, tag);
                        comparator = Expect(TokenKind.String, "Expected a comparator name.").Value;
                        if (!comparator.Equals("i;ascii-casemap", StringComparison.OrdinalIgnoreCase)
                            && !comparator.Equals("i;octet", StringComparison.OrdinalIgnoreCase))
                            Throw(tag, $"Comparator '{comparator}' is not supported.");
                        break;
                    case "all" when allowAddressPart:
                        RejectDuplicateTag(ref hasAddressPart, tag);
                        addressPart = SieveAddressPart.All;
                        break;
                    case "localpart" when allowAddressPart:
                        RejectDuplicateTag(ref hasAddressPart, tag);
                        addressPart = SieveAddressPart.LocalPart;
                        break;
                    case "domain" when allowAddressPart:
                        RejectDuplicateTag(ref hasAddressPart, tag);
                        addressPart = SieveAddressPart.Domain;
                        break;
                    case "raw" when allowBodyTransform:
                        RejectDuplicateTag(ref hasBodyTransform, tag);
                        bodyTransform = SieveBodyTransform.Raw;
                        break;
                    case "text" when allowBodyTransform:
                        RejectDuplicateTag(ref hasBodyTransform, tag);
                        bodyTransform = SieveBodyTransform.Text;
                        break;
                    case "content" when allowBodyTransform:
                        RejectDuplicateTag(ref hasBodyTransform, tag);
                        bodyTransform = SieveBodyTransform.Content;
                        contentTypes = ParseStringList();
                        if (contentTypes.Any(contentType => !IsValidContentType(contentType)))
                            Throw(tag, "A body content type is invalid.");
                        break;
                    default: Throw(tag, $"Unknown test tag ':{tag.Value}' for {command.Value}."); break;
                }
            }
            return new MatchOptions(matchType, comparator);
        }

        private static void RejectDuplicateTag(ref bool seen, Token tag)
        {
            if (seen)
                Throw(tag, $"Duplicate or conflicting test tag ':{tag.Value}'.");
            seen = true;
        }

        private IReadOnlyList<string> ParseHeaderNameList(Token command)
        {
            var names = ParseStringList();
            if (names.Any(name => name.Length is < 1 or > 998
                || name.Any(character => character is < '!' or > '~' or ':')))
            {
                Throw(command, "A header field name is invalid.");
            }
            return names;
        }

        private static bool IsValidContentType(string value)
        {
            var separator = value.IndexOf('/');
            return separator > 0
                && separator == value.LastIndexOf('/')
                && separator < value.Length - 1
                && IsMimeToken(value[..separator])
                && (value[(separator + 1)..] == "*"
                    || IsMimeToken(value[(separator + 1)..]));
        }

        private static bool IsMimeToken(string value) => value.All(character =>
            character is > ' ' and < '\u007f'
            && character is not '(' and not ')' and not '<' and not '>' and not '@'
                and not ',' and not ';' and not ':' and not '\\' and not '"'
                and not '/' and not '[' and not ']' and not '?' and not '=');

        private IReadOnlyList<string>? ParseOptionalFlags(Token command)
        {
            if (!AtTag("flags"))
                return null;
            Require("imap4flags", Consume());
            var flags = ParseStringList();
            ValidateFlags(command, flags);
            return flags;
        }

        private static void ValidateFlags(Token token, IReadOnlyList<string> flags)
        {
            if (flags.Count > 128)
                throw new SieveParseException(token.Line, token.Column, "Too many message flags.");
            foreach (var flag in flags)
            {
                if (!IsValidFlag(flag))
                    throw new SieveParseException(token.Line, token.Column, $"Message flag '{flag}' is invalid.");
            }
        }

        private static bool IsValidFlag(string flag)
        {
            if (flag.Length is < 1 or > 255)
                return false;
            if (flag[0] == '\\')
                return flag.ToUpperInvariant() is "\\SEEN" or "\\DELETED" or "\\FLAGGED" or "\\DRAFT" or "\\ANSWERED";
            return flag.All(character => character > ' '
                && character < '\u007f'
                && character is not '(' and not ')' and not '{' and not '%' and not '*' and not ']');
        }

        private IReadOnlyList<string> ParseStringList()
        {
            if (!At(TokenKind.LeftBracket))
                return [Expect(TokenKind.String, "Expected a string or string list.").Value];

            Consume();
            var values = new List<string>();
            if (At(TokenKind.RightBracket))
                Throw(Current, "A string list cannot be empty.");
            values.Add(Expect(TokenKind.String, "Expected a string.").Value);
            while (At(TokenKind.Comma))
            {
                Consume();
                values.Add(Expect(TokenKind.String, "Expected a string.").Value);
            }
            Expect(TokenKind.RightBracket, "Expected ']'.");
            return values;
        }

        private static long ParseNumber(Token token)
        {
            var multiplier = 1L;
            var digits = token.Value;
            if (!char.IsAsciiDigit(token.Value[^1]))
            {
                multiplier = char.ToUpperInvariant(token.Value[^1]) switch
                {
                    'K' => 1024L,
                    'M' => 1024L * 1024L,
                    'G' => 1024L * 1024L * 1024L,
                    _ => 1L,
                };
                digits = token.Value[..^1];
            }
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number > long.MaxValue / multiplier)
                throw new SieveParseException(token.Line, token.Column, "The numeric value is too large.");
            return number * multiplier;
        }

        private void Require(string capability, Token token)
        {
            if (!_required.Contains(capability))
                Throw(token, $"The '{capability}' capability must be declared with require.");
        }

        private void EnsureDepth(int depth)
        {
            if (depth > MaximumNestingDepth)
                Throw(Current, "The script nesting limit was exceeded.");
        }

        private void ExpectSemicolon(Token command) =>
            Expect(TokenKind.Semicolon, $"Expected ';' after {command.Value}.");

        private bool At(TokenKind kind) => Current.Kind == kind;
        private bool AtIdentifier(string value) =>
            At(TokenKind.Identifier) && Current.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        private bool AtTag(string value) =>
            At(TokenKind.Tag) && Current.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        private Token Current => tokens[_position];
        private Token Consume() => tokens[_position++];

        private Token Expect(TokenKind kind, string message)
        {
            if (!At(kind))
                Throw(Current, message);
            return Consume();
        }

        private static void Throw(Token token, string message) =>
            throw new SieveParseException(token.Line, token.Column, message);
    }

    private enum TokenKind
    {
        Identifier,
        Tag,
        String,
        Number,
        LeftBracket,
        RightBracket,
        LeftParenthesis,
        RightParenthesis,
        LeftBrace,
        RightBrace,
        Semicolon,
        Comma,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Value, int Line, int Column);

    private sealed class SieveParseException(int line, int column, string message) : Exception(message)
    {
        public int Line { get; } = line;
        public int Column { get; } = column;
    }
}

internal sealed record SieveProgram(IReadOnlyList<SieveStatement> Statements);
internal abstract record SieveStatement;
internal sealed record SieveBranch(SieveTest Test, IReadOnlyList<SieveStatement> Statements);
internal sealed record SieveIf(IReadOnlyList<SieveBranch> Branches, IReadOnlyList<SieveStatement> ElseStatements) : SieveStatement;
internal sealed record SieveKeep(IReadOnlyList<string>? Flags) : SieveStatement;
internal sealed record SieveFileInto(string Folder, bool Copy, bool Create, IReadOnlyList<string>? Flags) : SieveStatement;
internal sealed record SieveRedirect(string Address, bool Copy) : SieveStatement;
internal sealed record SieveDiscard : SieveStatement;
internal sealed record SieveStop : SieveStatement;
internal sealed record SieveReject(string Reason) : SieveStatement;
internal sealed record SieveSetFlags(IReadOnlyList<string> Flags) : SieveStatement;
internal sealed record SieveAddFlags(IReadOnlyList<string> Flags) : SieveStatement;
internal sealed record SieveRemoveFlags(IReadOnlyList<string> Flags) : SieveStatement;

internal abstract record SieveTest;
internal sealed record SieveTrue : SieveTest;
internal sealed record SieveFalse : SieveTest;
internal sealed record SieveNot(SieveTest Test) : SieveTest;
internal sealed record SieveAllOf(IReadOnlyList<SieveTest> Tests) : SieveTest;
internal sealed record SieveAnyOf(IReadOnlyList<SieveTest> Tests) : SieveTest;
internal sealed record SieveExists(IReadOnlyList<string> HeaderNames) : SieveTest;
internal sealed record SieveSize(long Bytes, bool Over) : SieveTest;
internal sealed record SieveHeader(MatchOptions Options, IReadOnlyList<string> HeaderNames, IReadOnlyList<string> Keys) : SieveTest;
internal sealed record SieveAddress(MatchOptions Options, SieveAddressPart AddressPart, IReadOnlyList<string> HeaderNames, IReadOnlyList<string> Keys) : SieveTest;
internal sealed record SieveEnvelope(MatchOptions Options, SieveAddressPart AddressPart, IReadOnlyList<string> Fields, IReadOnlyList<string> Keys) : SieveTest;
internal sealed record SieveBody(MatchOptions Options, SieveBodyTransform Transform, IReadOnlyList<string> ContentTypes, IReadOnlyList<string> Keys) : SieveTest;
internal sealed record SieveMailboxExists(IReadOnlyList<string> Folders) : SieveTest;
internal sealed record MatchOptions(SieveMatchType MatchType, string Comparator);
internal enum SieveMatchType { Is, Contains, Matches }
internal enum SieveAddressPart { All, LocalPart, Domain }
internal enum SieveBodyTransform { Text, Raw, Content }
