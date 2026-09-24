using System.Runtime.InteropServices;

namespace mk8.email.MailWire;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ImapUidSetRange(int? Start, int? End);
