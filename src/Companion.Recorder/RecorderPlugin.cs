using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;
using Companion.Transport;

namespace ValheimPerformanceCompanion.Recorder;

[BepInPlugin(Id, "Performance Companion Recorder", "0.1.10")]
public sealed class RecorderPlugin : BaseUnityPlugin
{
    public const string Id = "tools.valheim.performancecompanion";
    private static RecorderPlugin? instance;
    private static Terminal.ConsoleCommand? ownedCommand;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly ConcurrentQueue<Request> requests = new ConcurrentQueue<Request>();
    private readonly ConcurrentQueue<string> consoleMessages = new ConcurrentQueue<string>();
    private readonly Harmony harmony = new Harmony(Id);
    private Task? server;
    private Recording? recording;
    private Task? saving;
    private string lastDirectory = "", lastSession = "", lastError = "";
    private int pid;
    private bool legacy;
    private string statusJson = "";
    private long statusPublishedAt;
    private int statusRefreshRequested;
    private int pendingMutations;
    private sealed class Request
    {
        internal string Command = "";
        internal long Deadline;
        internal bool Mutation;
        internal TaskCompletionSource<string> Result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void Awake()
    {
        instance = this;
        pid = Process.GetCurrentProcess().Id;
        legacy = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("RiverheimCascadesLoader.FrameTraceCapture", false) != null);
        harmony.CreateClassProcessor(typeof(ConsoleHook)).Patch();
        RegisterCommands();
        PublishStatus();
        server = Task.Run(ServerLoop);
        Logger.LogInfo("Performance Companion ready. The companion can connect before or after Valheim starts. Console: perf start [monitor|standard|deep], perf status, perf stop. Lag spikes are detected automatically.");
    }
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    private static class ConsoleHook { private static void Postfix() => RegisterCommands(); }
    private static void RegisterCommands()
    {
        var commands = AccessTools.Field(typeof(Terminal), "commands")?.GetValue(null) as System.Collections.IDictionary;
        if (commands == null) { instance?.Logger.LogWarning("Valheim console command registry is unavailable."); return; }
        if (commands.Contains("perf")) return;
        ownedCommand = new Terminal.ConsoleCommand("perf", "start [monitor|standard|deep] | stop | status — control Performance Companion", args =>
        {
            if (instance == null) return;
            string command = string.Join(" ", args.Args.Skip(1));
            if (command.Length == 0) command = "status";
            instance.SendConsole(command);
            args.Context.AddString("Performance Companion: request sent.");
        });
    }
    private void SendConsole(string command)
    {
        if (command.Length > 512) { consoleMessages.Enqueue("Command is too long."); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "ValheimPerformanceCompanion.Controller." + pid, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); timeout.CancelAfter(180000);
                using var close = timeout.Token.Register(() => pipe.Dispose());
                await pipe.ConnectAsync(1500, timeout.Token).ConfigureAwait(false);
                WindowsPipe.VerifyServer(pipe);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                await writer.WriteLineAsync(command).ConfigureAwait(false);
                consoleMessages.Enqueue(await ReadLine(reader, 4096, timeout.Token).ConfigureAwait(false));
            }
            catch (Exception ex) { consoleMessages.Enqueue("Open Performance Companion and connect to this game. " + ex.Message); }
        });
    }
    private async Task ServerLoop()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = WindowsPipe.CreateServer("ValheimPerformanceCompanion.Recorder." + pid);
                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); timeout.CancelAfter(12000);
                using var close = timeout.Token.Register(() => pipe.Dispose());
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                string command = await ReadLine(reader, 2048, timeout.Token).ConfigureAwait(false);
                bool readOnly = command == "hello" || command == "status";
                if (readOnly && Stopwatch.GetTimestamp() - Volatile.Read(ref statusPublishedAt) > Stopwatch.Frequency)
                {
                    Interlocked.Exchange(ref statusRefreshRequested, 1);
                    await writer.WriteLineAsync(ReadCachedStatus()).ConfigureAwait(false);
                    continue;
                }
                var request = new Request { Command = command, Deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency, Mutation = !readOnly };
                if (request.Mutation) Interlocked.Increment(ref pendingMutations);
                requests.Enqueue(request);
                using var cancel = timeout.Token.Register(() => request.Result.TrySetCanceled());
                if (readOnly)
                {
                    // Prefer a fresh game-thread response. When Unity stops pumping frames,
                    // answer from its last immutable snapshot without touching Unity objects.
                    if (await Task.WhenAny(request.Result.Task, Task.Delay(250, timeout.Token)).ConfigureAwait(false) != request.Result.Task)
                    {
                        request.Result.TrySetCanceled();
                        Interlocked.Exchange(ref statusRefreshRequested, 1);
                        await writer.WriteLineAsync(ReadCachedStatus()).ConfigureAwait(false);
                        continue;
                    }
                }
                await writer.WriteLineAsync(await request.Result.Task.ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException)
            { if (!shutdown.IsCancellationRequested) consoleMessages.Enqueue("Recorder connection ended: " + ex.Message); }
            catch (Exception ex)
            {
                lastError = "Recorder control server failed: " + ex;
                Logger.LogError(lastError);
                consoleMessages.Enqueue("Recorder communication failed. See BepInEx/LogOutput.log for the exact error.");
                return;
            }
        }
    }
    private static async Task<string> ReadLine(StreamReader reader, int maximum, CancellationToken cancellation)
    {
        var value = new StringBuilder(); var one = new char[1];
        while (value.Length < maximum)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = await reader.ReadAsync(one, 0, 1).ConfigureAwait(false);
            if (count == 0) throw new IOException("Incomplete control message.");
            if (one[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(one[0]);
        }
        throw new IOException("Control message too large.");
    }
    private void Update()
    {
        try { UpdateRecorder(); }
        finally { if (Interlocked.Exchange(ref statusRefreshRequested, 0) != 0) PublishStatus(); }
    }
    private void UpdateRecorder()
    {
        while (consoleMessages.TryDequeue(out string message))
        { Logger.LogInfo(message); if (Console.instance != null) Console.instance.AddString("Performance Companion: " + message); }
        if (saving?.IsCompleted == true)
        {
            if (saving.IsFaulted) { lastError = saving.Exception!.GetBaseException().Message; Logger.LogError(lastError); }
            saving = null;
            Interlocked.Exchange(ref statusRefreshRequested, 1);
        }
        if (requests.TryDequeue(out Request request))
        {
            try
            {
                if (Stopwatch.GetTimestamp() < request.Deadline && !request.Result.Task.IsCompleted)
                {
                    string error = "";
                    try { Execute(request.Command); }
                    catch (Exception ex) { error = ex.Message; Logger.LogWarning(error); }
                    PublishStatus();
                    request.Result.TrySetResult(error.Length == 0 ? Volatile.Read(ref statusJson) : Json(Status(error)));
                }
                else request.Result.TrySetCanceled();
            }
            finally { if (request.Mutation) Interlocked.Decrement(ref pendingMutations); }
        }
        Recording? active = recording;
        if (active != null)
        {
            try
            {
                if (!active.Started)
                {
                    if (active.PreparationExpired) { lastError = "Capture preparation timed out. Start a new capture and approve the collector prompt."; Stop(); }
                    return;
                }
                active.Sample();
                if (active.Elapsed >= active.Duration || active.WriterError.Length > 0) Stop();
            }
            catch (Exception ex) { lastError = "Sampling stopped: " + ex.Message; Stop(); }
        }
    }
    private void Execute(string command)
    {
        string[] parts = command.Split('\t');
        switch (parts[0])
        {
            case "hello": case "status": return;
            case "start": case "prepare":
                legacy = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("RiverheimCascadesLoader.FrameTraceCapture", false) != null);
                if (parts.Length != 4 || !Guid.TryParseExact(parts[1], "N", out _)) throw new ArgumentException("Invalid capture request.");
                if (recording != null || saving != null) throw new InvalidOperationException("A recording is already active or saving.");
                if (parts[2] != "Monitor" && parts[2] != "Standard" && parts[2] != "Deep") throw new ArgumentException("Unknown mode.");
                if (!int.TryParse(parts[3], out int seconds) || seconds < 5 || seconds > 600) throw new ArgumentException("Duration must be 5–600 seconds.");
                if (parts[2] == "Deep" && legacy) Logger.LogInfo("Legacy recorder detected. Companion Deep uses its own probe owner; existing instrumentation may add measurement overhead.");
                lastError = ""; lastSession = parts[1];
                lastDirectory = Path.Combine(Paths.BepInExRootPath, "PerformanceCompanion", "Captures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + parts[1]);
                recording = new Recording(lastDirectory, parts[1], parts[2], seconds, Logger);
                if (parts[0] == "start") recording.Begin();
                break;
            case "begin":
                if (parts.Length != 2 || parts[1] != lastSession || recording == null) throw new InvalidOperationException("Prepared session is unavailable. Start a new capture.");
                recording.Begin();
                break;
            case "stop":
                if (parts.Length != 2 || parts[1] != lastSession) throw new InvalidOperationException("Session identity mismatch.");
                Stop(); break;
            default: throw new ArgumentException("Unknown recorder command.");
        }
    }
    private void Stop()
    {
        Recording? active = recording;
        if (active == null) return;
        recording = null;
        try { active.EndOnGameThread(); }
        catch (Exception ex) { lastError = "Recorder cleanup failed: " + ex.Message; Logger.LogError(lastError); }
        saving = Task.Run(active.Save);
        PublishStatus();
    }
    private void PublishStatus()
    {
        Volatile.Write(ref statusJson, Json(Status("")));
        Volatile.Write(ref statusPublishedAt, Stopwatch.GetTimestamp());
    }
    private string ReadCachedStatus()
    {
        // An old Idle snapshot must never make recovery discard ownership of a
        // Start/Prepare/Begin/Stop that the game thread has not reconciled yet.
        if (Volatile.Read(ref pendingMutations) != 0)
            return Json(new StatusData { Protocol = 1, ProcessId = pid,
                Error = "A recorder command is awaiting the game thread. Bring Valheim forward and retry when it responds." });
        return Volatile.Read(ref statusJson);
    }
    private object Status(string error) => new StatusData {
        Protocol = 1, ProcessId = pid, GameDirectory = Paths.GameRootPath, BepInExRoot = Paths.BepInExRootPath,
        SessionId = lastSession, Directory = lastDirectory, State = recording != null ? (recording.Started ? "Recording" : "Prepared") : saving != null ? "Saving" : "Idle",
        Error = error, LastError = lastError, Frames = recording?.Count ?? 0, DroppedFrames = recording?.Dropped ?? 0,
        LastMs = recording?.LastMs ?? 0, ElapsedSeconds = recording?.Elapsed ?? 0, LegacyRecorder = legacy, SupportsPreparation = true };
    public sealed class StatusData
    {
        public int Protocol { get; set; } public int ProcessId { get; set; }
        public string GameDirectory { get; set; } = ""; public string BepInExRoot { get; set; } = "";
        public string SessionId { get; set; } = ""; public string Directory { get; set; } = "";
        public string State { get; set; } = ""; public string Error { get; set; } = ""; public string LastError { get; set; } = "";
        public long Frames { get; set; } public long DroppedFrames { get; set; }
        public double LastMs { get; set; } public double ElapsedSeconds { get; set; } public bool LegacyRecorder { get; set; }
        public bool SupportsPreparation { get; set; }
    }
    internal static string Json(object value)
    {
        using var buffer = new MemoryStream(); new DataContractJsonSerializer(value.GetType()).WriteObject(buffer, value);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private void OnDestroy()
    {
        shutdown.Cancel(); Stop();
        try { saving?.Wait(TimeSpan.FromSeconds(3)); server?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException ex) { Logger.LogWarning("Shutdown: " + ex.GetBaseException().Message); }
        harmony.UnpatchSelf();
        var commands = AccessTools.Field(typeof(Terminal), "commands")?.GetValue(null) as System.Collections.IDictionary;
        if (commands != null && commands.Contains("perf") && ReferenceEquals(commands["perf"], ownedCommand)) commands.Remove("perf");
        ownedCommand = null; instance = null;
    }
}

internal sealed class Recording
{
    private readonly string directory, id, mode;
    private readonly string configDirectory = Paths.ConfigPath;
    private long start;
    private DateTime utc;
    private readonly long preparedAt = Stopwatch.GetTimestamp();
    private readonly Receipt receipt;
    private readonly BlockingCollection<SampleData> queue = new BlockingCollection<SampleData>(8192);
    private readonly Task writer;
    private readonly DetailedTrace? details;
    private ProfilerRecorder cpu, gpu;
    private long previous, dropped;
    private double samplerTotalMs, samplerMaximumMs;
    private string writerError = "";
    public int Count { get; private set; }
    public int Duration { get; }
    public bool Deep => mode == "Deep";
    public double LastMs { get; private set; }
    public long Dropped => Interlocked.Read(ref dropped);
    public bool Started { get; private set; }
    public bool PreparationExpired => !Started && (Stopwatch.GetTimestamp() - preparedAt) / (double)Stopwatch.Frequency >= 180;
    public double Elapsed => Started ? (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency : 0;
    public string WriterError => Volatile.Read(ref writerError);
    private struct SampleData
    {
        internal long Stamp; internal double Interval, Unity, Cpu, Gpu, SamplerMs;
        internal bool Focus, Player; internal int Gc, Biome;
    }
    internal Recording(string path, string sessionId, string captureMode, int duration, BepInEx.Logging.ManualLogSource logger)
    {
        directory = path; id = sessionId; mode = captureMode; Duration = duration;
        Directory.CreateDirectory(directory);
        string inventory = string.Join("\n", Chainloader.PluginInfos.Values.Select(p => p.Metadata.GUID + "=" + p.Metadata.Version + "|" + p.Instance.GetType().Assembly.ManifestModule.ModuleVersionId).OrderBy(x => x));
        using var sha = SHA256.Create(); string fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(inventory))).Replace("-", "");
        File.WriteAllText(Path.Combine(directory, "plugins.txt"), inventory, new UTF8Encoding(false));
        receipt = new Receipt { Protocol = 1, SessionId = id, PluginVersion = "0.1.10", Mode = mode,
            StopwatchFrequency = Stopwatch.Frequency,
            ProfileFingerprint = fingerprint, GameVersion = typeof(Player).Assembly.GetName().Version?.ToString() ?? "unknown",
            UnityVersion = Application.unityVersion, Graphics = Screen.width + "x" + Screen.height + "; VSync=" + QualitySettings.vSyncCount + "; cap=" + Application.targetFrameRate + "; GPU=" + SystemInfo.graphicsDeviceName,
            MainThreadId = (int)GetCurrentThreadId(), LegacyRecorderDetected = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("RiverheimCascadesLoader.FrameTraceCapture", false) != null) };
        try
        {
            cpu = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            gpu = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 1);
            if (Deep)
            {
                gcTrace = new NativeGcTrace(directory);
                details = DetailedTrace.TryCreate(logger, directory, 360000) ?? throw new InvalidOperationException("Deep probes could not be installed. See the game log.");
                details.WritePatchInventory(DateTime.UtcNow.Ticks); details.Prepare();
            }
            writer = Task.Run(WriteFrames);
        }
        catch { cpu.Dispose(); gpu.Dispose(); details?.Dispose(); queue.Dispose(); throw; }
    }
    internal void Begin()
    {
        if (Started) return; // Repeated Begin for the same owned session must not reset the clock.
        if (PreparationExpired) throw new InvalidOperationException("Capture preparation expired. Start a new capture.");
        start = Stopwatch.GetTimestamp(); utc = DateTime.UtcNow;
        gcTrace?.Begin(start);
        receipt.StartUtc = utc.ToString("O"); receipt.StopwatchStart = start;
        File.WriteAllText(Path.Combine(directory, "recorder.json"), RecorderPlugin.Json(receipt), new UTF8Encoding(false));
        details?.Begin(start, utc.Ticks);
        Started = true;
    }
    internal void Sample()
    {
        long stamp = Stopwatch.GetTimestamp(); LastMs = previous == 0 ? 0 : (stamp - previous) * 1000.0 / Stopwatch.Frequency; previous = stamp;
        Player? player = Player.m_localPlayer;
        var sample = new SampleData { Stamp = stamp, Interval = LastMs, Unity = Time.unscaledDeltaTime * 1000,
            Focus = Application.isFocused, Player = player != null, Gc = GC.CollectionCount(0), Biome = player != null ? (int)player.GetCurrentBiome() : 0,
            Cpu = cpu.Valid && cpu.Count > 0 ? cpu.LastValue / 1e6 : 0, Gpu = gpu.Valid && gpu.Count > 0 ? gpu.LastValue / 1e6 : 0 };
        details?.SampleFrame(Count, stamp);
        sample.SamplerMs = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
        samplerTotalMs += sample.SamplerMs; samplerMaximumMs = Math.Max(samplerMaximumMs, sample.SamplerMs);
        if (!queue.TryAdd(sample)) Interlocked.Increment(ref dropped);
        Count++;
    }
    internal void EndOnGameThread()
    {
        queue.CompleteAdding();
        try { gcTrace?.End(); details?.End(); }
        finally { try { details?.Dispose(); } finally { cpu.Dispose(); gpu.Dispose(); } }
    }
    internal void Save()
    {
        writer.GetAwaiter().GetResult(); details?.Save();
        if (Deep) CodeInventory.Write(directory, AppDomain.CurrentDomain.GetAssemblies(),
            Chainloader.PluginInfos.Values.GroupBy(p => p.Instance.GetType().Assembly).ToDictionary(g => g.Key, g => string.Join(";", g.Select(p => p.Metadata.GUID))));
        if (WriterError.Length > 0) throw new IOException(WriterError);
        File.WriteAllText(Path.Combine(directory, "completed.json"), RecorderPlugin.Json(new Completion {
            SessionId = id, Frames = Count, DroppedFrames = Dropped, StopUtc = DateTime.UtcNow.ToString("O"), State = Started ? "Complete" : "Cancelled",
            MeanSamplerMs = Count > 0 ? samplerTotalMs / Count : 0, MaximumSamplerMs = samplerMaximumMs }), new UTF8Encoding(false));
        queue.Dispose();
    }
    private void WriteFrames()
    {
        try
        {
            using var output = new StreamWriter(new FileStream(Path.Combine(directory, "frames.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            output.WriteLine("UtcTimestamp,ElapsedMs,FrameIntervalMs,UnityUnscaledDeltaMs,Fps,Focused,HasPlayer,Gc0,Biome,CpuMainMs,GpuFrameMs,RecorderSampleMs");
            WriteConfigurationFingerprint();
            long flush = Stopwatch.GetTimestamp();
            foreach (SampleData s in queue.GetConsumingEnumerable())
            {
                double elapsed = (s.Stamp - start) * 1000.0 / Stopwatch.Frequency;
                output.WriteLine(string.Join(",", utc.AddMilliseconds(elapsed).ToString("O"), N(elapsed), N(s.Interval), N(s.Unity), N(s.Interval > 0 ? 1000 / s.Interval : 0),
                    s.Focus ? "1" : "0", s.Player ? "1" : "0", s.Gc.ToString(CultureInfo.InvariantCulture), s.Biome.ToString(CultureInfo.InvariantCulture), s.Cpu > 0 ? N(s.Cpu) : "", s.Gpu > 0 ? N(s.Gpu) : "", N(s.SamplerMs)));
                if (Stopwatch.GetTimestamp() - flush >= Stopwatch.Frequency) { output.Flush(); flush = Stopwatch.GetTimestamp(); }
            }
        }
        catch (Exception ex) { Volatile.Write(ref writerError, "Capture writer failed: " + ex.Message); }
    }
    private void WriteConfigurationFingerprint()
    {
        try
        {
            using var aggregate = SHA256.Create();
            string[] paths = Directory.GetFiles(configDirectory, "*.cfg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 4096) throw new IOException("Configuration file count exceeds fingerprint limit.");
            long bytes = 0;
            foreach (string path in paths)
            {
                var info = new FileInfo(path); bytes += info.Length;
                if (info.Length > 4 * 1024 * 1024 || bytes > 64 * 1024 * 1024) throw new IOException("Configuration fingerprint size limit reached.");
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var hash = SHA256.Create();
                byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(path)); byte[] digest = hash.ComputeHash(input);
                aggregate.TransformBlock(name, 0, name.Length, name, 0); aggregate.TransformBlock(digest, 0, digest.Length, digest, 0);
            }
            aggregate.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            File.WriteAllText(Path.Combine(directory, "configuration.json"), RecorderPlugin.Json(new ConfigurationReceipt {
                ConfigurationFingerprint = BitConverter.ToString(aggregate.Hash!).Replace("-", ""), ConfigurationFiles = paths.Length }), new UTF8Encoding(false));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "configuration-warning.txt"), "Configuration fingerprint unavailable: " + ex.Message, new UTF8Encoding(false)); }
    }
    private static string N(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
    private NativeGcTrace? gcTrace;
    [System.Runtime.InteropServices.DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DataContract]
    public sealed class Receipt
    {
        [DataMember] public int Protocol { get; set; }
        [DataMember] public string SessionId { get; set; } = "";
        [DataMember] public string PluginVersion { get; set; } = "";
        [DataMember] public string Mode { get; set; } = "";
        [DataMember] public string StartUtc { get; set; } = "";
        [DataMember] public long StopwatchStart { get; set; }
        [DataMember] public long StopwatchFrequency { get; set; }
        [DataMember] public string ProfileFingerprint { get; set; } = "";
        [DataMember] public string GameVersion { get; set; } = "";
        [DataMember] public string UnityVersion { get; set; } = "";
        [DataMember] public string Graphics { get; set; } = "";
        [DataMember] public int MainThreadId { get; set; }
        [DataMember] public bool LegacyRecorderDetected { get; set; }
    }
    [DataContract]
    public sealed class Completion
    {
        [DataMember] public string SessionId { get; set; } = "";
        [DataMember] public int Frames { get; set; }
        [DataMember] public long DroppedFrames { get; set; }
        [DataMember] public string StopUtc { get; set; } = "";
        [DataMember] public string State { get; set; } = "";
        [DataMember] public double MeanSamplerMs { get; set; }
        [DataMember] public double MaximumSamplerMs { get; set; }
    }
    [DataContract]
    public sealed class ConfigurationReceipt
    {
        [DataMember] public string ConfigurationFingerprint { get; set; } = "";
        [DataMember] public int ConfigurationFiles { get; set; }
    }
}
