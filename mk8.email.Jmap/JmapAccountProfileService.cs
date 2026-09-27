using mk8.email.Application.Interfaces;
using mk8.email.Configuration;
using mk8.email.Contracts.Messaging;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Jmap;

public sealed class JmapAccountProfileService(
    JmapAccountService accounts,
    JmapContactStore contacts,
    EnvironmentConfig environment)
{
    public async Task<JmapApplicationProfile> GetProfileAsync(
        AuthenticatedMailUser user,
        CancellationToken cancellationToken = default)
    {
        await contacts.EnsureDefaultAddressBookAsync(user, cancellationToken).ConfigureAwait(false);
        var accessible = await accounts.GetAccountsAsync(user, cancellationToken).ConfigureAwait(false);
        var profiles = new JmapAccountProfile[accessible.Count];
        for (var index = 0; index < accessible.Count; index++)
        {
            var account = accessible[index];
            profiles[index] = new JmapAccountProfile(
                JmapId.Account(account.InboxId), account.Address, true, false, index == 0);
        }
        var limits = environment.Jmap;
        return new JmapApplicationProfile(user.Username,
            new JmapServiceLimits(limits.MaxUploadSizeBytes, limits.MaxConcurrentUploads,
                limits.MaxRequestSizeBytes, limits.MaxConcurrentRequests, limits.MaxCallsInRequest,
                limits.MaxObjectsInGet, limits.MaxObjectsInSet,
                FolderDB.MaximumHierarchyDepth, FolderDB.MaximumLeafNameOctets,
                environment.Limits.MaxMessageSizeBytes, JmapCollation.SupportedIdentifiers.ToArray(),
                ["receivedAt", "size", "from", "to", "subject", "sentAt", "hasKeyword",
                 "allInThreadHaveKeyword", "someInThreadHaveKeyword"]), profiles);
    }
}
