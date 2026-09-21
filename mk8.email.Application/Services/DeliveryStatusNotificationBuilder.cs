using System.Globalization;
using System.Text;
using mk8.email.Application.Protocol;
using mk8.email.Infrastructure.Models;

namespace mk8.email.Application.Services;

internal enum DeliveryStatusAction
{
    Failed,
    Delayed,
    Delivered,
    Relayed,
    Expanded,
}

internal sealed record BuiltDeliveryStatusNotification(
    string RawMessage,
    bool RequiresSmtpUtf8);

internal static class DeliveryStatusNotificationBuilder
{
    private const int MaximumFullReturnBytes = 1024 * 1024;

    public static BuiltDeliveryStatusNotification Build(
        MailQueueMessageDB message,
        MailQueueRecipientDB recipient,
        DeliveryStatusAction action,
        string reportingHost,
        DateTimeOffset now,
        string? diagnostic = null,
        string? enhancedStatusCode = null,
        string? remoteMta = null)
    {
        var actionName = action.ToString().ToLowerInvariant();
        var status = NormalizeStatus(enhancedStatusCode, action);
        var safeDiagnostic = SanitizeFieldText(
            diagnostic ?? DefaultDiagnostic(action),
            allowUtf8: message.RequiresSmtpUtf8);
        var finalAddressType = message.RequiresSmtpUtf8
            || recipient.Recipient.Any(character => !char.IsAscii(character))
                ? "utf-8"
                : "rfc822";

        string? originalAddressType = null;
        string? originalAddress = null;
        if (recipient.DsnOriginalRecipient is not null
            && SmtpDsn.TryValidateOriginalRecipient(
                recipient.DsnOriginalRecipient,
                message.RequiresSmtpUtf8,
                out var parsedType,
                out var parsedAddress))
        {
            originalAddressType = parsedType;
            originalAddress = parsedAddress;
        }

        var usesGlobalStatus = message.RequiresSmtpUtf8
            || finalAddressType == "utf-8"
            || string.Equals(originalAddressType, "utf-8", StringComparison.OrdinalIgnoreCase)
            || safeDiagnostic.Any(character => !char.IsAscii(character));
        var statusBody = new StringBuilder();
        if (SmtpDsn.TryDecodeEnvelopeId(message.DsnEnvelopeId, out var envelopeId))
        {
            statusBody.Append("Original-Envelope-ID: ")
                .Append(SanitizeFieldText(envelopeId, usesGlobalStatus))
                .Append("\r\n");
        }
        statusBody.Append("Reporting-MTA: dns; ").Append(reportingHost).Append("\r\n")
            .Append("Arrival-Date: ").Append(FormatDate(message.ReceivedAt)).Append("\r\n")
            .Append("\r\n");
        if (originalAddressType is not null && originalAddress is not null)
        {
            statusBody.Append("Original-Recipient: ")
                .Append(originalAddressType)
                .Append("; ")
                .Append(SanitizeFieldText(originalAddress, usesGlobalStatus))
                .Append("\r\n");
        }
        statusBody.Append("Final-Recipient: ")
            .Append(finalAddressType)
            .Append("; ")
            .Append(SanitizeFieldText(recipient.Recipient, usesGlobalStatus))
            .Append("\r\n")
            .Append("Action: ").Append(actionName).Append("\r\n")
            .Append("Status: ").Append(status).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(remoteMta))
        {
            statusBody.Append("Remote-MTA: dns; ")
                .Append(SanitizeFieldText(remoteMta, allowUtf8: false))
                .Append("\r\n");
        }
        if (action is DeliveryStatusAction.Failed or DeliveryStatusAction.Delayed)
        {
            statusBody.Append("Diagnostic-Code: smtp; ")
                .Append(safeDiagnostic)
                .Append("\r\n");
        }
        statusBody.Append("Last-Attempt-Date: ").Append(FormatDate(now.UtcDateTime)).Append("\r\n");

        var originalHeaders = ExtractHeaders(message.RawMessage);
        var returnFullMessage = action == DeliveryStatusAction.Failed
            && string.Equals(message.DsnReturnContent, "FULL", StringComparison.Ordinal)
            && MailWireEncoding.Instance.GetByteCount(message.RawMessage) <= MaximumFullReturnBytes;
        var returnedContent = returnFullMessage ? message.RawMessage : originalHeaders;
        var returnedType = message.RequiresSmtpUtf8
            ? (returnFullMessage ? "message/global" : "message/global-headers")
            : (returnFullMessage ? "message/rfc822" : "text/rfc822-headers");

        var boundary = $"=_mk8_dsn_{recipient.Id:N}_{actionName}";
        while (message.RawMessage.Contains(boundary, StringComparison.Ordinal))
            boundary += "x";

        var humanText = action switch
        {
            DeliveryStatusAction.Failed =>
                $"Delivery to {recipient.Recipient} failed.\r\n\r\n{safeDiagnostic}\r\n",
            DeliveryStatusAction.Delayed =>
                $"Delivery to {recipient.Recipient} has been delayed.\r\n\r\n{safeDiagnostic}\r\n",
            DeliveryStatusAction.Delivered =>
                $"Delivery to {recipient.Recipient} succeeded.\r\n",
            DeliveryStatusAction.Relayed =>
                $"The message for {recipient.Recipient} was relayed.\r\n",
            _ => $"The address {recipient.Recipient} was expanded.\r\n",
        };
        var subject = action switch
        {
            DeliveryStatusAction.Failed => "Delivery Status Notification (Failure)",
            DeliveryStatusAction.Delayed => "Delivery Status Notification (Delay)",
            DeliveryStatusAction.Delivered => "Delivery Status Notification (Success)",
            DeliveryStatusAction.Relayed => "Delivery Status Notification (Relayed)",
            _ => "Delivery Status Notification (Expanded)",
        };

