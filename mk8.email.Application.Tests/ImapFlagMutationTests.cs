using mk8.email.Application.Services;
using mk8.email.Contracts.Imap;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest DiscoverInternals instantiates this test class by reflection; focused discovery is verified by executed test counts.")]
internal sealed class ImapFlagMutationTests
{
    [TestMethod]
    [DataRow(ImapFlagMutationMode.Add)]
    [DataRow(ImapFlagMutationMode.Replace)]
    [DataRow(ImapFlagMutationMode.Remove)]
    public void SuccessfulMutationClearsFailureAndPreservesModeSemantics(ImapFlagMutationMode mode)
    {
        var email = new EmailDB
        {
            IsRead = true,
            IsDeleted = true,
            Keywords = ["retained"],
        };

        Assert.IsTrue(ImapFlagMutation.TryApply(email, mode, ["\\Seen", "added"], out var failure));
        Assert.AreEqual(string.Empty, failure, StringComparer.Ordinal);
        Assert.AreEqual(mode != ImapFlagMutationMode.Remove, email.IsRead);
        Assert.AreEqual(mode != ImapFlagMutationMode.Replace, email.IsDeleted);
        var expectedKeywords = mode switch
        {
            ImapFlagMutationMode.Add => new[] { "added", "retained" },
            ImapFlagMutationMode.Replace => ["added"],
            ImapFlagMutationMode.Remove => ["retained"],
            _ => throw new AssertFailedException("Unexpected mutation mode."),
        };
        CollectionAssert.AreEqual(expectedKeywords, email.Keywords);
    }

    [TestMethod]
    public void KeywordLimitFailureDoesNotPartiallyMutateTheMessage()
    {
        var originalKeywords = Enumerable.Range(0, ImapFlagMutation.MaximumKeywordsPerMessage)
            .Select(index => $"tag{index}").ToArray();
        var email = new EmailDB { IsRead = false, IsFlagged = true, Keywords = originalKeywords };

        Assert.IsFalse(ImapFlagMutation.TryApply(
            email, ImapFlagMutationMode.Add, ["\\Seen", "overflow"], out var failure));
        Assert.AreEqual("Too many keywords", failure, StringComparer.Ordinal);
        Assert.IsFalse(email.IsRead);
        Assert.IsTrue(email.IsFlagged);
        Assert.AreSame(originalKeywords, email.Keywords);
    }

    [TestMethod]
    public void MaximumKeywordCountStillAcceptsSystemFlagChanges()
    {
        var email = new EmailDB
        {
            Keywords = Enumerable.Range(0, ImapFlagMutation.MaximumKeywordsPerMessage)
                .Select(index => $"tag{index}").ToArray(),
        };

        Assert.IsTrue(ImapFlagMutation.TryApply(
            email, ImapFlagMutationMode.Add, ["\\Seen"], out var failure));
        Assert.AreEqual(string.Empty, failure, StringComparer.Ordinal);
        Assert.IsTrue(email.IsRead);
        Assert.HasCount(ImapFlagMutation.MaximumKeywordsPerMessage, email.Keywords);
    }

    [TestMethod]
    public void InvalidModeFailsBeforeChangingFlags()
    {
        var email = new EmailDB { IsRead = false };

        Assert.IsFalse(ImapFlagMutation.TryApply(
            email, (ImapFlagMutationMode)999, ["\\Seen"], out var failure));
        Assert.AreEqual("Invalid STORE action", failure, StringComparer.Ordinal);
        Assert.IsFalse(email.IsRead);
    }

    [TestMethod]
    public void InvalidRecentFlagDoesNotApplyEarlierFlags()
    {
        var email = new EmailDB { IsRead = false };

        Assert.IsFalse(ImapFlagMutation.TryApply(
            email, ImapFlagMutationMode.Add, ["\\Seen", "\\Recent"], out var failure));
        StringAssert.Contains(failure, "cannot be changed", StringComparison.Ordinal);
        Assert.IsFalse(email.IsRead);
    }
}
