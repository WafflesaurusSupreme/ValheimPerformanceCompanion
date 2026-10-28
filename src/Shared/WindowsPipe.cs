using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Companion.Transport;

// Unity Mono does not implement WindowsIdentity.User/Owner, which CurrentUserOnly invokes.
internal static class WindowsPipe
{
    internal static NamedPipeServerStream CreateServer(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("Invalid local pipe name.", nameof(name));
        string sid = ProcessUser(GetCurrentProcess());
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:" + sid + "D:P(A;;GA;;;" + sid + ")", 1, out IntPtr descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
            // Duplex, overlapped I/O, first instance; byte mode and reject remote clients.
            SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000 | 0x00080000, 8, 1, 4096, 4096, 0, ref security);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    internal static void VerifyServer(NamedPipeClientStream pipe, int? expectedPid = null)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (expectedPid.HasValue && serverPid != (uint)expectedPid.Value) throw new InvalidDataException("Recorder pipe belongs to a different process.");
        IntPtr process = OpenProcess(0x1000, false, serverPid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!string.Equals(ProcessUser(process), ProcessUser(GetCurrentProcess()), StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The pipe server belongs to a different Windows user.");
        }
        finally { CloseHandle(process); }
    }

    private static string ProcessUser(IntPtr process)
    {
        if (!OpenProcessToken(process, 8, out IntPtr token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out int length);
            int error = Marshal.GetLastWin32Error();
            if (error != 122 || length <= 0 || length > 65536) throw new Win32Exception(error);
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!ConvertSidToStringSid(Marshal.ReadIntPtr(buffer), out IntPtr text)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Windows returned an empty user identity."); }
                finally { LocalFree(text); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
