using System.Text;
using MimeKit;
using mk8.email.Application.Protocol;
using mk8.email.Application.Services;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Tests;

[TestClass]
public sealed class SmtpDsnTests
{
    [TestMethod]
    public void DsnParametersNormalizeAndRejectAmbiguousValues()
    {
        Assert.IsTrue(SmtpDsn.TryNormalizeReturnContent("hdrs", out var returnContent));
        Assert.AreEqual("HDRS", returnContent);
        Assert.IsFalse(SmtpDsn.TryNormalizeReturnContent("body", out _));

        Assert.IsTrue(SmtpDsn.TryNormalizeNotify(
            "success,failure,delay",
            out var notify));
        Assert.AreEqual("SUCCESS,FAILURE,DELAY", notify);
        Assert.IsTrue(SmtpDsn.TryNormalizeNotify("never", out notify));
        Assert.AreEqual("NEVER", notify);
        Assert.IsFalse(SmtpDsn.TryNormalizeNotify("NEVER,FAILURE", out _));
        Assert.IsFalse(SmtpDsn.TryNormalizeNotify("FAILURE,FAILURE", out _));

        Assert.IsTrue(SmtpDsn.TryValidateEnvelopeId("job+2B42+3Ddone"));
        Assert.IsTrue(SmtpDsn.TryDecodeEnvelopeId("job+2B42+3Ddone", out var envelopeId));
        Assert.AreEqual("job+42=done", envelopeId);
        Assert.IsFalse(SmtpDsn.TryValidateEnvelopeId("job+2b42"));
        Assert.IsFalse(SmtpDsn.TryValidateEnvelopeId("job=42"));
    }

    [TestMethod]
    public void OriginalRecipientSupportsXtextAndInternationalUnitext()
    {
        Assert.IsTrue(SmtpDsn.TryValidateOriginalRecipient(
            "rfc822;old+2Btag+40example.net",
            smtpUtf8: false,
            out var addressType,
            out var decoded));
        Assert.AreEqual("rfc822", addressType);
        Assert.AreEqual("old+tag@example.net", decoded);

        Assert.IsTrue(SmtpDsn.TryValidateOriginalRecipient(
            @"utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@example.net",
            smtpUtf8: false,
            out addressType,
            out decoded));
        Assert.AreEqual("utf-8", addressType);
        Assert.AreEqual("δοκιμή@example.net", decoded);

        Assert.IsFalse(SmtpDsn.TryValidateOriginalRecipient(
            "utf-8;δοκιμή@example.net",
            smtpUtf8: false,
            out _,
            out _));
        Assert.IsFalse(SmtpDsn.TryValidateOriginalRecipient(
            @"utf-8;\x{D800}@example.net",
            smtpUtf8: true,
            out _,
            out _));
    }

