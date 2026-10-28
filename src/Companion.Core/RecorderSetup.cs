using System.Diagnostics;
using System.Security.Cryptography;

namespace Companion.Core;

public sealed record SetupResult(bool Ready, bool WaitingForExit, string Message);

public static class RecorderSetup
{
    public const string FileName = "ValheimPerformanceCompanion.Recorder.dll";
    public static bool GameRunning()
    {
        Process[] games = Process.GetProcessesByName("valheim");
        try { return games.Length > 0; }
        finally { foreach (Process game in games) game.Dispose(); }
    }
    public static SetupResult Ensure(GameTarget target, string? bundleRoot = null, Func<bool>? gameRunning = null)
    {
        gameRunning ??= GameRunning;
        string source = Path.Combine(bundleRoot ?? AppContext.BaseDirectory, "Recorder", FileName);
        if (!File.Exists(source)) throw new FileNotFoundException("The portable package is incomplete. Extract the entire ZIP, including its Recorder folder, beside the app.", source);
        string root = Path.GetFullPath(target.BepInExRoot);
        if (!File.Exists(Path.Combine(root, "core", "BepInEx.dll")))
            return new(false, false, "This location has no BepInEx. Select a mod profile with BepInEx installed.");
        string folder = Path.Combine(root, "plugins", "ValheimPerformanceCompanion");
        string destination = Path.Combine(folder, FileName);
        byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        byte[] expected = Hash(source);
        if (File.Exists(destination) && expected.SequenceEqual(Hash(destination)))
            return new(true, false, "Recorder ready. Waiting for Valheim; checking for its PID every 30 seconds.");
        if (gameRunning()) return new(false, true, "Action needed: Close Valheim once and keep this app open. Recorder setup will finish automatically. Wait for 'Recorder ready', then relaunch your profile.");
        for (DirectoryInfo? part = new(folder); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recorder setup cannot write through a linked directory. Choose the profile's actual folder.");
        Directory.CreateDirectory(folder);
        string stage = destination + ".stage-" + Guid.NewGuid().ToString("N");
        File.Copy(source, stage);
        if (!expected.SequenceEqual(Hash(stage))) throw new IOException("Recorder copy integrity check failed.");
        if (gameRunning()) return new(false, true, "Valheim started during setup. Close it once; setup will retry automatically. The staged copy is retained.");
        if (File.Exists(destination))
        {
            string backup = Path.Combine(root, "PerformanceCompanion", "RecorderBackups");
            Directory.CreateDirectory(backup);
            File.Replace(stage, destination, Path.Combine(backup, Guid.NewGuid().ToString("N") + ".dll"));
        }
        else File.Move(stage, destination);
        return new(true, false, "Recorder ready · Setup finished automatically. Launch Valheim through your profile. Searching for its PID every 30 seconds; click Check now to search sooner.");
    }
}

public static class TargetSelection
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    public static bool Same(GameTarget a, GameTarget b) =>
        string.Equals(Path.GetFullPath(a.GameDirectory).TrimEnd('\\'), Path.GetFullPath(b.GameDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ProfileDirectory?.TrimEnd('\\'), b.ProfileDirectory?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static GameTarget? Choose(IReadOnlyList<GameTarget> targets, GameTarget? preferred, bool followDetected = false)
    {
        if (followDetected)
        {
            GameTarget[] running = targets.Where(t => t.ProcessId.HasValue).ToArray();
            if (running.Length == 1) return running[0];
        }
        if (preferred is not null)
            return targets.Where(t => Same(t, preferred)).OrderByDescending(t => t.ProcessId.HasValue).FirstOrDefault()
                ?? preferred with { ProcessId = null, Evidence = "Selected profile retained; waiting for its game process." };
        return targets.FirstOrDefault(t => t.ProcessId.HasValue) ?? (targets.Count == 1 ? targets[0] : null);
    }
}
