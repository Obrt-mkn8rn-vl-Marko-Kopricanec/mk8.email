using System.Runtime.InteropServices;

namespace mk8.email.Application.Worker;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct WorkerDrainResult(int ApplicationRequests, int MailMessages);
