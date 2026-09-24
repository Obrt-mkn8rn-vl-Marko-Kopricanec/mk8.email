namespace mk8.email.Messaging;

// Existing key material is a byte array in the published messaging API.
#pragma warning disable CA1819
public sealed record MessagingEncryptionKey(string Id, byte[] Key);
#pragma warning restore CA1819
