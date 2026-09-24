using System.Text;
using Microsoft.EntityFrameworkCore;
using mk8.email.Infrastructure.Models;
using mk8.email.MailWire;

namespace mk8.email.Application.Services;

internal static class ImapSearchEngine
{
    private const int MaximumSearchTokens = 4096;
    private const int MaximumSearchNestingDepth = 64;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly record struct MessageSetRange(int Start, int End);

    [Flags]
    private enum SearchDataRequirements
    {
        None = 0,
        Body = 1,
        RawHeaders = 2,
        ModSequenceResult = 4,
    }

    private enum SearchTokenKind
    {
        Atom,
        OpenParenthesis,
        CloseParenthesis,
    }

    private readonly record struct SearchToken(SearchTokenKind Kind, string Value);

    private sealed record SearchStoredMessage(
        Guid Id,
        int Uid,
        string Sender,
        string Recipient,
        string Subject,
        string Body,
        bool IsRead,
        bool IsDeleted,
        bool IsFlagged,
        bool IsDraft,
        bool IsAnswered,
        string[] Keywords,
        long ModSeq,
        int SizeBytes,
        string? RawHeaders,
        string? MessageId,
        string? InReplyTo,
        string? Cc,
        string? EmailObjectId,
        string? ThreadObjectId,
        DateTime ReceivedAt);

    internal sealed record SearchCandidate(Guid Id, int Uid, int SequenceNumber);

    internal sealed record SearchExecutionResult(
        IReadOnlyList<SearchCandidate> Matches,
        string? FailureResponse,
        long? HighestModSequence);

    private sealed record SearchPredicate(
        SearchDataRequirements Requirements,
        Func<SearchStoredMessage, int, bool> IsMatch);

