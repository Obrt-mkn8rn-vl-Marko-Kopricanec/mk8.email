namespace mk8.email.Configuration;

public sealed class Pop3Config
{
    public int Port { get; init; } = 110;
    public int ImplicitTlsPort { get; init; } = 995;
    public bool EnablePop3 { get; init; }
    public bool EnableImplicitTls { get; init; }
    public bool EnableStartTls { get; init; }
}
