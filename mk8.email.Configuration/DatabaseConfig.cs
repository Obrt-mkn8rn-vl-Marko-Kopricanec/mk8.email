namespace mk8.email.Configuration;

public sealed class DatabaseConfig
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Name { get; init; } = "mk8email";
    public string Username { get; init; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public string? PasswordFile { get; init; }
}
