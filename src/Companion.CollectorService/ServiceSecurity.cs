using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Companion.Collector;

internal sealed record Caller(int Pid, long Started, string Sid);

internal static class ServiceSecurity
{
    private static readonly SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private const FileSystemRights Mutating = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static void ValidateInstallation(string directory)
    {
        string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd(Path.DirectorySeparatorChar);
        if (!path.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The profiling service must be installed in protected Program Files.");
        for (DirectoryInfo? part = new(path); part is not null && part.FullName.Length > programFiles.Length; part = part.Parent)
            ValidateProtectedDirectory(part);
        var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(path));
        while (pending.TryPop(out DirectoryInfo? current))
        {
            ValidateProtectedDirectory(current);
            foreach (FileSystemInfo entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Installed collector files cannot be links.");
                if (entry is DirectoryInfo child) pending.Push(child);
                else
                {
                    FileSecurity security = ((FileInfo)entry).GetAccessControl();
                    ValidateOwner(security);
                    ValidateRules(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                }
            }
        }
    }

    private static void ValidateRules(IEnumerable<FileSystemAccessRule> rules)
    {
        foreach (FileSystemAccessRule rule in rules)
        {
            string sid = rule.IdentityReference.Value;
            bool trustedWriter = sid == system.Value || sid == administrators.Value || sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                && (rule.FileSystemRights & Mutating) != 0 && !trustedWriter)
                throw new UnauthorizedAccessException("The collector installation permits writes by an unprivileged identity.");
        }
    }

    private static void ValidateProtectedDirectory(DirectoryInfo directory)
    {
        directory.Refresh(); // DirectoryInfo caches a pre-create Exists/Attributes result.
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Collector directories must be existing, unlinked directories.");
        DirectorySecurity security = directory.GetAccessControl();
        ValidateOwner(security);
        ValidateRules(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
    }

    private static void ValidateOwner(FileSystemSecurity security)
    {
        string owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? throw new UnauthorizedAccessException("Missing collector file owner.");
        if (owner != system.Value && owner != administrators.Value && owner != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
            throw new UnauthorizedAccessException("Collector directories must be owned by Windows or Administrators.");
    }

    internal static void CreateProtectedDirectory(string path, string? readerSid = null)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists) { ValidateProtectedDirectory(directory); return; }
        var security = new DirectorySecurity();
        security.SetOwner(system); security.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Parent directory metadata is readable; trace files inherit only their specific owner's read grant.
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        if (readerSid is not null)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(readerSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(security);
        ValidateProtectedDirectory(directory);
    }

    internal static void ValidateStagingDirectory(string path)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ValheimPerformanceCompanion");
        for (DirectoryInfo? part = new(path); part is not null && part.FullName.Length >= root.Length; part = part.Parent)
            ValidateProtectedDirectory(part);
    }

    internal static NamedPipeServerStream CreatePipe()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:SYD:P(A;;GA;;;SY)(A;;GRGW;;;IU)", 1, out nint descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            SafePipeHandle handle = CreateNamedPipe(@"\\.\pipe\" + CollectorProtocol.PipeName, 3 | 0x40000000 | 0x00080000, 8, 1, 16384, 16384, 0, ref security);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    internal static void Disconnect(NamedPipeServerStream pipe) => pipe.Disconnect();

    internal static bool OwnerIsAlive(Caller owner)
    {
        try
        {
            using Process process = Process.GetProcessById(owner.Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.Started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // Never label a live owner abandoned when inspection is denied.
    }

    internal static Caller Authenticate(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using Process process = Process.GetProcessById(checked((int)pid));
        if (process.SessionId == 0) throw new UnauthorizedAccessException("An interactive desktop client is required.");
        using WindowsIdentity identity = Identity(process);
        string sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
        // A filtered administrator token still carries the deny-only Administrators SID.
        // Additional standard-user SIDs may be granted by an administrator in the protected installation file.
        bool authorized = SamePackageFamily(process) || identity.Groups?.Contains(administrators) == true;
        string grants = Path.Combine(AppContext.BaseDirectory, "collector-users.txt");
        if (!authorized && File.Exists(grants))
        {
            if (new FileInfo(grants).Length > 16384) throw new InvalidDataException("Collector authorization list exceeds its limit.");
            authorized = File.ReadLines(grants).Any(line => string.Equals(line.Trim(), sid, StringComparison.Ordinal));
        }
        if (!authorized) throw new UnauthorizedAccessException("This Windows account has not been authorized to collect system profiling traces.");
        string expected = Path.Combine(AppContext.BaseDirectory, "ValheimPerformanceCompanion.exe");
        if (!string.Equals(ProcessPath(process), expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The collector accepts only the installed companion application.");
        return new Caller(process.Id, process.StartTime.ToUniversalTime().Ticks, sid);
    }

    private static bool SamePackageFamily(Process client)
    {
        using Process service = Process.GetCurrentProcess();
        string? serviceFamily = PackageFamily(service);
        return serviceFamily is not null && string.Equals(serviceFamily, PackageFamily(client), StringComparison.Ordinal);
    }

    private static string? PackageFamily(Process process)
    {
        uint length = 0;
        int error = GetPackageFamilyName(process.SafeHandle, ref length, null);
        if (error == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (error != 122 || length is < 1 or > 1024) throw new Win32Exception(error);
        var name = new StringBuilder(checked((int)length));
        error = GetPackageFamilyName(process.SafeHandle, ref length, name);
        if (error != 0) throw new Win32Exception(error);
        return name.ToString();
    }

    internal static void ValidateGame(Caller caller, ServiceRequest request)
    {
        if (request.OwnerPid != caller.Pid) throw new UnauthorizedAccessException("Capture owner does not match the pipe client.");
        using Process game = Process.GetProcessById(request.GamePid);
        using WindowsIdentity identity = Identity(game);
        if (!string.Equals(identity.User?.Value, caller.Sid, StringComparison.Ordinal)
            || !string.Equals(game.ProcessName, "valheim", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The selected Valheim process must belong to the requesting Windows user.");
    }

    private static WindowsIdentity Identity(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, 8, out SafeAccessTokenHandle token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token) return new WindowsIdentity(token.DangerousGetHandle());
    }
    private static string ProcessPath(Process process)
    {
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(path.ToString());
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal nint Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder? family);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint instances, uint outputBytes, uint inputBytes, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
