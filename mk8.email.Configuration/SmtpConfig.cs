namespace mk8.email.Configuration;

public sealed class SmtpConfig
{
    public string Hostname { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public int SubmissionPort { get; init; } = 587;
    public int ImplicitTlsPort { get; init; } = 465;
    public bool EnableSmtp { get; init; } = true;
    public bool EnableSubmission { get; init; }
    public bool EnableImplicitTls { get; init; }
    public bool EnableStartTls { get; init; }
    public bool RequireTls { get; init; }
    public bool RequireAuth { get; init; } = true;
    public bool AllowRelay { get; init; }
}
