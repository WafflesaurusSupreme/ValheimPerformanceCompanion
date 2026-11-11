using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Companion.App;

public static class CollectorSetup
{
    public const string ServiceName = "ValheimPerformanceCompanionCollector";
    public const string Explanation = "Standard and Deep recording use a Windows collector service to run the bundled Microsoft PerfView tool with system profiling permissions. The companion downloads the matching release from its official GitHub repository and checks its SHA-256 before installation. Windows asks for administrator approval once. System tracing can include activity from other applications; raw traces may contain sensitive paths or process details. Recordings stay on this computer unless you export and share them. If you decline setup, Monitor recording and opening/comparing existing recordings remain available.";
    private const string Repository = "https://api.github.com/repos/WafflesaurusSupreme/ValheimPerformanceCompanion";
    private static readonly HttpClient client = CreateClient();
    private static string Version => typeof(CollectorSetup).Assembly.GetName().Version!.ToString(3);
    private sealed record ReleaseAsset(string Url, string Hash);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimPerformanceCompanion/1.0");
        return http;
    }

    public static bool IsInstalled()
    {
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenService(manager, ServiceName, 4);
            if (service != 0) { CloseServiceHandle(service); return true; }
            int error = Marshal.GetLastWin32Error();
            if (error == 1060) return false;
            throw new Win32Exception(error);
        }
        finally { CloseServiceHandle(manager); }
    }

    public static async Task CheckAtStartupAsync(Action<bool> showState, Func<Task> setup, Func<bool>? detect = null)
    {
        bool installed = await Task.Run(detect ?? IsInstalled);
        showState(installed);
        if (!installed) await setup();
    }

    private static async Task<ReleaseAsset> GetAssetAsync(CancellationToken cancellation)
    {
        using var response = await client.GetAsync(Repository + "/releases/tags/v" + Version + "-preview.1", cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("Release metadata exceeds its limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var metadata = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
        {
            if (metadata.Length + count > 1024 * 1024) throw new InvalidDataException("Release metadata exceeds its limit.");
            metadata.Write(buffer, 0, count);
        }
        var parsed = ParseRelease(metadata.ToArray());
        return new(parsed.Url, parsed.Hash);
    }

    public static (string Url, string Hash) ParseRelease(byte[] metadata)
    {
        if (metadata.Length > 1024 * 1024) throw new InvalidDataException("Release metadata exceeds its limit.");
        using var json = JsonDocument.Parse(metadata);
        var matches = json.RootElement.GetProperty("assets").EnumerateArray().Where(a =>
            a.GetProperty("name").GetString() == $"ValheimPerformanceCompanion-{Version}-win-x64.zip").ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The matching collector download is not available yet. Try setup again later.");
        string url = matches[0].GetProperty("browser_download_url").GetString()!;
        string digest = matches[0].GetProperty("digest").GetString() ?? "";
        if (url != $"https://github.com/WafflesaurusSupreme/ValheimPerformanceCompanion/releases/download/v{Version}-preview.1/ValheimPerformanceCompanion-{Version}-win-x64.zip"
            || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("The official collector download has no valid SHA-256 identity.");
        return (url, digest[7..]);
    }

    public static async Task<string> DownloadAndInstallAsync(IProgress<string> progress, CancellationToken cancellation = default)
    {
        progress.Report("Finding the matching collector download…");
        ReleaseAsset asset = await GetAssetAsync(cancellation);
        string setupId = Guid.NewGuid().ToString("N");
        string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimPerformanceCompanion", "Setup", setupId);
        Directory.CreateDirectory(work);
        string archive = Path.Combine(work, "release.zip");
        progress.Report("Downloading recording support from GitHub…");
        using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 256L * 1024 * 1024) throw new InvalidDataException("Collector download exceeds 256 MB.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920]; long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
            {
                total += count;
                if (total > 256L * 1024 * 1024) throw new InvalidDataException("Collector download exceeds 256 MB.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            }
        }
        await VerifyHashAsync(archive, asset.Hash, cancellation);
        string receipt = Path.Combine(InstallDirectory(setupId), "setup-result.json");
        progress.Report("Windows will ask for administrator approval to install recording support…");
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Cannot locate the running companion."))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "--install-collector", archive, setupId, WindowsIdentity.GetCurrent().User!.Value }) start.ArgumentList.Add(argument);
        using Process installer = Process.Start(start) ?? throw new IOException("Collector setup did not start.");
        using var installTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        installTimeout.CancelAfter(TimeSpan.FromMinutes(8));
        await installer.WaitForExitAsync(installTimeout.Token);
        if (!File.Exists(receipt)) throw new IOException("Collector setup did not return a result. Your recordings are unchanged.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, cancellation));
        if (installer.ExitCode != 0 || !result.RootElement.GetProperty("Success").GetBoolean())
            throw new IOException(result.RootElement.GetProperty("Error").GetString() ?? "Collector installation failed.");
        string application = result.RootElement.GetProperty("Application").GetString()!;
        string expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ValheimPerformanceCompanion") + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(application).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(application))
            throw new IOException("Collector setup returned an invalid application location.");
        return application;
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Collector download failed SHA-256 verification. Setup was stopped.");
    }

    private static string InstallDirectory(string setupId) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ValheimPerformanceCompanion", Version + "-" + setupId);

    public static async Task<int> InstallAsync(string archive, string setupId, string userSid)
    {
        if (!Guid.TryParseExact(setupId, "N", out _)) return 1;
        string destination = InstallDirectory(setupId);
        string receipt = Path.Combine(destination, "setup-result.json");
        string? application = null; string? error = null; bool createdService = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator approval is required.");
            _ = new SecurityIdentifier(userSid);
            if (IsInstalled()) throw new IOException("The collector is already installed. Restart the companion to use it.");
            // Re-fetch the official digest in the elevated process; never trust a caller-supplied hash.
            ReleaseAsset asset = await GetAssetAsync(CancellationToken.None);
            await using var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (locked.Length > 256L * 1024 * 1024) throw new InvalidDataException("Collector download exceeds 256 MB.");
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(locked));
            if (!hash.Equals(asset.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release SHA-256 verification failed.");
            locked.Position = 0;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ValheimPerformanceCompanion");
            ValidateProtected(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            if (Directory.Exists(root)) ValidateProtected(root); else CreateProtected(root);
            if (Directory.Exists(destination)) throw new IOException("Collector setup destination already exists.");
            using var zip = new ZipArchive(locked, ZipArchiveMode.Read, true);
            ValidateArchive(zip, destination);
            CreateProtected(destination);
            foreach (var entry in zip.Entries)
            {
                string relative = entry.FullName.Replace('\\', '/')["ValheimPerformanceCompanion/".Length..];
                if (relative.Length == 0) continue;
                string target = Path.GetFullPath(Path.Combine(destination, relative));
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output);
            }
            await File.WriteAllTextAsync(Path.Combine(destination, "collector-users.txt"), userSid + "\n");
            ProtectTree(destination);
            application = Path.Combine(destination, "ValheimPerformanceCompanion.exe");
            if (FileVersionInfo.GetVersionInfo(application).FileVersion != Version + ".0") throw new InvalidDataException("Downloaded application version is incorrect.");
            await ServiceCommandAsync("create", ServiceName, "binPath=", "\"" + Path.Combine(destination, "VpcCollectorService.exe") + "\"", "start=", "auto", "obj=", "LocalSystem");
            createdService = true;
            await ServiceCommandAsync("start", ServiceName);
            await Task.Delay(2000);
            await ServiceCommandAsync("query", ServiceName);
            if (!IsRunning()) throw new IOException("The collector service did not remain running after installation.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (createdService)
            {
                try { await ServiceCommandAsync("stop", ServiceName); } catch (Exception stopError) { error += " Stop: " + stopError.Message; }
                try { await ServiceCommandAsync("delete", ServiceName); } catch (Exception removeError) { error += " Rollback: " + removeError.Message; }
            }
        }
        if (!Directory.Exists(destination)) return 1;
        string temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { Success = error is null, Application = application, Error = error }));
        var receiptInfo = new FileInfo(temporary); var receiptAcl = receiptInfo.GetAccessControl(); receiptAcl.SetOwner(new SecurityIdentifier("S-1-5-32-544")); receiptInfo.SetAccessControl(receiptAcl);
        File.Move(temporary, receipt, false);
        return error is null ? 0 : 1;
    }

    public static void ValidateArchive(ZipArchive archive, string destination)
    {
        string boundary = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        if (archive.Entries.Count > 5000) throw new InvalidDataException("Excessive release files.");
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            const string prefix = "ValheimPerformanceCompanion/";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release root.");
            string relative = name[prefix.Length..]; if (relative.Length == 0) continue;
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !seen.Add(target) || relative.Contains(':'))
                throw new InvalidDataException("Unsafe or duplicate release path.");
            total += entry.Length;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Release exceeds its extraction limit.");
        }
    }

    private static void CreateProtected(string path)
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544")); security.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-32-545" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), sid.EndsWith("545", StringComparison.Ordinal) ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
    }

    private static void ValidateProtected(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Collector installation cannot use linked directories.");
            var security = directory.GetAccessControl();
            string owner = security.GetOwner(typeof(SecurityIdentifier))!.Value;
            string[] trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
            if (!trusted.Contains(owner)) throw new UnauthorizedAccessException("Collector installation folder has an untrusted owner.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !trusted.Contains(rule.IdentityReference.Value)
                    && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    && (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                    throw new UnauthorizedAccessException("Collector installation folder permits unprivileged writes.");
            if (directory.FullName.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void ProtectTree(string destination)
    {
        var administrators = new SecurityIdentifier("S-1-5-32-544");
        foreach (string path in Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(path)) { var info = new DirectoryInfo(path); var acl = info.GetAccessControl(); acl.SetOwner(administrators); info.SetAccessControl(acl); }
            else { var info = new FileInfo(path); var acl = info.GetAccessControl(); acl.SetOwner(administrators); info.SetAccessControl(acl); }
        }
    }

    private static async Task ServiceCommandAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start service control.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await process.WaitForExitAsync(timeout.Token);
        string details = await output + await error;
        if (process.ExitCode != 0) throw new IOException("Collector service control failed: " + details);
    }

    private static bool IsRunning()
    {
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenService(manager, ServiceName, 4);
            if (service == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error()); return status.State == 4; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
}
