using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using mk8.email.Application.Interfaces;
using mk8.email.Infrastructure.Data;
using mk8.email.Infrastructure.Models;
using mk8.email.Jmap;

namespace mk8.email.Messaging.Tests;

[TestClass]
[TestCategory("PostgreSQL")]
public sealed class JmapAccountAsyncPostgresTests
{
    [TestMethod]
    [Timeout(90_000)]
    [DataRow("accounts")]
    [DataRow("account")]
    [DataRow("contact")]
    [DataRow("contactError")]
    public async Task AccountQueriesDoNotRequireTheCallersSynchronizationContext(string operation)
    {
        await using var server = await PostgresTestDatabase.TryCreateAsync();
        if (server is null)
        {
            Assert.Inconclusive("Set MK8_EMAIL_TEST_POSTGRES to a PostgreSQL admin connection string.");
            return;
        }
        var gate = new GatedReaderInterceptor();
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseNpgsql(server.ConnectionString).AddInterceptors(gate).Options;
        await using var database = new EmailDbContext(options);
        var inbox = await SeedAccountAsync(database);
        var user = new AuthenticatedMailUser(inbox.OwnerId, inbox.Owner.Username);
        var application = new JmapAccountService(database);
        var context = new RecordingSynchronizationContext();
        gate.Armed = true;
        var pending = StartUnderContext(context, () => operation switch
        {
            "accounts" => application.GetAccountsAsync(user),
            "account" => application.GetAccountAsync(user, JmapId.Account(inbox.Id)),
            "contact" => application.GetContactAccountAsync(user, JmapId.Account(inbox.Id)),
            "contactError" => application.GetContactAccountErrorAsync(user, JmapId.Account(inbox.Id)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
        try
        {
            Assert.IsFalse(pending.IsCompleted, "The interceptor must force a genuinely asynchronous query.");
        }
        finally
        {
            gate.Release();
        }
        await pending;
        Assert.AreEqual(1, gate.GatedQueries);
        Assert.AreEqual(0, context.PostCount);
        await AssertResultAsync(pending, inbox.Id);
    }

    private static Task StartUnderContext(SynchronizationContext context, Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task AssertResultAsync(Task completed, Guid inboxId)
    {
        if (completed is Task<IReadOnlyList<JmapAccount>> accounts)
        {
            var result = await accounts.ConfigureAwait(false);
            Assert.HasCount(1, result);
            Assert.AreEqual(inboxId, result[0].InboxId);
        }
        else if (completed is Task<JmapAccount?> account)
        {
            var result = await account.ConfigureAwait(false);
            Assert.IsNotNull(result);
            Assert.AreEqual(inboxId, result.InboxId);
        }
        else if (completed is Task<string> error)
        {
            Assert.AreEqual("accountNotSupportedByMethod", await error.ConfigureAwait(false));
        }
        else
        {
            Assert.Fail("An unexpected account query result was returned.");
        }
    }

    private static async Task<InboxDB> SeedAccountAsync(EmailDbContext database)
    {
        await database.Database.EnsureCreatedAsync();
        var company = new CompanyDB { Id = Guid.CreateVersion7(), Name = "JMAP async boundary" };
        var inbox = new InboxDB
        {
            Id = Guid.CreateVersion7(),
            Name = "owner",
            Owner = new UserDB
            {
                Id = Guid.CreateVersion7(),
                Username = "owner@example.test",
                PasswordHash = "unused",
                Company = company,
            },
            Address = new AddressDB
            {
                Id = Guid.CreateVersion7(),
                Domain = "example.test",
                IsActive = true,
                Company = company,
            },
        };
        database.Inboxes.Add(inbox);
        await database.SaveChangesAsync();
        return inbox;
    }

    private sealed class GatedReaderInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gatedQueries;

        public bool Armed { get; set; }
        public int GatedQueries => Volatile.Read(ref _gatedQueries);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Interlocked.Increment(ref _gatedQueries);
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
