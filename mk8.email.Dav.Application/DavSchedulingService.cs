using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MimeKit;
using MimeKit.Text;
using mk8.email.Application.Interfaces;
using mk8.email.Contracts.Mail;
using mk8.email.Configuration;

namespace mk8.email.Dav;

internal sealed class DavSchedulingService(
    DavStore store,
    IMailSubmissionQueue queue,
    EnvironmentConfig environment)
{
    private static readonly HashSet<string> SupportedMethods = new(StringComparer.Ordinal)
    {
        "PUBLISH",
        "REQUEST",
        "REPLY",
        "ADD",
        "CANCEL",
        "REFRESH",
        "COUNTER",
        "DECLINECOUNTER",
    };

    private static readonly HashSet<string> OrganizerMethods = new(StringComparer.Ordinal)
    {
        "PUBLISH",
        "REQUEST",
        "ADD",
        "CANCEL",
    };

    // The scheduling request is validated and queued recipient-by-recipient in protocol order.
#pragma warning disable MA0051
    public async Task<DavScheduleSubmissionResult> SubmitAsync(
        AuthenticatedMailUser user,
        IEnumerable<string> originatorHeaders,
        IEnumerable<string> recipientHeaders,
        string? contentType,
        byte[] body,
        string? clientIp,
        CancellationToken cancellationToken)
    {
#pragma warning restore MA0051
        if (!TryParseHeaderAddresses(originatorHeaders, out var originators)
            || originators.Count != 1
            || !string.Equals(originators[0], user.Username, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(403, "The scheduling originator must be the authenticated calendar user.");
        }

        if (!TryParseHeaderAddresses(recipientHeaders, out var recipients)
            || recipients.Count == 0
            || recipients.Count > environment.Limits.MaxRecipientsPerMessage)
        {
            return Failure(400, "The scheduling request has an invalid recipient count.");
        }
        if (!DavContent.TryValidate(
                DavCollectionKind.Calendar,
                contentType,
                body,
                out var content,
                out var contentFailure))
        {
            return Failure(415, contentFailure);
        }
        if (!TryParseRequest(content!, contentType, out var request, out var requestFailure))
            return Failure(400, requestFailure);
        if (!IsAuthorizedSender(user.Username, request!))
            return Failure(403, "The authenticated calendar user cannot send this iTIP method.");

        var results = new List<DavScheduleRecipientResult>(recipients.Count);
        // The recipient loop awaits queue work; a Span cannot cross those awaits.
#pragma warning disable HLQ012
        foreach (var recipient in recipients)
#pragma warning restore HLQ012
        {
            if (!IsExpectedRecipient(request!, recipient))
            {
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "3.7;Invalid Calendar User"));
                continue;
            }

            var resolved = await store.ResolveCalendarRecipientAsync(recipient, cancellationToken).ConfigureAwait(false);
            if (request!.IsFreeBusy)
            {
                if (resolved.User is null)
                {
                    results.Add(new DavScheduleRecipientResult(
                        Mailto(recipient),
                        "3.7;Invalid Calendar User"));
                    continue;
                }
                var resources = await store.GetCalendarResourcesAsync(
                    resolved.User.Id,
                    cancellationToken).ConfigureAwait(false);
                if (!resources.IsComplete)
                {
                    results.Add(new DavScheduleRecipientResult(
                        Mailto(recipient),
                        "5.2;No scheduling support"));
                    continue;
                }
                var intervals = DavFreeBusy.Collect(
                    resources.Resources,
                    request.RangeStart!.Value,
                    request.RangeEnd!.Value);
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "2.0;Success",
                    BuildFreeBusyReply(request, recipient, intervals)));
                continue;
            }

            if (resolved.User is not null)
            {
                var resourceName = SchedulingResourceName(request.Uid, body);
                var stored = await store.StoreSchedulingMessageAsync(
                    resolved.User,
                    resourceName,
                    request.Content,
                    body,
                    cancellationToken).ConfigureAwait(false);
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    stored.Status is DavResourceWriteStatus.Created
                        or DavResourceWriteStatus.Updated
                        or DavResourceWriteStatus.Unchanged
                        ? "2.0;Success"
                        : "5.1;Service unavailable"));
                continue;
            }

            if (resolved.IsLocal)
            {
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "3.7;Invalid Calendar User"));
                continue;
            }
            if (!environment.Smtp.AllowRelay)
            {
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "5.3;No scheduling support"));
                continue;
            }

            try
            {
                var rawMessage = await BuildImipMessageAsync(
                    user.Username,
                    recipient,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (Encoding.Latin1.GetByteCount(rawMessage) > environment.Limits.MaxMessageSizeBytes)
                {
                    results.Add(new DavScheduleRecipientResult(
                        Mailto(recipient),
                        "5.2;No scheduling support"));
                    continue;
                }
                _ = await queue.EnqueueAsync(new MailSubmission(
                    Guid.CreateVersion7(),
                    user.Username,
                    [new MailEnvelopeRecipient(recipient, resolved.IsLocal)],
                    rawMessage,
                    clientIp,
                    environment.Smtp.Hostname,
                    user.Username), cancellationToken).ConfigureAwait(false);
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "2.0;Success"));
            }
            catch (ArgumentException)
            {
                results.Add(new DavScheduleRecipientResult(
                    Mailto(recipient),
                    "5.1;Service unavailable"));
            }
        }

        return new DavScheduleSubmissionResult(200, null, results);
    }

    // iTIP and iCalendar validation shares one ordered failure response path.
