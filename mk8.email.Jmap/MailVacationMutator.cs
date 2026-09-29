using System.Text;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Jmap;

internal sealed class MailVacationMutator(
    JmapAccountService accounts,
    JmapVacationResponseService vacations,
    VacationResponseContentService content,
    JmapStateService states,
    EnvironmentConfig environment) : IMailVacationMutator
{
    public async Task<MailVacationSetResult> SetAsync(
        MailVacationSetCommand command,
        AuthenticatedMailUser user,
        CancellationToken cancellationToken)
    {
        if (command.Updates is null)
            throw new InvalidOperationException("The vacation mutation omitted its updates.");
        var account = await accounts.GetAccountByInboxIdAsync(user, command.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return new(MailVacationSetStatus.AccountNotFound, null, null, []);
        var response = await vacations.GetOrCreateAsync(account.InboxId, cancellationToken).ConfigureAwait(false);
        var oldState = await states.GetStateAsync(account.InboxId,
            JmapConstants.VacationResponseDataType, cancellationToken).ConfigureAwait(false);
        if (command.IfInState is not null && !string.Equals(command.IfInState, oldState, StringComparison.Ordinal))
            return new(MailVacationSetStatus.StateMismatch, null, null, []);

        var outcomes = new List<MailVacationUpdateResult>(command.Updates.Count);
        foreach (var update in command.Updates)
        {
            if (update is null)
                throw new InvalidOperationException("The vacation mutation contains a null update.");
            var currentBodies = await content.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            var subject = update.SetSubject ? update.Subject : response.Subject;
            var textBody = update.SetTextBody ? update.TextBody : currentBodies.TextBody;
            var htmlBody = update.SetHtmlBody ? update.HtmlBody : currentBodies.HtmlBody;
            var invalid = Validate(subject, textBody, htmlBody, environment.Limits.MaxMessageSizeBytes);
            if (invalid.Count > 0)
            {
                outcomes.Add(new(false, invalid));
                continue;
            }
            if (update.SetIsEnabled) response.IsEnabled = update.IsEnabled;
            if (update.SetFromDate) response.FromDate = update.FromDate;
            if (update.SetToDate) response.ToDate = update.ToDate;
            response.Subject = subject;
            await vacations.SetBodiesAsync(response, textBody, htmlBody, cancellationToken).ConfigureAwait(false);
            response.UpdatedAt = DateTime.UtcNow;
            await vacations.SaveAsync(cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(true, []));
        }
        var newState = await states.GetStateAsync(account.InboxId,
            JmapConstants.VacationResponseDataType, cancellationToken).ConfigureAwait(false);
        return new(MailVacationSetStatus.Ok, oldState, newState, outcomes.ToArray());
    }

    private static IReadOnlyList<string> Validate(string? subject, string? textBody,
        string? htmlBody, int maximumBodyBytes)
    {
        var invalid = new List<string>();
        if (subject is { Length: > 998 }) invalid.Add("subject");
        var textBytes = Encoding.UTF8.GetByteCount(textBody ?? string.Empty);
        if (textBytes > maximumBodyBytes) invalid.Add("textBody");
        var htmlBytes = Encoding.UTF8.GetByteCount(htmlBody ?? string.Empty);
        if (htmlBytes > maximumBodyBytes) invalid.Add("htmlBody");
        if (textBytes + (long)htmlBytes > Math.Max(0, maximumBodyBytes - 16_384))
        {
            invalid.Add("textBody");
            invalid.Add("htmlBody");
        }
        return invalid.Distinct(StringComparer.Ordinal).ToArray();
    }
}
