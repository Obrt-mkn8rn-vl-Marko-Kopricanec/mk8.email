namespace mk8.email.Messaging;

public interface IMessagingPayloadProtector
{
    ProtectedPayload Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    byte[] Unprotect(ProtectedPayload payload, ReadOnlySpan<byte> associatedData);
}
