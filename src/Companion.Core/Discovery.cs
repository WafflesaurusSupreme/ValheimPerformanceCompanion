using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Companion.Core;

public sealed record GameTarget(string GameDirectory, string? ProfileDirectory, string Evidence, int? ProcessId = null)
{
    public string DisplayName => (ProfileDirectory is null ? "Valheim · unmodded / profile unknown" : new DirectoryInfo(ProfileDirectory).Name)
        + (ProcessId is null ? " · game closed" : $" · PID {ProcessId}");
    public string BepInExRoot => Path.Combine(ProfileDirectory ?? GameDirectory, "BepInEx");
    public override string ToString() => DisplayName;
}
public sealed record DiscoveryResult(IReadOnlyList<GameTarget> Targets, IReadOnlyList<string> Notes);

public static partial class Discovery
{
    public static DiscoveryResult Find(IEnumerable<string>? extraRoots = null)
    {
        var notes = new List<string>(); var targets = new List<GameTarget>(); var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcessesByName("valheim"))
        {
            using (process)
            {
                try
                {
                    string? exe = process.MainModule?.FileName;
                    if (exe is null) continue;
                    string game = Path.GetDirectoryName(exe)!; games.Add(game);
                    string? command = ProcessCommandLine.Read(process);
                    string? profile = command is null ? null : ProfileFromArguments(SplitCommandLine(command));
                    targets.Add(new(game, profile, profile is null ? "Running process; profile not yet verified. Connect to the recorder to identify it." : "Verified from the running game's Doorstop target.", process.Id));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                { notes.Add($"Could not inspect game process {process.Id}: {ex.Message}"); }
            }
        }
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            string? steam = Registry.GetValue(key, key.StartsWith("HKEY_CURRENT", StringComparison.Ordinal) ? "SteamPath" : "InstallPath", null) as string;
            if (steam is not null) steamRoots.Add(steam);
        }
        steamRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (string root in steamRoots.ToArray())
        {
            string libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            try { foreach (string library in SteamLibraries(File.ReadAllText(libraries))) steamRoots.Add(library); }
            catch (IOException ex) { notes.Add("Steam library list: " + ex.Message); }
        }
        foreach (string library in steamRoots)
        {
            string manifest = Path.Combine(library, "steamapps", "appmanifest_892970.acf");
            string install = "Valheim";
            if (File.Exists(manifest))
            {
                Match m = InstallDirectory().Match(File.ReadAllText(manifest));
                if (m.Success && Path.GetFileName(m.Groups[1].Value) == m.Groups[1].Value) install = m.Groups[1].Value;
            }
            string game = Path.Combine(library, "steamapps", "common", install);
            if (IsGame(game)) games.Add(Path.GetFullPath(game));
        }
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] roots = new[] {
            Path.Combine(roaming, "Thunderstore Mod Manager", "DataFolder", "Valheim", "profiles"),
            Path.Combine(roaming, "r2modmanPlus-local", "Valheim", "profiles"),
            Path.Combine(roaming, "r2modmanPlus-local", "DataFolder", "Valheim", "profiles")
        }.Concat(extraRoots ?? []).ToArray();
        foreach (string root in roots)
        {
            if (IsGame(root)) games.Add(Path.GetFullPath(root));
            if (!Directory.Exists(root)) continue;
            string[] profiles;
            try { profiles = Directory.Exists(Path.Combine(root, "BepInEx")) ? [root] : Directory.GetDirectories(root); }
            catch (UnauthorizedAccessException ex) { notes.Add(ex.Message); continue; }
            foreach (string profile in profiles.Where(p => Directory.Exists(Path.Combine(p, "BepInEx"))))
            {
                if (targets.Any(t => string.Equals(t.ProfileDirectory, profile, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (string game in games)
                    targets.Add(new(game, profile, "Installed profile candidate; not verified active. A recorder handshake resolves the running profile."));
            }
        }
        foreach (string game in games)
            if (!targets.Any(t => t.GameDirectory == game && t.ProfileDirectory is null))
                targets.Add(new(game, Directory.Exists(Path.Combine(game, "BepInEx")) ? game : null, "Steam installation. Direct BepInEx presence is not proof of a running modded session."));
        if (targets.Count == 0) notes.Add("No installation found. Browse to valheim.exe or add a custom profile folder.");
        return new(targets.OrderByDescending(t => t.ProcessId.HasValue).ThenBy(t => t.DisplayName).ToArray(), notes);
    }
    public static bool IsGame(string path) => File.Exists(Path.Combine(path, "valheim.exe")) && Directory.Exists(Path.Combine(path, "valheim_Data"));
    public static IEnumerable<string> SteamLibraries(string text)
    {
        foreach (Match match in LibraryPath().Matches(text)) yield return match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
    }
    public static string? ProfileFromArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--doorstop-enable=false", StringComparison.OrdinalIgnoreCase)
                || args[i].Equals("--doorstop-enabled=false", StringComparison.OrdinalIgnoreCase)) return null;
            if ((args[i].Equals("--doorstop-enable", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--doorstop-enabled", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Length && args[i + 1].Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        }
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i]; string? value = null;
            foreach (string option in new[] { "--doorstop-target", "--doorstop-target-assembly" })
            {
                if (key.Equals(option, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) value = args[i + 1];
                if (key.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)) value = key[(option.Length + 1)..];
            }
            if (value is null) continue;
            string normalized = value.Replace('/', Path.DirectorySeparatorChar);
            int bep = normalized.LastIndexOf("\\BepInEx\\", StringComparison.OrdinalIgnoreCase);
            if (bep > 0 && Path.IsPathFullyQualified(normalized)) return Path.GetFullPath(normalized[..bep]);
        }
        return null;
    }
    public static string[] SplitCommandLine(string command)
    {
        nint argv = CommandLineToArgvW(command, out int count);
        if (argv == 0) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size)) ?? "").ToArray(); }
        finally { LocalFree(argv); }
    }
    [GeneratedRegex("\"(?:path|[0-9]+)\"\\s+\"([A-Za-z]:[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LibraryPath();
    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex InstallDirectory();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

internal static class ProcessCommandLine
{
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public nint Buffer; }
    public static string? Read(Process process)
    {
        nint handle = OpenProcess(0x1000, false, process.Id);
        if (handle == 0) return null;
        try
        {
            NtQueryInformationProcess(handle, 60, 0, 0, out int length);
            if (length <= 0 || length > 131072) return null;
            nint memory = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, 60, memory, length, out _) != 0) return null;
                var value = Marshal.PtrToStructure<UnicodeString>(memory);
                long offset = value.Buffer.ToInt64() - memory.ToInt64();
                return offset >= 0 && offset + value.Length <= length ? Marshal.PtrToStringUni(value.Buffer, value.Length / 2) : null;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint handle, int info, nint data, int length, out int returned);
}
