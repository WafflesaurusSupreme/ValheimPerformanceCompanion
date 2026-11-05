using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using Companion.Collector;

namespace Companion.Core;

internal sealed class CollectorServiceException(string message, string state) : IOException(message)
{
    internal string State { get; } = state;
}

internal static class CollectorServiceClient
{
    internal static async Task<ServiceReply> SendAsync(CollectorRequest request, string command)
    {
        var message = new ServiceRequest(1, command, request.SessionId, request.GamePid, request.OwnerPid, request.Seconds, request.Mode);
        CollectorProtocol.Validate(message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command == "stop" ? 155 : 80));
        using var pipe = new NamedPipeClientStream(".", CollectorProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(5000, timeout.Token).ConfigureAwait(false); }
        catch (TimeoutException error)
        { throw new IOException("The installed profiling collector is unavailable. Repair the application installation.", error); }
        VerifyServer(pipe);
        await CollectorProtocol.WriteAsync(pipe, message, timeout.Token).ConfigureAwait(false);
        ServiceReply reply = await CollectorProtocol.ReadReplyAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (!reply.Success) throw new CollectorServiceException(reply.Message, reply.State);
        return reply;
    }

    private static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenService(manager, CollectorProtocol.ServiceName, 1 | 4);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!QueryServiceStatusEx(service, 0, out ServiceStatus status, Marshal.SizeOf<ServiceStatus>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.State != 4 || status.ProcessId != pid || status.Type != 0x10)
                    throw new UnauthorizedAccessException("The pipe does not belong to the running profiling service.");
                QueryServiceConfig(service, 0, 0, out uint bytes);
                int error = Marshal.GetLastWin32Error();
                if (error != 122 || bytes is < 1 or > 65536) throw new Win32Exception(error);
                nint buffer = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    if (!QueryServiceConfig(service, buffer, bytes, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    ServiceConfiguration configuration = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
                    string binary = (Marshal.PtrToStringUni(configuration.BinaryPath) ?? "").Trim('"');
                    string account = Marshal.PtrToStringUni(configuration.Account) ?? "";
                    string expected = Path.Combine(AppContext.BaseDirectory, "VpcCollectorService.exe");
                    if (!string.Equals(binary, expected, StringComparison.OrdinalIgnoreCase)
                        || !(account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)))
                        throw new UnauthorizedAccessException("The profiling service configuration belongs to a different installation or account.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    internal static async Task CopyResultsAsync(CollectorRequest request, string? source)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string expected = CollectorProtocol.SessionDirectory(identity.User?.Value ?? throw new UnauthorizedAccessException(), request.SessionId);
        if (source is null || !string.Equals(Path.GetFullPath(source), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The collector returned an unexpected trace directory.");
        var directory = new DirectoryInfo(source);
        for (DirectoryInfo? part = directory; part is not null; part = part.Parent)
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Collector trace paths cannot be linked directories.");
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Collector output cannot contain linked files.");
            bool trace = file.Name.StartsWith("cpu.etl", StringComparison.Ordinal) && (file.Extension is ".etl" or ".zip");
            bool diagnostic = file.Name.StartsWith("perfview-", StringComparison.Ordinal) && file.Extension == ".log"
                || file.Name is "collector-ready.json" or "collector-end.json" or "collector-result.json" or "collector-quality.txt" or "collector-cleanup-error.txt";
            if (!trace && !diagnostic) continue;
            string destination = Path.Combine(request.Directory, file.Name);
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An existing capture output is a linked file.");
                await using var previous = File.OpenRead(destination);
                await using var expectedFile = File.OpenRead(file.FullName);
                byte[] oldHash = await SHA256.HashDataAsync(previous).ConfigureAwait(false);
                byte[] expectedHash = await SHA256.HashDataAsync(expectedFile).ConfigureAwait(false);
                if (!oldHash.AsSpan().SequenceEqual(expectedHash)) throw new IOException("Existing capture output differs from the service trace; both copies were preserved.");
                continue;
            }
            // The desktop user's token performs every write to a user-selected game/profile folder.
            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            string stage = destination + ".copy-" + Guid.NewGuid().ToString("N");
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await input.CopyToAsync(output).ConfigureAwait(false);
            File.Move(stage, destination);
        }
    }

    internal static string StagingDirectory(string sessionId)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return CollectorProtocol.SessionDirectory(identity.User?.Value ?? throw new UnauthorizedAccessException(), sessionId);
    }

    internal static async Task<bool> RecoverCompletedAsync(CollectorRequest request)
    {
        string source = StagingDirectory(request.SessionId);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, "collector-result.json"))) return false;
        var metadata = CollectorStaging.ReadReceipt<CollectorRequest>(Path.Combine(source, "collector-request.json"));
        CollectorStaging.ValidateBinding(metadata, request, source);
        CollectorStaging.ReadReceipt<CollectorResult>(Path.Combine(source, "collector-result.json"));
        await CopyResultsAsync(request, source).ConfigureAwait(false);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfiguration { internal uint Type, Start, Error; internal nint BinaryPath, LoadGroup; internal uint Tag; internal nint Dependencies, Account, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, int bytes, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(nint service, nint config, uint bytes, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint service);
}