#pragma warning disable MA0051
    private static bool TryParseRequest(
        DavContentInfo content,
        string? contentType,
        out DavSchedulingRequest? request,
        out string failure)
    {
#pragma warning restore MA0051
        request = null;
        failure = string.Empty;
        var lines = DavContent.UnfoldLines(content.Text);
        var method = CalendarLevelValue(lines, "METHOD")?.Trim().ToUpperInvariant();
        if (method is null || !SupportedMethods.Contains(method))
        {
            failure = "The iCalendar object has no supported METHOD property.";
            return false;
        }
        var mediaMethod = ContentTypeParameter(contentType, "method")?.ToUpperInvariant();
        if (mediaMethod is not null && !string.Equals(mediaMethod, method, StringComparison.Ordinal))
        {
            failure = "The text/calendar method parameter does not match the METHOD property.";
            return false;
        }
        if (content.Components.Count != 1)
        {
            failure = "A scheduling message must contain one component type.";
            return false;
        }

        if (!TryAddresses(content.Properties, "ORGANIZER", out var organizers)
            || organizers.Count != 1)
        {
            failure = "Every scheduling component must use the same valid ORGANIZER.";
            return false;
        }
        if (!TryAddresses(content.Properties, "ATTENDEE", out var attendees))
        {
            failure = "The scheduling component contains an invalid ATTENDEE.";
            return false;
        }
        var organizer = organizers.Single();
        if (!OrganizerMethods.Contains(method) && attendees.Count != 1)
        {
            failure = "An iTIP response must identify exactly one replying ATTENDEE.";
            return false;
        }
        var isFreeBusy = content.Components.SetEquals(["VFREEBUSY"]);
        DateTimeOffset? rangeStart = null;
        DateTimeOffset? rangeEnd = null;
        if (isFreeBusy)
        {
            if (!string.Equals(method, "REQUEST", StringComparison.Ordinal)
                || !TryPropertyDate(content.Properties, "DTSTART", out var start)
                || !TryPropertyDate(content.Properties, "DTEND", out var end)
                || end <= start
                || end - start > TimeSpan.FromDays(366))
            {
                failure = "A free-busy request requires a valid DTSTART/DTEND range of at most 366 days.";
                return false;
            }
            rangeStart = start;
            rangeEnd = end;
        }

        request = new DavSchedulingRequest(
            method,
            content.Uid,
            organizer,
            attendees,
            FirstValue(content.Properties, "SUMMARY"),
            isFreeBusy,
            rangeStart,
            rangeEnd,
            content);
        return true;
    }

    private static bool IsAuthorizedSender(string username, DavSchedulingRequest request)
    {
        if (OrganizerMethods.Contains(request.Method))
        {
            return string.Equals(
                request.Organizer,
                username,
                StringComparison.OrdinalIgnoreCase);
        }
        return request.Attendees.Contains(username);
    }

    private static bool IsExpectedRecipient(DavSchedulingRequest request, string recipient)
    {
        if (OrganizerMethods.Contains(request.Method))
        {
            return string.Equals(request.Method, "PUBLISH", StringComparison.Ordinal)
                || request.Attendees.Contains(recipient);
        }
        return string.Equals(request.Organizer, recipient, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> BuildImipMessageAsync(
        string sender,
        string recipient,
        DavSchedulingRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new MimeMessage
        {
            Date = DateTimeOffset.UtcNow,
            MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
            Subject = Subject(request),
        };
        message.From.Add(MailboxAddress.Parse(sender));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Headers.Add("Content-Class", "urn:content-classes:calendarmessage");

        var text = new TextPart(TextFormat.Plain)
        {
            Text = PlainText(request),
        };
        var calendar = new TextPart("calendar")
        {
            Text = request.Content.Text,
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
        };
        calendar.ContentType.Charset = "utf-8";
        calendar.ContentType.Parameters["method"] = request.Method;
        calendar.ContentType.Name = "invite.ics";
        message.Body = new MultipartAlternative(text, calendar);

        var format = FormatOptions.Default.Clone();
        format.NewLineFormat = NewLineFormat.Dos;
        var stream = new MemoryStream();
        await using var streamLifetime = stream.ConfigureAwait(false);
        await message.WriteToAsync(format, stream, cancellationToken).ConfigureAwait(false);
        return Encoding.Latin1.GetString(stream.ToArray());
    }

    private static string Subject(DavSchedulingRequest request)
    {
        var description = string.IsNullOrWhiteSpace(request.Summary)
            ? "calendar event"
            : request.Summary;
        return request.Method switch
        {
            "CANCEL" => $"Cancelled: {description}",
            "REPLY" => $"Meeting response: {description}",
            "COUNTER" => $"Meeting counterproposal: {description}",
            "DECLINECOUNTER" => $"Counterproposal declined: {description}",
            _ => $"Invitation: {description}",
        };
    }

    private static string PlainText(DavSchedulingRequest request) => request.Method switch
    {
        "CANCEL" => "This calendar event has been cancelled. Open the attached calendar data for details.",
        "REPLY" => "A participant responded to this calendar event. Open the attached calendar data for details.",
        _ => "You received a calendar scheduling message. Open the attached calendar data for details.",
    };

    private static string BuildFreeBusyReply(
        DavSchedulingRequest request,
        string attendee,
        IReadOnlyList<DavBusyInterval> intervals)
    {
        var builder = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\n")
            .Append("PRODID:-//mk8.email//CalDAV Scheduling//EN\r\n")
            .Append("VERSION:2.0\r\n")
            .Append("METHOD:REPLY\r\n")
            .Append("BEGIN:VFREEBUSY\r\n")
            .Append("UID:").Append(EscapeText(request.Uid)).Append("\r\n")
            .Append("DTSTAMP:").Append(FormatUtc(DateTimeOffset.UtcNow)).Append("\r\n")
            .Append("DTSTART:").Append(FormatUtc(request.RangeStart!.Value)).Append("\r\n")
            .Append("DTEND:").Append(FormatUtc(request.RangeEnd!.Value)).Append("\r\n")
            .Append("ORGANIZER:").Append(Mailto(request.Organizer)).Append("\r\n")
            .Append("ATTENDEE:").Append(Mailto(attendee)).Append("\r\n");
        foreach (var interval in intervals)
        {
            builder.Append("FREEBUSY;FBTYPE=BUSY:")
                .Append(FormatUtc(interval.Start))
                .Append('/')
                .Append(FormatUtc(interval.End))
                .Append("\r\n");
        }
        return builder
            .Append("REQUEST-STATUS:2.0;Success\r\n")
            .Append("END:VFREEBUSY\r\n")
            .Append("END:VCALENDAR\r\n")
            .ToString();
    }

    private static string SchedulingResourceName(string uid, byte[] body)
    {
        using var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hashAlgorithm.AppendData(Encoding.UTF8.GetBytes(uid));
        hashAlgorithm.AppendData([0]);
        hashAlgorithm.AppendData(body);
        var hash = hashAlgorithm.GetHashAndReset();
        return Convert.ToHexStringLower(hash) + ".ics";
    }

    private static bool TryParseHeaderAddresses(
        IEnumerable<string> headers,
        out List<string> addresses)
    {
        addresses = [];
        foreach (var value in headers.SelectMany(value =>
                     value.Split(',', StringSplitOptions.RemoveEmptyEntries)))
        {
            var normalized = NormalizeAddress(value);
            if (normalized is null)
                return false;
            if (!addresses.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                addresses.Add(normalized);
        }
        return true;
    }

    private static bool TryAddresses(
        IReadOnlyDictionary<string, IReadOnlyList<string>> properties,
        string name,
        out HashSet<string> addresses)
    {
        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        addresses = parsed;
        if (!properties.TryGetValue(name, out var values))
            return true;
        foreach (var value in values)
        {
            var normalized = NormalizeAddress(value);
            if (normalized is null)
                return false;
            parsed.Add(normalized);
        }
        return true;
    }

    private static string? NormalizeAddress(string value)
    {
        var candidate = value.Trim().Trim('<', '>');
        if (candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                candidate = Uri.UnescapeDataString(candidate[7..]);
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        if (!MailboxAddress.TryParse(candidate, out var mailbox))
            return null;
        // Existing calendar recipient keys are lower-case mailbox addresses.
#pragma warning disable CA1308
        return mailbox.Address.ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string? CalendarLevelValue(IReadOnlyList<string> lines, string name)
    {
        var depth = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
                continue;
            }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
                continue;
            }
            if (depth == 1
                && DavContent.TryParseProperty(line, out var propertyName, out var value)
                && propertyName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static string? ContentTypeParameter(string? contentType, string name)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;
        foreach (var segment in contentType.Split(';').Skip(1))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0
                || !segment[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return segment[(separator + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static string? FirstValue(
        IReadOnlyDictionary<string, IReadOnlyList<string>> properties,
        string name) => properties.TryGetValue(name, out var values) && values.Count > 0
        ? values[0]
        : null;

    private static bool TryPropertyDate(
        IReadOnlyDictionary<string, IReadOnlyList<string>> properties,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        return properties.TryGetValue(name, out var values)
            && values.Count > 0
            && DavFreeBusy.TryParseDate(values[0], null, out value, out _);
    }

    private static string Mailto(string address) => $"mailto:{address}";

    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static DavScheduleSubmissionResult Failure(int statusCode, string error) =>
        new(statusCode, error, []);
}

// Free/busy parsing and recurrence expansion form one protocol parser below.
#pragma warning disable MA0048
internal static class DavFreeBusy
{
#pragma warning restore MA0048
    private const int MaximumOccurrences = 10_000;
    private const int MaximumScannedDays = 200_000;

    public static IReadOnlyList<DavBusyInterval> Collect(
        IEnumerable<DavResource> resources,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var intervals = new List<DavBusyInterval>();
        foreach (var resource in resources)
        {
            if (!resource.ContentType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase))
                continue;
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(resource.Content);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }
            foreach (var group in ParseEvents(text).GroupBy(item => item.Uid, StringComparer.Ordinal))
                AddEventGroup(intervals, group.ToList(), rangeStart, rangeEnd);
        }
        return Merge(intervals, rangeStart, rangeEnd);
    }

    private static void AddEventGroup(
        List<DavBusyInterval> output,
        IReadOnlyList<DavEvent> events,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var exceptions = events
            .Where(item => item.RecurrenceId is not null)
            .GroupBy(item => item.RecurrenceId!.Value)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var master in events.Where(item => item.RecurrenceId is null))
        {
            if (master.IsTransparent || master.IsCancelled || master.End <= master.Start)
                continue;
            var duration = master.End - master.Start;
            foreach (var occurrence in ExpandStarts(master, rangeStart, rangeEnd))
            {
                if (master.Exclusions.Contains(occurrence))
                    continue;
                if (exceptions.TryGetValue(occurrence, out var exception))
                {
                    if (!exception.IsTransparent
                        && !exception.IsCancelled
                        && exception.End > exception.Start
                        && Intersects(exception.Start, exception.End, rangeStart, rangeEnd))
                    {
                        output.Add(new DavBusyInterval(exception.Start, exception.End));
                    }
                    continue;
                }
                var end = occurrence + duration;
                if (Intersects(occurrence, end, rangeStart, rangeEnd))
                    output.Add(new DavBusyInterval(occurrence, end));
            }
        }
    }

    private static IEnumerable<DateTimeOffset> ExpandStarts(
        DavEvent item,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        if (item.RecurrenceRule is null)
        {
            yield return item.Start;
            foreach (var extra in item.Additions)
                yield return extra;
            yield break;
        }

        var rule = item.RecurrenceRule;
        var emitted = 0;
        var scanned = 0;
        var finalLocalDate = item.RecurrenceZone is null
            ? rangeEnd.UtcDateTime.Date
            : TimeZoneInfo.ConvertTime(rangeEnd, item.RecurrenceZone).Date;
        var firstLocalDate = item.LocalStart.Date;
        if (rule.Count is null)
        {
            var localRangeStart = item.RecurrenceZone is null
                ? rangeStart.UtcDateTime
                : TimeZoneInfo.ConvertTime(rangeStart, item.RecurrenceZone).DateTime;
            var lookbackDays = Math.Max(
                1,
                (int)Math.Ceiling((item.End - item.Start).TotalDays) + 1);
            var boundedStart = localRangeStart.Date.AddDays(-lookbackDays);
            if (boundedStart > firstLocalDate)
                firstLocalDate = boundedStart;
        }
        for (var day = firstLocalDate; day <= finalLocalDate; day = day.AddDays(1))
        {
            if (++scanned > MaximumScannedDays || emitted >= MaximumOccurrences)
                break;
            if (!MatchesRecurrenceDay(item.LocalStart, day, rule))
                continue;
            var localCandidate = new DateTime(
                day.Year,
                day.Month,
                day.Day,
                item.LocalStart.Hour,
                item.LocalStart.Minute,
                item.LocalStart.Second,
                DateTimeKind.Unspecified);
            if (item.RecurrenceZone?.IsInvalidTime(localCandidate) == true)
                continue;
            var candidate = item.RecurrenceZone is null
                ? new DateTimeOffset(DateTime.SpecifyKind(localCandidate, DateTimeKind.Utc))
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                    localCandidate,
                    item.RecurrenceZone));
            if (candidate < item.Start || rule.Until is not null && candidate > rule.Until)
                continue;
            emitted++;
            if (rule.Count is not null && emitted > rule.Count)
                break;
            yield return candidate;
        }
        foreach (var extra in item.Additions)
            yield return extra;
    }

    private static bool MatchesRecurrenceDay(
        DateTime start,
        DateTime day,
        DavRecurrenceRule rule)
    {
        var days = (day - start.Date).Days;
        if (days < 0 || rule.ByMonths.Count > 0 && !rule.ByMonths.Contains(day.Month))
            return false;
        return rule.Frequency switch
        {
            "DAILY" => days % rule.Interval == 0
                && MatchesByMonthDay(day, rule.ByMonthDays)
                && MatchesByDay(day, rule.ByDays),
            "WEEKLY" => WeeksBetween(
                    StartOfWeek(start.Date, rule.WeekStart),
                    StartOfWeek(day, rule.WeekStart)) % rule.Interval == 0
                && MatchesByMonthDay(day, rule.ByMonthDays)
                && (rule.ByDays.Count == 0
                    ? day.DayOfWeek == start.DayOfWeek
                    : MatchesByDay(day, rule.ByDays)),
            "MONTHLY" => MonthsBetween(start.Date, day) % rule.Interval == 0
                && MatchesMonthDay(start, day, rule),
            "YEARLY" => day.Year - start.Year >= 0
                && (day.Year - start.Year) % rule.Interval == 0
                && (rule.ByMonths.Count == 0
                    ? day.Month == start.Month
                    : rule.ByMonths.Contains(day.Month))
                && MatchesMonthDay(start, day, rule),
            _ => day == start.Date,
        };
    }

    private static bool MatchesMonthDay(
        DateTime start,
        DateTime day,
        DavRecurrenceRule rule)
    {
        if (!MatchesMonthDayBase(start, day, rule))
            return false;
        if (rule.BySetPositions.Count == 0)
            return true;

        var matchingDays = Enumerable.Range(1, DateTime.DaysInMonth(day.Year, day.Month))
            .Select(dayOfMonth => new DateTime(day.Year, day.Month, dayOfMonth))
            .Where(candidate => MatchesMonthDayBase(start, candidate, rule))
            .ToArray();
        var index = Array.FindIndex(matchingDays, candidate => candidate.Day == day.Day);
        if (index < 0)
            return false;
        var positivePosition = index + 1;
        var negativePosition = index - matchingDays.Length;
        return rule.BySetPositions.Contains(positivePosition)
            || rule.BySetPositions.Contains(negativePosition);
    }

    private static bool MatchesMonthDayBase(
        DateTime start,
        DateTime day,
        DavRecurrenceRule rule)
    {
        if (!MatchesByMonthDay(day, rule.ByMonthDays))
            return false;
        if (rule.ByDays.Count > 0 && !MatchesByDay(day, rule.ByDays))
            return false;
        return rule.ByMonthDays.Count > 0
            || rule.ByDays.Count > 0
            || day.Day == start.Day;
    }

    private static bool MatchesByMonthDay(DateTime day, IReadOnlySet<int> values) =>
        values.Count == 0
        || values.Any(value => value > 0
            ? day.Day == value
            : day.Day == DateTime.DaysInMonth(day.Year, day.Month) + value + 1);

    private static bool MatchesByDay(DateTime day, IReadOnlyList<DavByDay> rules)
    {
        if (rules.Count == 0)
            return true;
        foreach (var rule in rules.Where(rule => rule.Day == day.DayOfWeek))
        {
            if (rule.Ordinal is null)
                return true;
            var ordinal = rule.Ordinal.Value > 0
                ? (day.Day - 1) / 7 + 1
                : -((DateTime.DaysInMonth(day.Year, day.Month) - day.Day) / 7 + 1);
            if (ordinal == rule.Ordinal)
                return true;
        }
        return false;
    }

    private static DateTime StartOfWeek(DateTime day, DayOfWeek weekStart)
    {
        var difference = (7 + (int)day.DayOfWeek - (int)weekStart) % 7;
        return day.AddDays(-difference);
    }

    private static int WeeksBetween(DateTime start, DateTime value) =>
        (value - start).Days / 7;

    private static int MonthsBetween(DateTime start, DateTime value) =>
        (value.Year - start.Year) * 12 + value.Month - start.Month;

    private static List<DavBusyInterval> Merge(
        IEnumerable<DavBusyInterval> values,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var ordered = values
            .Select(value => new DavBusyInterval(
                value.Start < rangeStart ? rangeStart : value.Start,
                value.End > rangeEnd ? rangeEnd : value.End))
            .Where(value => value.End > value.Start)
            .OrderBy(value => value.Start)
            .ThenBy(value => value.End)
            .ToList();
        if (ordered.Count == 0)
            return [];

        var merged = new List<DavBusyInterval> { ordered[0] };
        foreach (var value in ordered.Skip(1))
        {
            var previous = merged[^1];
            if (value.Start <= previous.End)
            {
                merged[^1] = new DavBusyInterval(
                    previous.Start,
                    value.End > previous.End ? value.End : previous.End);
            }
            else
            {
                merged.Add(value);
            }
        }
        return merged;
    }

    private static bool Intersects(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd) => start < rangeEnd && end > rangeStart;

    private static List<DavEvent> ParseEvents(string text)
    {
        var result = new List<DavEvent>();
        List<DavCalendarProperty>? properties = null;
        var nested = 0;
        foreach (var line in DavContent.UnfoldLines(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                properties = [];
                nested = 0;
                continue;
            }
            if (properties is null)
                continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                nested++;
                continue;
            }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase) && nested == 0)
                {
                    if (TryCreateEvent(properties, out var calendarEvent))
                        result.Add(calendarEvent!);
                    properties = null;
                }
                else if (nested > 0)
                {
                    nested--;
                }
                continue;
            }
            if (nested == 0 && TryParseProperty(line, out var property))
                properties.Add(property!);
        }
        return result;
    }

    // Event recurrence input must be validated before any interval projection.
