using System.Text.Json;
using mk8.email.Application.Interfaces;
using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Messaging.Tests;

// The fixture uses real durable transport/Worker execution with controlled domain results.
// Actual credential/company/MFA policy is covered by the separate Application suite.
internal sealed class GatewayAutodiscoverFixtureDispatcher(IServiceProvider services) : IApplicationRequestDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid AccountId = new("138ef53b-3efa-4a59-8602-e215c0df1e35");
    private static readonly Guid EmptyAccountId = new("8c7c67bd-1792-4ea0-87ab-304579ab87cf");

    public Task<ApplicationResponse> DispatchAsync(ApplicationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.Operation, ApplicationOperations.ImapAuthenticatePassword, StringComparison.Ordinal))
        {
            var authentication = JsonSerializer.Deserialize<ImapPasswordAuthentication>(request.Payload, JsonOptions)!;
            var identity = string.Equals(authentication.Password, "test-protocol-secret", StringComparison.Ordinal)
                ? Identity(authentication.Username) : new ImapIdentityResult(null, null);
            return Task.FromResult(Success(request.Id, identity));
        }
        if (string.Equals(request.Operation, ApplicationOperations.ImapListMailboxes, StringComparison.Ordinal))
        {
            var lookup = JsonSerializer.Deserialize<ImapMailboxListRequest>(request.Payload, JsonOptions)!;
            Assert.IsFalse(lookup.SubscribedOnly);
            Assert.IsTrue(lookup.UserId == AccountId || lookup.UserId == EmptyAccountId);
            return Task.FromResult(Success(request.Id, new ImapMailboxListResult(lookup.UserId == AccountId
                ? [new ImapMailboxInfo("owner", "example.test", "Inbox", true, true)] : [])));
        }
        return new ApplicationRequestDispatcher(services).DispatchAsync(request, cancellationToken);
    }

    private static ImapIdentityResult Identity(string username) => username switch
    {
        "owner@example.test" => new ImapIdentityResult(AccountId, username),
        "empty@example.test" => new ImapIdentityResult(EmptyAccountId, username),
        _ => new ImapIdentityResult(null, null),
    };

    private static ApplicationResponse Success<T>(Guid id, T value) => new(id, "application/json",
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), new Dictionary<string, string>(StringComparer.Ordinal));
}
