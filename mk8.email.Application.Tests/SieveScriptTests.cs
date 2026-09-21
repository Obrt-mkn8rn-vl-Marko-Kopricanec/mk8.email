using mk8.email.Application.Protocol;
using mk8.email.Contracts.Enums;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SieveScriptTests
{
    private const string RawMessage =
        "From: Sender <sender@example.net>\r\n" +
        "To: admin@mk8n.com\r\n" +
        "Subject: Queue test\r\n" +
        "X-List: engineering\r\n\r\n" +
        "This is the message body.\r\n";

    [TestMethod]
    public void ConditionalFileIntoAppliesFlagsAndSuppressesImplicitKeep()
    {
        var compilation = SieveScript.Compile(
            """
            require ["fileinto", "body", "imap4flags", "mailbox"];
            if allof (
                header :contains ["Subject", "X-List"] ["queue", "engineering"],
                body :contains "message body"
            ) {
                setflag ["\\Flagged", "project"];
                fileinto :create :flags ["\\Seen", "project"] "Projects/MK8";
            } else {
                keep;
            }
            """);

        Assert.IsTrue(compilation.Succeeded, Format(compilation));
        var result = Evaluate(compilation);
        Assert.AreEqual(1, result.Deliveries.Count);
        Assert.AreEqual("Projects/MK8", result.Deliveries[0].Folder);
        Assert.IsTrue(result.Deliveries[0].Create);
        CollectionAssert.AreEquivalent(
            new[] { "\\Seen", "project" },
            result.Deliveries[0].Flags.ToArray());
        Assert.AreEqual(0, result.Redirects.Count);
        Assert.IsFalse(result.Discarded);
    }

    [TestMethod]
    public void AddressEnvelopeGlobAndCopyPreserveImplicitKeep()
    {
        var compilation = SieveScript.Compile(
            """
            require ["copy", "envelope"];
            if allof (
                address :domain :is "From" "example.net",
                envelope :matches "to" "admin@*.com"
            ) {
                redirect :copy "archive@example.org";
            }
            """);

        Assert.IsTrue(compilation.Succeeded, Format(compilation));
        var result = Evaluate(compilation);
        Assert.AreEqual(1, result.Deliveries.Count);
        Assert.AreEqual(DefaultFolders.Inbox, result.Deliveries[0].Folder);
        CollectionAssert.AreEqual(
            new[] { "archive@example.org" },
            result.Redirects.ToArray());
    }

    [TestMethod]
    public void MailboxExistsAndFlagStateSelectTheExpectedBranch()
    {
        var compilation = SieveScript.Compile(
            """
            require ["fileinto", "imap4flags", "mailbox"];
            if mailboxexists "Archive" {
                addflag ["\\Seen", "retained"];
                fileinto "Archive";
            } else {
                discard;
            }
            """);

        Assert.IsTrue(compilation.Succeeded, Format(compilation));
        var result = Evaluate(
            compilation,
            new HashSet<string>([DefaultFolders.Inbox, "Archive"], StringComparer.Ordinal));
        Assert.AreEqual(1, result.Deliveries.Count);
        Assert.AreEqual("Archive", result.Deliveries[0].Folder);
        CollectionAssert.AreEquivalent(
            new[] { "\\Seen", "retained" },
            result.Deliveries[0].Flags.ToArray());
        Assert.IsFalse(result.Discarded);
    }

    [TestMethod]
    public void RejectAcceptsDotStuffedMultilineReason()
    {
        var compilation = SieveScript.Compile(
            """
            require "reject";
            reject text:
            Policy refusal
            ..Contact the recipient.
            .
            ;
            """);

        Assert.IsTrue(compilation.Succeeded, Format(compilation));
        var result = Evaluate(compilation);
        Assert.AreEqual("Policy refusal\r\n.Contact the recipient.\r\n", result.RejectReason);
        Assert.AreEqual(0, result.Deliveries.Count);
    }

    [TestMethod]
    public void CompilerRejectsUndeclaredUnsupportedAndOversizedScripts()
    {
        var undeclared = SieveScript.Compile("fileinto \"Archive\";");
        Assert.IsFalse(undeclared.Succeeded);
        StringAssert.Contains(undeclared.Diagnostics[0].Message, "must be declared");

        var unsupported = SieveScript.Compile("require \"vacation\"; keep;");
        Assert.IsFalse(unsupported.Succeeded);
        StringAssert.Contains(unsupported.Diagnostics[0].Message, "not supported");

        var oversized = SieveScript.Compile(new string('x', SieveScript.MaximumScriptBytes + 1));
        Assert.IsFalse(oversized.Succeeded);
        StringAssert.Contains(oversized.Diagnostics[0].Message, "one-megabyte");
    }

    [TestMethod]
    public void InvalidSyntaxReportsSourceLocation()
    {
        var compilation = SieveScript.Compile(
            """
            if true {
                keep
            }
            """);

        Assert.IsFalse(compilation.Succeeded);
        Assert.AreEqual(3, compilation.Diagnostics[0].Line);
        Assert.IsTrue(compilation.Diagnostics[0].Column > 0);
    }

    [TestMethod]
    public void BodyContentSearchDecodesMimeTransferEncoding()
    {
        var compilation = SieveScript.Compile(
            """
            require ["body", "fileinto"];
            if body :content "application/json" :contains "critical" {
                fileinto "Archive";
            }
            """);
        const string mimeMessage =
            "From: sender@example.net\r\n" +
            "To: admin@mk8n.com\r\n" +
            "Subject: MIME body\r\n" +
            "Content-Type: multipart/mixed; boundary=test\r\n\r\n" +
            "--test\r\nContent-Type: text/plain\r\n\r\nordinary body\r\n" +
            "--test\r\nContent-Type: application/json\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            "eyJjcml0aWNhbCI6dHJ1ZX0=\r\n" +
            "--test--\r\n";

        Assert.IsTrue(compilation.Succeeded, Format(compilation));
        var result = SieveScript.Evaluate(
            compilation.Program!,
            new SieveMessageContext(
                "sender@example.net",
                "admin@mk8n.com",
                mimeMessage,
                DefaultFolders.Inbox,
                new HashSet<string>([DefaultFolders.Inbox, "Archive"], StringComparer.Ordinal)));
        Assert.AreEqual("Archive", result.Deliveries.Single().Folder);
    }

    [TestMethod]
    public void CompilerRejectsEmptyStringLists()
    {
        var compilation = SieveScript.Compile("require []; keep;");

        Assert.IsFalse(compilation.Succeeded);
        StringAssert.Contains(compilation.Diagnostics[0].Message, "cannot be empty");
    }

    [TestMethod]
    public void AsciiCasemapDoesNotFoldNonAsciiCharacters()
    {
        const string message =
            "From: sender@example.net\r\n" +
            "To: admin@mk8n.com\r\n" +
            "Subject: ÉX\r\n\r\nbody\r\n";
        var exact = SieveScript.Compile(
            "if header :is \"Subject\" \"éx\" { discard; } else { keep; }");
        var wildcard = SieveScript.Compile(
            "if header :matches \"Subject\" \"É?\" { discard; } else { keep; }");

        Assert.IsTrue(exact.Succeeded, Format(exact));
        Assert.IsTrue(wildcard.Succeeded, Format(wildcard));
        Assert.IsFalse(EvaluateMessage(exact, message).Discarded);
        Assert.IsTrue(EvaluateMessage(wildcard, message).Discarded);
    }

    private static SieveEvaluationResult Evaluate(
        SieveCompilationResult compilation,
        IReadOnlySet<string>? mailboxes = null) =>
        SieveScript.Evaluate(
            compilation.Program!,
            new SieveMessageContext(
                "sender@example.net",
                "admin@mk8n.com",
                RawMessage,
                DefaultFolders.Inbox,
                mailboxes ?? new HashSet<string>(DefaultFolders.All, StringComparer.Ordinal)));

    private static SieveEvaluationResult EvaluateMessage(
        SieveCompilationResult compilation,
        string message) =>
        SieveScript.Evaluate(
            compilation.Program!,
            new SieveMessageContext(
                "sender@example.net",
                "admin@mk8n.com",
                message,
                DefaultFolders.Inbox,
                new HashSet<string>(DefaultFolders.All, StringComparer.Ordinal)));

    private static string Format(SieveCompilationResult result) => string.Join(
        "; ",
        result.Diagnostics.Select(item => $"{item.Line}:{item.Column} {item.Message}"));
}
