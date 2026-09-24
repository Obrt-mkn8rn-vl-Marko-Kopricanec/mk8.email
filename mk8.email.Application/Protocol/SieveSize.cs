namespace mk8.email.Application.Protocol;

internal sealed record SieveSize(long Bytes, bool Over) : SieveTest;
