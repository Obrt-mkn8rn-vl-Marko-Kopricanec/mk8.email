namespace mk8.email.Application.Interfaces;

public sealed record OpenIdConnectPublicKey(
    string KeyType,
    string Use,
    string KeyId,
    string Algorithm,
    string Modulus,
    string Exponent);
