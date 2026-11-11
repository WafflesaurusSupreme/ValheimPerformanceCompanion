using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

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
    public static SetupResult Ensure(GameTarget target, string? bundleRoot = null, Func<bool>? gameRunning = null) => Ensure(target, bundleRoot, gameRunning, null);

    public static SetupResult Ensure(GameTarget target, string? bundleRoot, Func<bool>? gameRunning, Func<string, RecorderCapabilities>? inspect)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(target.BepInExRoot).ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\ValheimPerformanceCompanion.RecorderSetup." + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another recorder setup is in progress for this profile. Retry after it finishes.");
        try { return EnsureCore(target, bundleRoot, gameRunning, inspect); }
        finally { mutex.ReleaseMutex(); }
    }

    private static SetupResult EnsureCore(GameTarget target, string? bundleRoot, Func<bool>? gameRunning, Func<string, RecorderCapabilities>? inspect)
    {
        gameRunning ??= GameRunning;
        inspect ??= RecorderCapabilities.Read;
        string source = Path.Combine(bundleRoot ?? AppContext.BaseDirectory, "Recorder", FileName);
        if (!File.Exists(source)) throw new FileNotFoundException("The portable package is incomplete. Extract the entire ZIP, including its Recorder folder, beside the app.", source);
        string root = Path.GetFullPath(target.BepInExRoot);
        if (!File.Exists(Path.Combine(root, "core", "BepInEx.dll")))
            return new(false, false, "This location has no BepInEx. Select a mod profile with BepInEx installed.");
        string folder = Path.Combine(root, "plugins", "ValheimPerformanceCompanion");
        string destination = Path.Combine(folder, FileName);
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recorder setup cannot replace a linked file. The existing recorder was preserved.");
        byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        byte[] expected = Hash(source);
        string? before = File.Exists(destination) ? Convert.ToHexString(Hash(destination)) : null;
        if (before == Convert.ToHexString(expected))
            return new(true, false, "Recorder ready. Waiting for Valheim; checking for its PID every 30 seconds.");
        if (gameRunning()) return new(false, true, "Action needed: Close Valheim once and keep this app open. Recorder setup will finish automatically. Wait for 'Recorder ready', then relaunch your profile.");
        RecorderCapabilities bundled = inspect(source);
        RecorderCapabilities? installed = before is null ? null : inspect(destination);
        if (bundled.SHA256 != Convert.ToHexString(expected) || installed is not null && installed.SHA256 != before)
            throw new IOException("Recorder identity changed during capability inspection. Existing files were preserved.");
        if (!bundled.ParticleCensus || !bundled.DisableCleanup || !bundled.EarlyCommandValidation)
            throw new InvalidDataException("The bundled recorder lacks required census/lifecycle capabilities. The installed recorder was preserved.");
        if (installed is not null && (bundled.Version <= installed.Version || !bundled.Preserves(installed)))
            return new(false, false, "The installed recorder has a newer/equal revision or capabilities absent from this bundle. It was preserved; use a Companion update that includes those fixes.");
        for (DirectoryInfo? part = new(folder); part is not null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recorder setup cannot write through a linked directory. Choose the profile's actual folder.");
        Directory.CreateDirectory(folder);
        string stage = destination + ".stage-" + Guid.NewGuid().ToString("N");
        File.Copy(source, stage);
        if (!expected.SequenceEqual(Hash(stage))) throw new IOException("Recorder copy integrity check failed.");
        if (gameRunning()) return new(false, true, "Valheim started during setup. Close it once; setup will retry automatically. The staged copy is retained.");
        string? current = File.Exists(destination) ? Convert.ToHexString(Hash(destination)) : null;
        if (current != before) throw new IOException("Recorder changed during setup. The new preimage and staged copy were preserved; retry after reviewing the other updater.");
        string transaction = Guid.NewGuid().ToString("N");
        string receipts = Path.Combine(root, "PerformanceCompanion", "SetupTransactions");
        foreach (string protectedPath in new[] { receipts, Path.Combine(root, "PerformanceCompanion", "RecorderBackups") })
            for (DirectoryInfo? part = new(protectedPath); part is not null; part = part.Parent)
                if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recorder transaction paths cannot use linked directories.");
        Directory.CreateDirectory(receipts);
        string receipt = Path.Combine(receipts, transaction + ".json");
        string? backupPath = before is null ? null : Path.Combine(root, "PerformanceCompanion", "RecorderBackups", transaction + ".dll");
        void WriteReceipt(string state)
        {
            string temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new {
            Transaction = transaction, State = state, Utc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
            Application = Environment.ProcessPath, AppVersion = typeof(RecorderSetup).Assembly.GetName().Version?.ToString(),
            target.GameDirectory, target.ProfileDirectory, Source = source, Destination = destination,
            SourceSHA256 = Convert.ToHexString(expected), PreviousSHA256 = before, Backup = backupPath,
            SourceCapabilities = bundled, PreviousCapabilities = installed
            }));
            File.Move(temporary, receipt, true);
        }
        WriteReceipt("Prepared");
        if (File.Exists(destination))
        {
            string backup = Path.Combine(root, "PerformanceCompanion", "RecorderBackups");
            Directory.CreateDirectory(backup);
            File.Replace(stage, destination, backupPath);
        }
        else File.Move(stage, destination);
        if (!expected.SequenceEqual(Hash(destination))) { WriteReceipt("VerificationFailed"); throw new IOException("Installed recorder hash verification failed; inspect its setup transaction and preserved backup."); }
        WriteReceipt("InstalledAndVerified");
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
