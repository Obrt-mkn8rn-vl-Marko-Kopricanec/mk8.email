using mk8.email.Application.Protocol;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class Rfc5256ThreadingTests
{
    [TestMethod]
    public void MessageIdNormalizationUnquotesButRemainsCaseSensitive()
    {
        var quoted = Rfc5256Threading.ParseMessageIds(
            """<"01KF8JCEOCBS0045PS"@xxx.yyy.com>""");
        var unquoted = Rfc5256Threading.ParseMessageIds(
            "<01KF8JCEOCBS0045PS@xxx.yyy.com>");
        var escaped = Rfc5256Threading.ParseMessageIds("""<"a\.b"@example.net>""");
        var dotted = Rfc5256Threading.ParseMessageIds("<a.b@example.net>");
        var quotedAtSign = Rfc5256Threading.ParseMessageIds(
            """<"a@b"@example.net>""");
        var differentCase = Rfc5256Threading.ParseMessageIds(
            "<01kf8jceocbs0045ps@xxx.yyy.com>");

        Assert.HasCount(1, quoted);
        Assert.AreEqual(quoted[0], unquoted[0]);
        Assert.AreEqual(escaped[0], dotted[0]);
        Assert.HasCount(1, quotedAtSign);
        Assert.IsEmpty(Rfc5256Threading.ParseMessageIds("<a@b@example.net>"));
        Assert.AreNotEqual(quotedAtSign[0], unquoted[0]);
        Assert.AreNotEqual(quoted[0], differentCase[0]);
        Assert.AreEqual(
            unquoted[0],
            Rfc5256Threading.ParseFirstMessageId("01KF8JCEOCBS0045PS@xxx.yyy.com"));
        Assert.IsEmpty(Rfc5256Threading.ParseMessageIds(
            "noise <without-domain> <@example.net> <local@>"));
    }

    [TestMethod]
    public void ReferencesBuildsChainsBranchesMissingParentsAndLoopSafeTrees()
    {
        var messages = new[]
        {
            Message(1, "A", "<a@example.net>"),
            Message(2, "B", "<b@example.net>", "<a@example.net>"),
            Message(3, "C", "<c@example.net>", "<a@example.net> <b@example.net>"),
            Message(4, "D", "<d@example.net>", "<a@example.net>"),
            Message(5, "E", "<e@example.net>", "<missing@example.net>"),
            Message(6, "F", "<f@example.net>", "<missing@example.net>"),
            Message(7, "G", "<loop-a@example.net>", "<loop-b@example.net>"),
            Message(8, "H", "<loop-b@example.net>", "<loop-a@example.net>"),
        };

        Assert.AreEqual(
            "(1 (2 3)(4))((5)(6))(8 7)",
            Rfc5256Threading.BuildReferences(messages));
    }

    [TestMethod]
    public void ReferencesUsesFirstDuplicateAndNormalizesQuotedIds()
    {
        var messages = new[]
        {
            Message(1, "Quoted root", """<"quoted"@example.net>"""),
            Message(2, "Quoted child", "<child@example.net>", "<quoted@example.net>"),
            Message(3, "Duplicate", "<quoted@example.net>"),
            Message(4, "Upper case ID", "<Case@example.net>"),
            Message(5, "Lower case reference", "<case-child@example.net>",
                "<case@example.net>"),
        };

        Assert.AreEqual(
            "(1 2)(3)(4)(5)",
            Rfc5256Threading.BuildReferences(messages));
    }

    [TestMethod]
    public void ReferencesPreservesEstablishedLinksAndReplacesTentativeParent()
    {
        var messages = new[]
        {
            Message(1, "A", "<a@example.net>"),
            Message(2, "F", "<f@example.net>",
                "<d@example.net> <e@example.net>"),
            Message(3, "D", "<d@example.net>"),
            Message(4, "E", "<e@example.net>", "<a@example.net>"),
        };

        Assert.AreEqual(
            "(1 4 2)(3)",
            Rfc5256Threading.BuildReferences(messages));
    }

    [TestMethod]
    public void ReferencesMergesRootThreadsUsingReplyAndDummyRules()
    {
        var messages = new[]
        {
            Message(1, "Topic", "<one@example.net>"),
            Message(2, "Re: topic", "<two@example.net>"),
            Message(3, "TOPIC", "<three@example.net>"),
            Message(4, "Re: Promote", "<four@example.net>"),
            Message(5, "Promote", "<five@example.net>"),
            Message(6, "Re:", "<six@example.net>"),
            Message(7, "Fwd:", "<seven@example.net>"),
        };

        Assert.AreEqual(
            "((1 2)(3))(5 4)(6)(7)",
            Rfc5256Threading.BuildReferences(messages));
    }

    [TestMethod]
    public void ReferencesMergesDummyRootsAndSortsTheirChildren()
    {
        var messages = new[]
        {
            Message(1, "Shared", "<one@example.net>", "<missing-a@example.net>"),
            Message(2, "Other A", "<two@example.net>", "<missing-a@example.net>"),
            Message(3, "Re: Shared", "<three@example.net>", "<missing-b@example.net>"),
            Message(4, "Other B", "<four@example.net>", "<missing-b@example.net>"),
            Message(5, "SHARED", "<five@example.net>"),
        };

        Assert.AreEqual(
            "((1)(2)(3)(4)(5))",
            Rfc5256Threading.BuildReferences(messages));
    }

    [TestMethod]
    [DataRow("[list] Topic", false)]
    [DataRow("Re: [list] Topic", true)]
    [DataRow("Topic (fwd)", true)]
    [DataRow("[fwd: Topic]", true)]
    public void ReplyOrForwardDetectionOnlyMarksReplyArtifacts(
        string subject,
        bool expected)
    {
        Assert.AreEqual(expected, Rfc5256.AnalyzeSubject(subject).IsReplyOrForward);
    }

    private static Rfc5256ThreadMessage Message(
        int identifier,
        string subject,
        string messageId,
        string? references = null)
    {
        var analyzed = Rfc5256.AnalyzeSubject(subject);
        return new Rfc5256ThreadMessage(
            identifier,
            identifier,
            new DateTime(2026, 1, identifier, 0, 0, 0, DateTimeKind.Utc),
            Convert.ToBase64String(Rfc5256.UnicodeCasemapSortKey(analyzed.BaseSubject)),
            analyzed.IsReplyOrForward,
            Rfc5256Threading.ParseFirstMessageId(messageId),
            Rfc5256Threading.ParseMessageIds(references));
    }
}
