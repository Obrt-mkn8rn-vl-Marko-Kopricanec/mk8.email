namespace mk8.email.Contracts.Messaging;

public sealed record MailPlanApplicationRequest(
    ProtocolAuthentication Authentication,
    MailAdmissionPlan? Plan = null);