    private sealed class SearchParser(
        IReadOnlyList<SearchToken> tokens,
        int maximumSequenceNumber,
        int maximumUid,
        IReadOnlySet<int> savedSearchUids,
        bool utf8Enabled)
    {
        private int _index;
        private string _failureResponse = "BAD Invalid search criteria";

        public bool TryParse(out SearchPredicate predicate, out string failureResponse)
        {
            predicate = MatchNothing();

            if (tokens.Count == 0)
            {
                failureResponse = _failureResponse;
                return false;
            }

            if (CurrentAtomEquals("CHARSET"))
            {
                _index++;
                if (!TryReadValue(out var charset))
                {
                    failureResponse = _failureResponse;
                    return false;
                }

                var supportedCharset = utf8Enabled ? "UTF-8" : "US-ASCII";
                if (!charset.Equals(supportedCharset, StringComparison.OrdinalIgnoreCase))
                {
                    failureResponse = utf8Enabled
                        ? "BAD SEARCH charset conflicts with UTF8=ACCEPT"
                        : "NO [BADCHARSET (US-ASCII)] Unsupported search charset";
                    return false;
                }
            }

            if (_index >= tokens.Count
                || !TryParseConjunction(stopAtCloseParenthesis: false, depth: 0, out predicate)
                || _index != tokens.Count)
            {
                failureResponse = _failureResponse;
                return false;
            }

            failureResponse = string.Empty;
            return true;
        }

        private bool TryParseConjunction(
            bool stopAtCloseParenthesis,
            int depth,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (depth > MaximumSearchNestingDepth)
                return false;

            var predicates = new List<SearchPredicate>();
            while (_index < tokens.Count
                   && tokens[_index].Kind != SearchTokenKind.CloseParenthesis)
            {
                if (!TryParseKey(depth, out var item))
                    return false;
                predicates.Add(item);
            }

            if (predicates.Count == 0)
                return false;

            if (stopAtCloseParenthesis)
            {
                if (_index >= tokens.Count
                    || tokens[_index].Kind != SearchTokenKind.CloseParenthesis)
                {
                    return false;
                }

                _index++;
            }
            else if (_index < tokens.Count)
            {
                return false;
            }

            predicate = CombineAnd(predicates);
            return true;
        }

        private bool TryParseKey(int depth, out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (depth > MaximumSearchNestingDepth || _index >= tokens.Count)
                return false;

            var token = tokens[_index++];
            if (token.Kind == SearchTokenKind.OpenParenthesis)
            {
                return TryParseConjunction(
                    stopAtCloseParenthesis: true,
                    depth + 1,
                    out predicate);
            }

            if (token.Kind != SearchTokenKind.Atom)
                return false;

            if (string.Equals(token.Value, "$", StringComparison.Ordinal))
            {
                predicate = new SearchPredicate(
                    SearchDataRequirements.None,
                    (message, _) => savedSearchUids.Contains(message.Uid));
                return true;
            }

            if (LooksLikeMessageSet(token.Value))
            {
                if (!TryParseMessageSet(
                        token.Value,
                        maximumSequenceNumber,
                        out var sequenceRanges))
                {
                    return false;
                }

                predicate = new SearchPredicate(
                    SearchDataRequirements.None,
                    (_, sequenceNumber) => MessageSetContains(sequenceRanges, sequenceNumber));
                return true;
            }

            switch (token.Value.ToUpperInvariant())
            {
                case "ALL":
                    predicate = MatchEverything();
                    return true;
                case "ANSWERED":
                    predicate = Flag(static message => message.IsAnswered);
                    return true;
                case "UNANSWERED":
                    predicate = Flag(static message => !message.IsAnswered);
                    return true;
                case "DELETED":
                    predicate = Flag(static message => message.IsDeleted);
                    return true;
                case "UNDELETED":
                    predicate = Flag(static message => !message.IsDeleted);
                    return true;
                case "DRAFT":
                    predicate = Flag(static message => message.IsDraft);
                    return true;
                case "UNDRAFT":
                    predicate = Flag(static message => !message.IsDraft);
                    return true;
                case "FLAGGED":
                    predicate = Flag(static message => message.IsFlagged);
                    return true;
                case "UNFLAGGED":
                    predicate = Flag(static message => !message.IsFlagged);
                    return true;
                case "SEEN":
                    predicate = Flag(static message => message.IsRead);
                    return true;
                case "UNSEEN":
                    predicate = Flag(static message => !message.IsRead);
                    return true;
                case "NEW":
                case "RECENT":
                    predicate = MatchNothing();
                    return true;
                case "OLD":
                    predicate = MatchEverything();
                    return true;
                case "NOT":
                    if (!TryParseKey(depth + 1, out var negated))
                        return false;
                    predicate = new SearchPredicate(
                        negated.Requirements,
                        (message, sequenceNumber) => !negated.IsMatch(message, sequenceNumber));
                    return true;
                case "OR":
                    if (!TryParseKey(depth + 1, out var left)
                        || !TryParseKey(depth + 1, out var right))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        left.Requirements | right.Requirements,
                        (message, sequenceNumber) => left.IsMatch(message, sequenceNumber)
                            || right.IsMatch(message, sequenceNumber));
                    return true;
                case "BCC":
                    return TryParseHeaderValue("Bcc", out predicate);
                case "CC":
                    return TryParseHeaderValue("Cc", out predicate);
                case "FROM":
                    return TryParseHeaderValue("From", out predicate);
                case "SUBJECT":
                    if (!TryReadValue(out var subject))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => ContainsSearchText(message.Subject, subject));
                    return true;
                case "EMAILID":
                    if (!TryReadValue(out var emailObjectId)
                        || !IsValidObjectId(emailObjectId))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => string.Equals(
                            FormatEmailObjectId(message.Id, message.EmailObjectId),
                            emailObjectId,
                            StringComparison.Ordinal));
                    return true;
                case "THREADID":
                    if (!TryReadValue(out var threadObjectId)
                        || !IsValidObjectId(threadObjectId))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => string.Equals(
                            FormatThreadObjectId(message.Id, message.ThreadObjectId),
                            threadObjectId,
                            StringComparison.Ordinal));
                    return true;
                case "TO":
                    return TryParseHeaderValue("To", out predicate);
                case "BODY":
                    if (!TryReadValue(out var bodyText))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.Body,
                        (message, _) => ContainsSearchText(message.Body, bodyText));
                    return true;
                case "TEXT":
                    if (!TryReadValue(out var text))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.Body | SearchDataRequirements.RawHeaders,
                        (message, _) => MessageTextContains(message, text));
                    return true;
                case "HEADER":
                    if (!TryReadValue(out var headerName)
                        || headerName.Length == 0
                        || !TryReadValue(out var headerValue))
                    {
                        return false;
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.RawHeaders,
                        (message, _) => HeaderContains(
                            message.RawHeaders,
                            headerName,
                            headerValue));
                    return true;
                case "BEFORE":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate < searchDate,
                        out predicate);
                case "ON":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate == searchDate,
                        out predicate);
                case "SINCE":
                    return TryParseReceivedDate(
                        static (messageDate, searchDate) => messageDate >= searchDate,
                        out predicate);
                case "SENTBEFORE":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate < searchDate,
                        out predicate);
                case "SENTON":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate == searchDate,
                        out predicate);
                case "SENTSINCE":
                    return TryParseSentDate(
                        static (messageDate, searchDate) => messageDate >= searchDate,
                        out predicate);
                case "LARGER":
                    if (!TryReadUnsignedNumber(out var larger))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => message.SizeBytes > larger);
                    return true;
                case "SMALLER":
                    if (!TryReadUnsignedNumber(out var smaller))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => message.SizeBytes < smaller);
                    return true;
                case "UID":
                    if (!TryReadValue(out var uidSet))
                    {
                        return false;
                    }

                    if (string.Equals(uidSet, "$", StringComparison.Ordinal))
                    {
                        predicate = new SearchPredicate(
                            SearchDataRequirements.None,
                            (message, _) => savedSearchUids.Contains(message.Uid));
                        return true;
                    }

                    if (!TryParseMessageSet(uidSet, maximumUid, out var uidRanges))
                        return false;

                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => MessageSetContains(uidRanges, message.Uid));
                    return true;
                case "KEYWORD":
                    if (!TryReadValue(out var keyword))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => HasKeyword(message, keyword));
                    return true;
                case "UNKEYWORD":
                    if (!TryReadValue(out var absentKeyword))
                        return false;
                    predicate = new SearchPredicate(
                        SearchDataRequirements.None,
                        (message, _) => !HasKeyword(message, absentKeyword));
                    return true;
                case "MODSEQ":
                    if (!TryReadValue(out var modSequenceText))
                        return false;
                    if (!TryParseModSequence(modSequenceText, out var modSequence))
                    {
                        if (!modSequenceText.StartsWith("/flags/", StringComparison.OrdinalIgnoreCase)
                            || !TryReadValue(out var entryType)
                            || entryType.ToUpperInvariant() is not ("SHARED" or "PRIV" or "ALL")
                            || !TryReadValue(out modSequenceText)
                            || !TryParseModSequence(modSequenceText, out modSequence))
                        {
                            return false;
                        }
                    }

                    predicate = new SearchPredicate(
                        SearchDataRequirements.ModSequenceResult,
                        (message, _) => message.ModSeq >= modSequence);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryParseModSequence(string value, out long modSequence) =>
            long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out modSequence)
            && modSequence >= 0;

        private bool TryParseHeaderValue(string headerName, out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value))
                return false;

            predicate = new SearchPredicate(
                SearchDataRequirements.RawHeaders,
                (message, _) => HeaderContains(message.RawHeaders, headerName, value));
            return true;
        }

        private bool TryParseReceivedDate(
            Func<DateOnly, DateOnly, bool> comparison,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value)
                || !TryParseImapDate(value, out var parsedDate))
            {
                return false;
            }

            var searchDate = DateOnly.FromDateTime(parsedDate);
            predicate = new SearchPredicate(
                SearchDataRequirements.None,
                (message, _) => comparison(
                    DateOnly.FromDateTime(message.ReceivedAt),
                    searchDate));
            return true;
        }

        private bool TryParseSentDate(
            Func<DateOnly, DateOnly, bool> comparison,
            out SearchPredicate predicate)
        {
            predicate = MatchNothing();
            if (!TryReadValue(out var value)
                || !TryParseImapDate(value, out var parsedDate))
            {
                return false;
            }

            var searchDate = DateOnly.FromDateTime(parsedDate);
            predicate = new SearchPredicate(
                SearchDataRequirements.RawHeaders,
                (message, _) => TryGetSentDate(message.RawHeaders, out var sentDate)
                    && comparison(sentDate, searchDate));
            return true;
        }

        private bool TryReadUnsignedNumber(out long value)
        {
            value = 0;
            return TryReadValue(out var text)
                && long.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value)
                && value >= 0
                && value <= uint.MaxValue;
        }

        private bool TryReadValue(out string value)
        {
            value = string.Empty;
            if (_index >= tokens.Count || tokens[_index].Kind != SearchTokenKind.Atom)
                return false;

            value = tokens[_index++].Value;
            return true;
        }

        private bool CurrentAtomEquals(string value) =>
            _index < tokens.Count
            && tokens[_index].Kind == SearchTokenKind.Atom
            && tokens[_index].Value.Equals(value, StringComparison.OrdinalIgnoreCase);

        private static SearchPredicate CombineAnd(IReadOnlyList<SearchPredicate> predicates)
        {
            if (predicates.Count == 1)
                return predicates[0];

            var requirements = SearchDataRequirements.None;
            foreach (var predicate in predicates)
                requirements |= predicate.Requirements;

            return new SearchPredicate(
                requirements,
                (message, sequenceNumber) =>
                {
                    foreach (var predicate in predicates)
                    {
                        if (!predicate.IsMatch(message, sequenceNumber))
                            return false;
                    }

                    return true;
                });
        }

        private static SearchPredicate Flag(Func<SearchStoredMessage, bool> predicate) =>
            new(SearchDataRequirements.None, (message, _) => predicate(message));

        private static SearchPredicate MatchEverything() =>
            new(SearchDataRequirements.None, static (_, _) => true);

        private static SearchPredicate MatchNothing() =>
            new(SearchDataRequirements.None, static (_, _) => false);
    }

    internal static async Task<SearchExecutionResult> FindSearchCandidatesAsync(
        IQueryable<EmailDB> query,
        MailboxMessageContentService content,
        string criteria,
        IReadOnlySet<int> savedSearchUids,
        bool utf8Enabled,
        CancellationToken cancellationToken,
        string? explicitCharset = null)
    {
        var decodeUtf8 = explicitCharset?.Equals(
            "UTF-8",
            StringComparison.OrdinalIgnoreCase) == true || explicitCharset is null && utf8Enabled;
        if (decodeUtf8 && !TryDecodeUtf8WireValue(criteria, out criteria))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid UTF-8 in search criteria",
                null);
        }
        if (explicitCharset?.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase) == true
            && criteria.Any(character => character > '\u007f'))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid US-ASCII in search criteria",
                null);
        }

        var bounds = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                MaximumUid = group.Max(message => message.Uid),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var maximumSequenceNumber = bounds?.Count ?? 0;
        var maximumUid = bounds?.MaximumUid ?? 0;

        if (!TryTokenizeSearchCriteria(criteria, out var tokens))
        {
            return new SearchExecutionResult(
                [],
                "BAD Invalid search criteria",
                null);
        }

        var parser = new SearchParser(
            tokens,
            maximumSequenceNumber,
            maximumUid,
            savedSearchUids,
            utf8Enabled);
        if (!parser.TryParse(out var predicate, out var failureResponse))
            return new SearchExecutionResult([], failureResponse, null);

        var includeBody = predicate.Requirements.HasFlag(SearchDataRequirements.Body);
        var includeRawHeaders = predicate.Requirements.HasFlag(SearchDataRequirements.RawHeaders);
        var includeModSequence = predicate.Requirements.HasFlag(
            SearchDataRequirements.ModSequenceResult);
        List<SearchStoredMessage> messages;
        if (includeBody || includeRawHeaders)
        {
            var stored = await query
                .OrderBy(message => message.Uid)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            messages = new List<SearchStoredMessage>(stored.Count);
            foreach (var email in stored)
            {
                var rawMessage = await content.ReadAsync(email, cancellationToken).ConfigureAwait(false);
                ApplyTransientRawMessage(email, rawMessage);
                messages.Add(CreateSearchStoredMessage(email, includeBody, includeRawHeaders));
            }
        }
        else
        {
            messages = await BuildSearchMessageQuery(query, false, false)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        var matches = new List<SearchCandidate>();
        long? highestModSequence = null;
        var sequenceNumber = 0;
        foreach (var message in messages)
        {
            sequenceNumber++;
            if (predicate.IsMatch(message, sequenceNumber))
            {
                matches.Add(new SearchCandidate(message.Id, message.Uid, sequenceNumber));
                if (includeModSequence
                    && (highestModSequence is null || message.ModSeq > highestModSequence))
                {
                    highestModSequence = message.ModSeq;
                }
            }
        }

        return new SearchExecutionResult(matches, null, highestModSequence);
    }

    private static SearchStoredMessage CreateSearchStoredMessage(
        EmailDB message,
        bool includeBody,
        bool includeRawHeaders) =>
        new(
            message.Id,
            message.Uid,
            message.Sender,
            message.Recipient,
            message.Subject,
            includeBody ? message.Body : string.Empty,
            message.IsRead,
            message.IsDeleted,
            message.IsFlagged,
            message.IsDraft,
            message.IsAnswered,
            message.Keywords,
            message.ModSeq,
            message.SizeBytes,
            includeRawHeaders ? message.RawHeaders : null,
            message.MessageId,
            message.InReplyTo,
            message.Cc,
            message.EmailObjectId,
            message.ThreadObjectId,
            message.ReceivedAt);

    private static IQueryable<SearchStoredMessage> BuildSearchMessageQuery(
        IQueryable<EmailDB> query,
        bool includeBody,
        bool includeRawHeaders)
    {
        var ordered = query.OrderBy(message => message.Uid);
        if (includeBody && includeRawHeaders)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                message.Body,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
                message.ReceivedAt));
        }

        if (includeBody)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                message.Body,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                null,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
                message.ReceivedAt));
        }

        if (includeRawHeaders)
        {
            return ordered.Select(message => new SearchStoredMessage(
                message.Id,
                message.Uid,
                message.Sender,
                message.Recipient,
                message.Subject,
                string.Empty,
                message.IsRead,
                message.IsDeleted,
                message.IsFlagged,
                message.IsDraft,
                message.IsAnswered,
                message.Keywords,
                message.ModSeq,
                message.SizeBytes,
                message.RawHeaders,
                message.MessageId,
                message.InReplyTo,
                message.Cc,
                message.EmailObjectId,
                message.ThreadObjectId,
                message.ReceivedAt));
        }

        return ordered.Select(message => new SearchStoredMessage(
            message.Id,
            message.Uid,
            message.Sender,
            message.Recipient,
            message.Subject,
            string.Empty,
            message.IsRead,
            message.IsDeleted,
            message.IsFlagged,
            message.IsDraft,
            message.IsAnswered,
            message.Keywords,
            message.ModSeq,
            message.SizeBytes,
            null,
            message.MessageId,
            message.InReplyTo,
            message.Cc,
            message.EmailObjectId,
            message.ThreadObjectId,
            message.ReceivedAt));
    }

    private static bool TryTokenizeSearchCriteria(
        string criteria,
        out List<SearchToken> tokens)
    {
        tokens = [];
        var index = 0;
        while (index < criteria.Length)
        {
            while (index < criteria.Length && criteria[index] == ' ')
                index++;

            if (index >= criteria.Length)
                break;

            if (tokens.Count >= MaximumSearchTokens)
                return false;

            if (criteria[index] == '(')
            {
                tokens.Add(new SearchToken(SearchTokenKind.OpenParenthesis, "("));
                index++;
                continue;
            }

            if (criteria[index] == ')')
            {
                tokens.Add(new SearchToken(SearchTokenKind.CloseParenthesis, ")"));
                index++;
                if (index < criteria.Length
                    && criteria[index] != ' '
                    && criteria[index] != ')')
                {
                    return false;
                }
                continue;
            }

            if (criteria[index] == '"')
            {
                index++;
                var value = new StringBuilder();
                var terminated = false;
                while (index < criteria.Length)
                {
                    var character = criteria[index++];
                    if (character == '"')
                    {
                        terminated = true;
                        break;
                    }

                    if (character == '\\')
                    {
                        if (index >= criteria.Length)
                            return false;
                        character = criteria[index++];
                        if (character is not '\\' and not '"')
                            return false;
                    }

                    if (character == '\0')
                        return false;
                    value.Append(character);
                }

                if (!terminated)
                    return false;
                if (index < criteria.Length
                    && criteria[index] != ' '
                    && criteria[index] != ')')
                {
                    return false;
                }

                tokens.Add(new SearchToken(SearchTokenKind.Atom, value.ToString()));
                continue;
            }

            var start = index;
            while (index < criteria.Length
                   && criteria[index] != ' '
                   && criteria[index] is not '(' and not ')')
            {
                if (criteria[index] is '\r' or '\n' or '\0')
                    return false;
                index++;
            }

            if (index == start)
                return false;
            if (index < criteria.Length && criteria[index] == '(')
                return false;

            tokens.Add(new SearchToken(
                SearchTokenKind.Atom,
                criteria[start..index]));
        }

        return tokens.Count <= MaximumSearchTokens;
    }

    private static bool LooksLikeMessageSet(string value)
    {
        if (value.Length == 0 || value[0] is not ('*' or >= '0' and <= '9'))
            return false;

        foreach (var character in value)
        {
            if (character is not ('*' or ':' or ',' or >= '0' and <= '9'))
                return false;
        }

        return true;
    }

    private static bool ContainsSearchText(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MessageTextContains(SearchStoredMessage message, string value)
    {
        if (ContainsSearchText(message.Body, value)
            || HeaderBlockContains(message.RawHeaders, value))
        {
            return true;
        }

        if (message.RawHeaders is not null)
            return false;

        return ContainsSearchText(message.Sender, value)
            || ContainsSearchText(message.Recipient, value)
            || ContainsSearchText(message.Subject, value)
            || ContainsSearchText(message.Cc, value)
            || ContainsSearchText(message.MessageId, value)
            || ContainsSearchText(message.InReplyTo, value);
    }

    private static bool HeaderBlockContains(string? rawHeaders, string value)
    {
        if (string.IsNullOrEmpty(rawHeaders))
            return false;
        if (ContainsSearchText(rawHeaders, value))
            return true;

        using var reader = new StringReader(rawHeaders);
        var unfolded = new StringBuilder(rawHeaders.Length);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                unfolded.Append(' ').Append(line.Trim());
            }
            else
            {
                if (unfolded.Length > 0)
                    unfolded.Append('\n');
                unfolded.Append(line);
            }
        }

        return ContainsSearchText(unfolded.ToString(), value);
    }

    private static bool HasKeyword(SearchStoredMessage message, string keyword) =>
        keyword.ToUpperInvariant() switch
        {
            "\\SEEN" => message.IsRead,
            "\\DELETED" => message.IsDeleted,
            "\\FLAGGED" => message.IsFlagged,
            "\\DRAFT" => message.IsDraft,
            "\\ANSWERED" => message.IsAnswered,
            "\\RECENT" => false,
            _ => message.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase),
        };

    private static bool HeaderContains(
        string? rawHeaders,
        string headerName,
        string searchValue)
    {
        if (string.IsNullOrEmpty(rawHeaders))
            return false;

        using var reader = new StringReader(rawHeaders);
        string? currentName = null;
        var currentValue = new StringBuilder();

        bool CurrentHeaderMatches() => currentName is not null
            && currentName.Equals(headerName, StringComparison.OrdinalIgnoreCase)
            && currentValue.ToString().Contains(
                searchValue,
                StringComparison.OrdinalIgnoreCase);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (currentName is not null)
                    currentValue.Append(' ').Append(line.Trim());
                continue;
            }

            if (CurrentHeaderMatches())
                return true;

            currentName = null;
            currentValue.Clear();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            currentName = line[..separator].Trim();
            currentValue.Append(line[(separator + 1)..].Trim());
        }

        return CurrentHeaderMatches();
    }

    private static bool TryGetSentDate(string? rawHeaders, out DateOnly sentDate)
    {
        sentDate = default;
        if (!TryGetHeaderValue(rawHeaders, "Date", out var dateValue)
            || !MimeKit.Utils.DateUtils.TryParse(dateValue, out var parsedDate))
        {
            return false;
        }

        sentDate = DateOnly.FromDateTime(parsedDate.Date);
        return true;
    }

    private static bool TryGetHeaderValue(
        string? rawHeaders,
        string headerName,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(rawHeaders))
            return false;

        using var reader = new StringReader(rawHeaders);
        string? currentName = null;
        string? matchedValue = null;
        var currentValue = new StringBuilder();

        bool TryUseCurrentHeader()
        {
            if (currentName is null
                || !currentName.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            matchedValue = currentValue.ToString();
            return true;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            if (line[0] is ' ' or '\t')
            {
                if (currentName is not null)
                    currentValue.Append(' ').Append(line.Trim());
                continue;
            }

            if (TryUseCurrentHeader())
            {
                value = matchedValue!;
                return true;
            }

            currentName = null;
            currentValue.Clear();
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            currentName = line[..separator].Trim();
            currentValue.Append(line[(separator + 1)..].Trim());
        }

        if (!TryUseCurrentHeader())
            return false;

        value = matchedValue!;
        return true;
    }

    private static bool TryParseImapDate(string dateStr, out DateTime result)
    {
        var unquoted = dateStr.Trim('"');
        return DateTime.TryParseExact(unquoted,
            ["d-MMM-yyyy", "dd-MMM-yyyy"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out result);
    }

    private static bool TryDecodeUtf8WireValue(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Any(character => character > byte.MaxValue))
            return false;
        try
        {
            decoded = StrictUtf8.GetString(MailWireEncoding.Instance.GetBytes(value));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void ApplyTransientRawMessage(EmailDB email, byte[] rawMessage)
    {
        email.RawMessage = rawMessage;
        var raw = MailWireEncoding.Instance.GetString(rawMessage);
        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator >= 0)
        {
            email.RawHeaders = raw[..separator];
            email.Body = raw[(separator + 4)..];
            return;
        }
        separator = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator >= 0)
        {
            email.RawHeaders = raw[..separator];
            email.Body = raw[(separator + 2)..];
            return;
        }
        email.RawHeaders = raw;
        email.Body = string.Empty;
    }

    private static string FormatEmailObjectId(Guid id, string? emailObjectId) =>
        FormatObjectId('M', emailObjectId, id);

    private static string? FormatThreadObjectId(Guid id, string? threadObjectId) =>
        string.IsNullOrEmpty(threadObjectId)
            ? null
            : FormatObjectId('T', threadObjectId, id);

    private static string FormatObjectId(char typePrefix, string? storedValue, Guid fallbackId)
    {
        var value = string.IsNullOrEmpty(storedValue)
            ? fallbackId.ToString("N")
            : storedValue;
        if (value.Length > 254 || !IsValidObjectId(value))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(value));
            value = Convert.ToHexStringLower(hash);
        }
        return string.Concat(typePrefix, value);
    }

    private static bool IsValidObjectId(string value) =>
        value.Length is >= 1 and <= 255
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_'
            or '-');

    private static bool TryParseMessageSet(
        string value,
        int maximumIdentifier,
        out List<MessageSetRange> ranges)
    {
        ranges = [];
        if (string.IsNullOrEmpty(value))
            return false;
        var parsedRanges = new List<MessageSetRange>();
        foreach (var part in value.Split(','))
        {
            if (part.Length == 0)
                return false;
            var separator = part.IndexOf(':');
            int start;
            int end;
            if (separator >= 0)
            {
                if (separator == 0
                    || separator == part.Length - 1
                    || part.IndexOf(':', separator + 1) >= 0
                    || !TryParseMessageSetEndpoint(part[..separator], maximumIdentifier, out start)
                    || !TryParseMessageSetEndpoint(part[(separator + 1)..], maximumIdentifier, out end))
                    return false;
            }
            else if (TryParseMessageSetEndpoint(part, maximumIdentifier, out start))
                end = start;
            else
                return false;
            if (start > end)
                (start, end) = (end, start);
            parsedRanges.Add(new MessageSetRange(start, end));
        }
        parsedRanges.Sort(static (left, right) =>
        {
            var startComparison = left.Start.CompareTo(right.Start);
            return startComparison != 0 ? startComparison : left.End.CompareTo(right.End);
        });
        foreach (var range in parsedRanges)
        {
            if (ranges.Count == 0)
            {
                ranges.Add(range);
                continue;
            }
            var previous = ranges[^1];
            var adjacent = previous.End < int.MaxValue && range.Start == previous.End + 1;
            if (range.Start <= previous.End || adjacent)
                ranges[^1] = new MessageSetRange(previous.Start, Math.Max(previous.End, range.End));
            else
                ranges.Add(range);
        }
        return true;
    }

    private static bool TryParseMessageSetEndpoint(
        string value,
        int maximumIdentifier,
        out int identifier)
    {
        if (string.Equals(value, "*", StringComparison.Ordinal))
        {
            identifier = maximumIdentifier;
            return true;
        }
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                identifier = 0;
                return false;
            }
        }
        return int.TryParse(value, out identifier) && identifier > 0;
    }

    private static bool MessageSetContains(IReadOnlyList<MessageSetRange> ranges, int identifier)
    {
        var low = 0;
        var high = ranges.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var range = ranges[middle];
            if (identifier < range.Start)
                high = middle - 1;
            else if (identifier > range.End)
                low = middle + 1;
            else
                return true;
        }
        return false;
    }
}
