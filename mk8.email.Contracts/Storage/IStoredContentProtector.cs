namespace mk8.email.Contracts.Storage;

public interface IStoredContentProtector
{
    ReadOnlyMemory<byte> Protect(ReadOnlySpan<byte> content, ReadOnlySpan<byte> associatedData);
    ReadOnlyMemory<byte> Unprotect(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData);
}
