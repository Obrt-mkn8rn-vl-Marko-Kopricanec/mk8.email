using System.Collections.Frozen;
using mk8.email.Contracts.Messaging;

namespace mk8.email.Gateway.Protocols.Jmap;

internal static class GatewayJmapFeatureCodec
{
    public const string CoreCapability = "urn:ietf:params:jmap:core";
    public const string MailCapability = "urn:ietf:params:jmap:mail";
    public const string SubmissionCapability = "urn:ietf:params:jmap:submission";
    public const string VacationResponseCapability = "urn:ietf:params:jmap:vacationresponse";
    public const string ContactsCapability = "urn:ietf:params:jmap:contacts";

    private static readonly FrozenDictionary<string, MailFeature> Features = new Dictionary<string, MailFeature>(StringComparer.Ordinal)
    {
        [CoreCapability] = MailFeature.Basic,
        [MailCapability] = MailFeature.Messages,
        [SubmissionCapability] = MailFeature.Submission,
        [VacationResponseCapability] = MailFeature.AutomaticReplies,
        [ContactsCapability] = MailFeature.Contacts,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static MailFeature Decode(string capability) =>
        Features.TryGetValue(capability, out var feature) ? feature : MailFeature.Unsupported;
}