#pragma warning disable MA0051
    private static bool TryCreateEvent(
        IReadOnlyList<DavCalendarProperty> properties,
        out DavEvent? calendarEvent)
    {
#pragma warning restore MA0051
        calendarEvent = null;
        var uid = Value(properties, "UID");
        var startProperty = Property(properties, "DTSTART");
        if (string.IsNullOrWhiteSpace(uid)
            || startProperty is null
            || !TryParseDate(startProperty.Value, startProperty.Parameters, out var start, out var dateOnly))
        {
            return false;
        }

        DateTimeOffset end;
        try
        {
            var endProperty = Property(properties, "DTEND");
            if (endProperty is not null
                && TryParseDate(endProperty.Value, endProperty.Parameters, out var parsedEnd, out _))
            {
                end = parsedEnd;
            }
            else if (TryParseDuration(Value(properties, "DURATION"), out var duration))
            {
                end = start + duration;
            }
            else
            {
                end = dateOnly ? start.AddDays(1) : start;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        DateTimeOffset? recurrenceId = null;
        var recurrenceProperty = Property(properties, "RECURRENCE-ID");
        if (recurrenceProperty is not null
            && TryParseDate(
                recurrenceProperty.Value,
                recurrenceProperty.Parameters,
                out var parsedRecurrence,
                out _))
        {
            recurrenceId = parsedRecurrence;
        }
        var exclusions = DateList(properties, "EXDATE");
        var additions = DateList(properties, "RDATE");
        var recurrenceZone = ResolveTimeZone(startProperty.Parameters);
        var localStart = recurrenceZone is not null
            && TryParseLocalDate(startProperty.Value, out var parsedLocalStart)
            ? parsedLocalStart
            : start.UtcDateTime;
        calendarEvent = new DavEvent(
            uid,
            start,
            end,
            localStart,
            recurrenceZone,
            recurrenceId,
            ParseRecurrence(Value(properties, "RRULE")),
            exclusions,
            additions,
            string.Equals(Value(properties, "TRANSP"), "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
            string.Equals(Value(properties, "STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static HashSet<DateTimeOffset> DateList(
        IReadOnlyList<DavCalendarProperty> properties,
        string name)
    {
        var result = new HashSet<DateTimeOffset>();
        foreach (var property in properties.Where(item =>
                     item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseDate(value, property.Parameters, out var parsed, out _))
                    result.Add(parsed);
            }
        }
        return result;
    }

    // Keep RRULE field validation together so invalid combinations fail as a unit.
#pragma warning disable MA0051
    private static DavRecurrenceRule? ParseRecurrence(string? value)
    {
#pragma warning restore MA0051
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            if (pair.Length != 2 || !parts.TryAdd(pair[0].ToUpperInvariant(), pair[1]))
                return null;
        }
        if (!parts.TryGetValue("FREQ", out var frequency)
            || frequency.ToUpperInvariant() is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            return null;
        }
        var interval = parts.TryGetValue("INTERVAL", out var intervalText)
            && int.TryParse(intervalText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInterval)
            && parsedInterval > 0
            ? parsedInterval
            : 1;
        int? count = parts.TryGetValue("COUNT", out var countText)
            && int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount)
            && parsedCount > 0
            ? Math.Min(parsedCount, MaximumOccurrences)
            : null;
        DateTimeOffset? until = parts.TryGetValue("UNTIL", out var untilText)
            && TryParseDate(untilText, null, out var parsedUntil, out _)
            ? parsedUntil
            : null;
        var byDays = parts.TryGetValue("BYDAY", out var byDayText)
            ? byDayText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseByDay)
                .Where(day => day is not null)
                .Cast<DavByDay>()
                .Distinct()
                .ToArray()
            : [];
        var byMonthDays = parts.TryGetValue("BYMONTHDAY", out var byMonthDayText)
            ? byMonthDayText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(text => int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var day) ? day : 0)
                .Where(day => day is >= 1 and <= 31 or >= -31 and <= -1)
                .ToHashSet()
            : [];
        var byMonths = parts.TryGetValue("BYMONTH", out var byMonthText)
            ? byMonthText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(text => int.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var month) ? month : 0)
                .Where(month => month is >= 1 and <= 12)
                .ToHashSet()
            : [];
        var bySetPositions = parts.TryGetValue("BYSETPOS", out var bySetPositionText)
            ? bySetPositionText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(text => int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var position) ? position : 0)
                .Where(position => position is >= 1 and <= 366 or >= -366 and <= -1)
                .ToHashSet()
            : [];
        var weekStart = parts.TryGetValue("WKST", out var weekStartText)
            ? ParseDayOfWeek(weekStartText) ?? DayOfWeek.Monday
            : DayOfWeek.Monday;
        return new DavRecurrenceRule(
            frequency.ToUpperInvariant(),
            interval,
            count,
            until,
            byDays,
            byMonthDays,
            byMonths,
            bySetPositions,
            weekStart);
    }

    private static DavByDay? ParseByDay(string value)
    {
        if (value.Length < 2)
            return null;
        var day = ParseDayOfWeek(value[^2..]);
        if (day is null)
            return null;
        int? ordinal = null;
        if (value.Length > 2)
        {
            if (!int.TryParse(
                    value[..^2],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var parsedOrdinal)
                || parsedOrdinal is 0 or < -53 or > 53)
            {
                return null;
            }
            ordinal = parsedOrdinal;
        }
        return new DavByDay(day.Value, ordinal);
    }

    private static DayOfWeek? ParseDayOfWeek(string value)
    {
        var token = value.Length >= 2 ? value[^2..].ToUpperInvariant() : value;
        return token switch
        {
            "SU" => DayOfWeek.Sunday,
            "MO" => DayOfWeek.Monday,
            "TU" => DayOfWeek.Tuesday,
            "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday,
            "SA" => DayOfWeek.Saturday,
            _ => null,
        };
    }

    private static bool TryParseProperty(string line, out DavCalendarProperty? property)
    {
        property = null;
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return false;
        var header = line[..separator].Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in header.Skip(1))
        {
            var equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
                parameters[item[..equals]] = item[(equals + 1)..].Trim('"');
        }
        property = new DavCalendarProperty(
            header[0].ToUpperInvariant(),
            parameters,
            line[(separator + 1)..]);
        return true;
    }

    // Date and timezone alternatives share a single iCalendar value parser.
