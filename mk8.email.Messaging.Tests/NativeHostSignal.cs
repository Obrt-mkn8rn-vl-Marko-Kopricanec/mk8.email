using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace mk8.email.Messaging.Tests;

internal static class NativeHostSignal
{
    public static void TerminateOwnedChild(Process child)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The owned pidfd control requires Linux.");
        if (child.HasExited) throw new InvalidOperationException("The owned child exited before graceful-stop admission.");
        var descriptor = OpenProcessDescriptor(child.Id, flags: 0);
        if (descriptor < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var target = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        // Process.Start retains this child's wait state. Recheck after acquisition:
        // if the original child was reaped/replaced, never signal the acquired fd.
        if (child.HasExited) throw new InvalidOperationException("The owned child exited during pidfd acquisition.");
        // Linux SIGTERM is protocol/platform-defined, not an allocated host input.
        // The pidfd binds the target even if its numerical PID is later recycled.
        if (SendProcessSignal(target, signal: 15, nint.Zero, flags: 0) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // These two bounded test-only libc signatures need runtime marshalling for
    // SafeHandle. LibraryImport would require enabling unsafe project compilation;
    // retain the existing project policy rather than widening it for this fixture.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenProcessDescriptor(int processId, uint flags);

    [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SendProcessSignal(SafeFileHandle target, int signal, nint information, uint flags);
#pragma warning restore SYSLIB1054
}
