namespace mk8.email.Imap.Presentation.Protocol;

internal enum ImapBinarySectionStatus
{
    Success,
    NotFound,
    NotLeaf,
    UnknownTransferEncoding,
    InvalidContent,
}