        var messageBuilder = new StringBuilder();
        AppendUtf8Wire(messageBuilder,
            $"From: Mail Delivery System <mailer-daemon@{reportingHost}>\r\n" +
            $"To: <{message.EnvelopeSender}>\r\n" +
            $"Subject: {subject}\r\n" +
            $"Date: {FormatDate(now.UtcDateTime)}\r\n" +
            $"Message-ID: <dsn-{recipient.Id:N}-{actionName}@{reportingHost}>\r\n" +
            "Auto-Submitted: auto-replied\r\n" +
            "MIME-Version: 1.0\r\n" +
            $"Content-Type: multipart/report; report-type=delivery-status; boundary=\"{boundary}\"\r\n" +
            "\r\n");
        messageBuilder.Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("Content-Transfer-Encoding: base64\r\n\r\n")
            .Append(WrapBase64(Encoding.UTF8.GetBytes(humanText))).Append("\r\n")
            .Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: ")
            .Append(usesGlobalStatus
                ? "message/global-delivery-status"
                : "message/delivery-status")
            .Append("\r\n");
        if (usesGlobalStatus)
        {
            messageBuilder.Append("Content-Transfer-Encoding: base64\r\n\r\n")
                .Append(WrapBase64(Encoding.UTF8.GetBytes(statusBody.ToString())))
                .Append("\r\n");
        }
        else
        {
            messageBuilder.Append("\r\n").Append(statusBody).Append("\r\n");
        }

        messageBuilder.Append("--").Append(boundary).Append("\r\n")
            .Append("Content-Type: ").Append(returnedType).Append("\r\n");
        if (message.RequiresSmtpUtf8)
        {
            messageBuilder.Append("Content-Transfer-Encoding: base64\r\n\r\n")
                .Append(WrapBase64(MailWireEncoding.Instance.GetBytes(returnedContent)))
                .Append("\r\n");
        }
        else
        {
            messageBuilder.Append("Content-Transfer-Encoding: ")
                .Append(SmtpInternationalization.ContainsEightBit(returnedContent) ? "8bit" : "7bit")
                .Append("\r\n\r\n")
                .Append(returnedContent);
            if (!returnedContent.EndsWith("\r\n", StringComparison.Ordinal))
                messageBuilder.Append("\r\n");
        }
        messageBuilder.Append("--").Append(boundary).Append("--\r\n");

        return new BuiltDeliveryStatusNotification(
            messageBuilder.ToString(),
            message.EnvelopeSender.Any(character => !char.IsAscii(character)));
    }

    private static string NormalizeStatus(string? value, DeliveryStatusAction action)
    {
        var expectedClass = action switch
        {
            DeliveryStatusAction.Failed => '5',
            DeliveryStatusAction.Delayed => '4',
            _ => '2',
        };
        if (value is not null)
        {
            var parts = value.Split('.');
            if (parts.Length == 3
                && parts[0] is "2" or "4" or "5"
                && parts.Skip(1).All(part =>
                    part.Length is >= 1 and <= 3
                    && part.All(char.IsAsciiDigit)))
            {
                return expectedClass + value[1..];
            }
        }
        return $"{expectedClass}.0.0";
    }

    private static string DefaultDiagnostic(DeliveryStatusAction action) => action switch
    {
        DeliveryStatusAction.Failed => "Delivery failed.",
        DeliveryStatusAction.Delayed => "Delivery is temporarily delayed.",
        DeliveryStatusAction.Delivered => "Delivery succeeded.",
        _ => "The message was relayed to another mail system.",
    };

    private static string ExtractHeaders(string rawMessage)
    {
        var separator = rawMessage.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator >= 0)
            return rawMessage[..(separator + 2)];
        separator = rawMessage.IndexOf("\n\n", StringComparison.Ordinal);
        return separator >= 0
            ? rawMessage[..separator].Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n"
            : rawMessage + (rawMessage.EndsWith("\r\n", StringComparison.Ordinal) ? string.Empty : "\r\n");
    }

    private static string SanitizeFieldText(string value, bool allowUtf8)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 512));
        foreach (var character in value)
        {
            if (builder.Length >= 512)
                break;
            if (character is '\r' or '\n' or '\0' || char.IsControl(character))
            {
                builder.Append(' ');
                continue;
            }
            builder.Append(allowUtf8 || char.IsAscii(character) ? character : '?');
        }
        return builder.ToString().Trim();
    }

    private static string FormatDate(DateTime value) =>
        value.ToUniversalTime().ToString(
            "ddd, dd MMM yyyy HH:mm:ss +0000",
            CultureInfo.InvariantCulture);

    private static string WrapBase64(byte[] value)
    {
        var encoded = Convert.ToBase64String(value);
        var builder = new StringBuilder(encoded.Length + encoded.Length / 76 * 2);
        for (var index = 0; index < encoded.Length; index += 76)
        {
            if (builder.Length > 0)
                builder.Append("\r\n");
            builder.Append(encoded, index, Math.Min(76, encoded.Length - index));
        }
        return builder.ToString();
    }

    private static void AppendUtf8Wire(StringBuilder builder, string value) =>
        builder.Append(MailWireEncoding.Instance.GetString(Encoding.UTF8.GetBytes(value)));
}