#pragma warning disable MA0051
    internal static bool TryParseDate(
        string value,
        IReadOnlyDictionary<string, string>? parameters,
        out DateTimeOffset result,
        out bool dateOnly)
    {
#pragma warning restore MA0051
        result = default;
        dateOnly = parameters?.TryGetValue("VALUE", out var valueType) == true
            && valueType.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || value.Length == 8;
        if (dateOnly
            && DateTime.TryParseExact(
                value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            result = new DateTimeOffset(
                DateTime.SpecifyKind(date, DateTimeKind.Utc));
            return true;
        }
        if (value.EndsWith('Z')
            && DateTimeOffset.TryParseExact(
                value,
                ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result))
        {
            return true;
        }
        if (value.Length >= 5
            && (value[^5] == '+' || value[^5] == '-'))
        {
            var normalized = value.Insert(value.Length - 2, ":");
            if (DateTimeOffset.TryParseExact(
                    normalized,
                    ["yyyyMMdd'T'HHmmsszzz", "yyyyMMdd'T'HHmmzzz"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out result))
            {
                result = result.ToUniversalTime();
                return true;
            }
        }
        if (!DateTime.TryParseExact(
                value,
                ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return false;
        }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (parameters?.TryGetValue("TZID", out var timeZoneId) == true)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                result = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                return false;
            }
            catch (InvalidTimeZoneException)
            {
                return false;
            }
        }
        result = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
        return true;
    }

    private static TimeZoneInfo? ResolveTimeZone(
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("TZID", out var timeZoneId))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool TryParseLocalDate(string value, out DateTime result)
    {
        if (DateTime.TryParseExact(
                value,
                ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out result))
        {
            result = DateTime.SpecifyKind(result, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    private static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            duration = XmlConvert.ToTimeSpan(value);
            return duration >= TimeSpan.Zero;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static DavCalendarProperty? Property(
        IReadOnlyList<DavCalendarProperty> properties,
        string name) => properties.LastOrDefault(item =>
        item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(
        IReadOnlyList<DavCalendarProperty> properties,
        string name) => Property(properties, name)?.Value;

    private sealed record DavCalendarProperty(
        string Name,
        IReadOnlyDictionary<string, string> Parameters,
        string Value);

    private sealed record DavEvent(
        string Uid,
        DateTimeOffset Start,
        DateTimeOffset End,
        DateTime LocalStart,
        TimeZoneInfo? RecurrenceZone,
        DateTimeOffset? RecurrenceId,
        DavRecurrenceRule? RecurrenceRule,
        IReadOnlySet<DateTimeOffset> Exclusions,
        IReadOnlySet<DateTimeOffset> Additions,
        bool IsTransparent,
        bool IsCancelled);

    private sealed record DavRecurrenceRule(
        string Frequency,
        int Interval,
        int? Count,
        DateTimeOffset? Until,
        IReadOnlyList<DavByDay> ByDays,
        IReadOnlySet<int> ByMonthDays,
        IReadOnlySet<int> ByMonths,
        IReadOnlySet<int> BySetPositions,
        DayOfWeek WeekStart);

    private sealed record DavByDay(DayOfWeek Day, int? Ordinal);
}
