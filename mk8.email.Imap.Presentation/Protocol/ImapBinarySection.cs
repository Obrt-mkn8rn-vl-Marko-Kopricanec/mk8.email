namespace mk8.email.Imap.Presentation.Protocol;

internal readonly record struct ImapBinarySection(byte[] Content)
{
    public bool RequiresLiteral8 => Content.AsSpan().Contains((byte)0);
}