    [TestMethod]
    public void FailureReportUsesStandardDeliveryStatusAndHonorsFullReturn()
    {
        var message = CreateMessage(
            "sender@example.net",
            "recipient@example.com",
            requiresSmtpUtf8: false);
        message.DsnReturnContent = "FULL";
        message.DsnEnvelopeId = "job+2B42+3Ddone";
        var recipient = message.Recipients.Single();
        recipient.DsnOriginalRecipient = "rfc822;old+2Btag+40example.net";

        var built = DeliveryStatusNotificationBuilder.Build(
            message,
            recipient,
            DeliveryStatusAction.Failed,
            message.RawMessage!,
            "email.mk8n.com",
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            "550 5.1.1 mailbox missing",
            "5.1.1",
            "mx.example.com");

        Assert.IsFalse(built.RequiresSmtpUtf8);
        using var parsed = MimeMessage.Load(new MemoryStream(
            Encoding.Latin1.GetBytes(built.RawMessage)));
        Assert.AreEqual("auto-replied", parsed.Headers[HeaderId.AutoSubmitted]);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsed.Body);
        Assert.AreEqual("delivery-status", report.ContentType.Parameters["report-type"]);
        Assert.HasCount(3, report);
        var status = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.HasCount(2, status.StatusGroups);
        Assert.AreEqual("job+42=done", status.StatusGroups[0]["Original-Envelope-ID"]);
        Assert.AreEqual("dns; email.mk8n.com", status.StatusGroups[0]["Reporting-MTA"]);
        Assert.AreEqual(
            "rfc822; old+tag@example.net",
            status.StatusGroups[1]["Original-Recipient"]);
        Assert.AreEqual(
            "rfc822; recipient@example.com",
            status.StatusGroups[1]["Final-Recipient"]);
        Assert.AreEqual("failed", status.StatusGroups[1]["Action"]);
        Assert.AreEqual("5.1.1", status.StatusGroups[1]["Status"]);
        Assert.AreEqual("dns; mx.example.com", status.StatusGroups[1]["Remote-MTA"]);
        Assert.AreEqual("message/rfc822", report[2].ContentType.MimeType);
    }

    [TestMethod]
    public void InternationalReportUsesGlobalTypesWithTransferEncoding()
    {
        var message = CreateMessage(
            "sender@example.net",
            "δοκιμή@example.com",
            requiresSmtpUtf8: true);
        message.DsnReturnContent = "HDRS";
        var recipient = message.Recipients.Single();
        recipient.DsnOriginalRecipient = @"utf-8;\x{3B4}\x{3BF}\x{3BA}\x{3B9}\x{3BC}\x{3AE}@example.com";

        var built = DeliveryStatusNotificationBuilder.Build(
            message,
            recipient,
            DeliveryStatusAction.Failed,
            message.RawMessage!,
            "email.mk8n.com",
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            "Το γραμματοκιβώτιο λείπει",
            "5.1.1");

        Assert.IsFalse(built.RequiresSmtpUtf8);
        using var parsed = MimeMessage.Load(new MemoryStream(
            Encoding.Latin1.GetBytes(built.RawMessage)));
        var report = Assert.IsInstanceOfType<MultipartReport>(parsed.Body);
        var statusPart = Assert.IsInstanceOfType<MimePart>(report[1]);
        Assert.AreEqual("message/global-delivery-status", statusPart.ContentType.MimeType);
        Assert.AreEqual(ContentEncoding.Base64, statusPart.ContentTransferEncoding);
        var status = DecodePart(statusPart);
        StringAssert.Contains(status, "Original-Recipient: utf-8; δοκιμή@example.com");
        StringAssert.Contains(status, "Final-Recipient: utf-8; δοκιμή@example.com");
        StringAssert.Contains(status, "Diagnostic-Code: smtp; Το γραμματοκιβώτιο λείπει");
        var returnedHeaders = Assert.IsInstanceOfType<MimePart>(report[2]);
        Assert.AreEqual("message/global-headers", returnedHeaders.ContentType.MimeType);
        Assert.AreEqual(ContentEncoding.Base64, returnedHeaders.ContentTransferEncoding);
        StringAssert.Contains(DecodePart(returnedHeaders), "Subject: Žuta pošta");
    }

    [TestMethod]
    [DataRow((int)DeliveryStatusAction.Failed, "Failure", "failed", "5.0.0", "message/rfc822")]
    [DataRow((int)DeliveryStatusAction.Delayed, "Delay", "delayed", "4.0.0", "text/rfc822-headers")]
    [DataRow((int)DeliveryStatusAction.Delivered, "Success", "delivered", "2.0.0", "text/rfc822-headers")]
    [DataRow((int)DeliveryStatusAction.Relayed, "Relayed", "relayed", "2.0.0", "text/rfc822-headers")]
    [DataRow((int)DeliveryStatusAction.Expanded, "Expanded", "expanded", "2.0.0", "text/rfc822-headers")]
    public void ReportActionsKeepTheirStatusAndReturnContent(
        int actionValue,
        string subjectSuffix,
        string actionName,
        string statusCode,
        string returnedType)
    {
        var message = CreateMessage(
            "sender@example.net",
            "recipient@example.com",
            requiresSmtpUtf8: false);
        message.DsnReturnContent = "FULL";
        var built = DeliveryStatusNotificationBuilder.Build(
            message,
            message.Recipients.Single(),
            (DeliveryStatusAction)actionValue,
            message.RawMessage!,
            "email.mk8n.com",
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

        using var parsed = MimeMessage.Load(new MemoryStream(
            Encoding.Latin1.GetBytes(built.RawMessage)));
        Assert.AreEqual($"Delivery Status Notification ({subjectSuffix})", parsed.Subject);
        var report = Assert.IsInstanceOfType<MultipartReport>(parsed.Body);
        var deliveryStatus = Assert.IsInstanceOfType<MessageDeliveryStatus>(report[1]);
        Assert.AreEqual(actionName, deliveryStatus.StatusGroups[1]["Action"]);
        Assert.AreEqual(statusCode, deliveryStatus.StatusGroups[1]["Status"]);
        Assert.AreEqual(returnedType, report[2].ContentType.MimeType);
    }

    private static MailQueueMessageDB CreateMessage(
        string sender,
        string recipientAddress,
        bool requiresSmtpUtf8)
    {
        var message = new MailQueueMessageDB
        {
            Id = Guid.Parse("0199a1ab-0000-7000-8000-000000000001"),
            EnvelopeSender = sender,
            RawMessage = requiresSmtpUtf8
                ? Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(
                    "From: sender@example.net\r\n" +
                    $"To: {recipientAddress}\r\n" +
                    "Subject: Žuta pošta\r\n\r\n" +
                    "Pozdrav\r\n"))
                : "From: sender@example.net\r\n" +
                    $"To: {recipientAddress}\r\n" +
                    "Subject: original\r\n\r\n" +
                    "body\r\n",
            RequiresSmtpUtf8 = requiresSmtpUtf8,
            ReceivedAt = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc),
        };
        message.Recipients.Add(new MailQueueRecipientDB
        {
            Id = Guid.Parse("0199a1ab-0000-7000-8000-000000000002"),
            Message = message,
            MessageId = message.Id,
            Recipient = recipientAddress,
        });
        return message;
    }

    private static string DecodePart(MimeEntity entity)
    {
        var part = Assert.IsInstanceOfType<MimePart>(entity);
        using var stream = new MemoryStream();
        part.Content!.DecodeTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
