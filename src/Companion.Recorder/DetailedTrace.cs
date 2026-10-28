using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ValheimPerformanceCompanion.Recorder;

// Timings are inclusive wall time. Nested methods and loop stages overlap and must not be summed.
internal sealed class DetailedTrace : IDisposable
{
    private const string HarmonyId = "tools.valheim.performancecompanion.deep";
    private readonly ManualLogSource log;
    private readonly string directory;
    private readonly int capacity;
    private readonly Harmony harmony = new Harmony(HarmonyId);
    private static readonly Dictionary<MethodBase, int> MethodSlots = new Dictionary<MethodBase, int>();
    private readonly List<string> names = new List<string>();
    private readonly List<int> strides = new List<int>();
    private readonly List<string> coverage = new List<string>();
    private readonly List<Counter> counters = new List<Counter>();
    private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
    private static Session? active;
    private static bool allocationSupported;
    [ThreadStatic] private static int swimmingDepth;
    private static readonly HashSet<int> SwimmingSlots = new HashSet<int>();
    private Session? captured;
    private readonly FrameTiming[] latestTiming = new FrameTiming[1];
    private bool frameTimingAvailable;

    internal sealed class Metric
    {
        internal long Sequence, Calls, Samples, Ticks, MainTicks, WorkerTicks, Maximum, Errors, AllocatedBytes, GcCalls;
    }

    internal sealed class Session
    {
        internal readonly Metric[] Metrics;
        internal readonly int[] Strides;
        internal readonly FrameData[] Frames;
        internal readonly long[][] CounterValues;
        internal readonly List<Window> Windows = new List<Window>(2400);
        internal readonly CharacterTrace Characters = new CharacterTrace();
        internal long Start, Utc;
        internal readonly int MainThread;
        internal int Count;
        internal long LastWindow;

        internal Session(int metricCount, int[] strides, int frameCapacity, int counterCount, long start, long utc, int main)
        {
            Metrics = new Metric[metricCount];
            for (int i = 0; i < metricCount; i++) Metrics[i] = new Metric();
            Strides = strides;
            Frames = new FrameData[frameCapacity];
            CounterValues = new long[counterCount][];
            for (int i = 0; i < counterCount; i++) CounterValues[i] = new long[frameCapacity];
            Start = LastWindow = start;
            Utc = utc;
            MainThread = main;
        }
    }

    internal struct Call
    {
        internal Session? Session;
        internal int Slot;
        internal long Start;
        internal bool Main;
        internal long AllocatedStart;
        internal int GcStart;
        internal CharacterTrace.Entry? Character;
        internal bool SwimmingScope;
    }

    internal struct FrameData
    {
        internal long Timestamp;
        internal float X, Y, Z;
        internal int Biome, Characters, Gc0, Gc1, Gc2;
        internal bool Focus, HasPlayer, HasTiming;
        internal bool Wet;
        internal string? Environment;
        internal double CpuMs, MainMs, RenderMs, GpuMs, PresentMs;
        internal double SamplerMs;
        internal ulong TimingStamp;
    }

    internal sealed class Window
    {
        internal long Timestamp;
        internal long[] Values = Array.Empty<long>();
    }

    private sealed class Counter
    {
        internal string Name = "", Unit = "";
        internal ProfilerRecorder Recorder;
    }

    private sealed class LoopBegin { }
    private sealed class LoopEnd { }

    private DetailedTrace(ManualLogSource log, string directory, int capacity)
    {
        this.log = log;
        this.directory = directory;
        this.capacity = capacity;
    }

    internal static DetailedTrace? TryCreate(ManualLogSource log, string directory, int capacity)
    {
        var trace = new DetailedTrace(log, directory, capacity);
        try { trace.Install(); return trace; }
        catch (Exception error)
        {
            log.LogError("Detailed trace installation failed: " + error);
            trace.Dispose();
            return null;
        }
    }

    private void Install()
    {
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var allocationProbe = new byte[4096];
            allocationSupported = GC.GetAllocatedBytesForCurrentThread() - before >= allocationProbe.Length;
            GC.KeepAlive(allocationProbe);
        }
        catch (Exception error) { coverage.Add("UNAVAILABLE per-thread allocation counter: " + error.Message); }
        coverage.Add("Per-thread allocation counter enabled=" + allocationSupported);
        AddMethods(typeof(HeightmapBuilder), "Build", 1);
        AddMethods(typeof(HeightmapBuilder), "RequestTerrainSync", 1);
        AddMethods(typeof(Heightmap), "Generate", 1);
        AddMethods(typeof(ZoneSystem), "Update", 1);
        AddMethods(typeof(ZoneSystem), "SpawnZone", 1);
        AddMethods(typeof(ZoneSystem), "PlaceVegetation", 1);
        AddMethods(typeof(ZoneSystem), "PlaceLocations", 1);
        AddMethods(typeof(ZoneSystem), "SpawnLocation", 1);
        AddMethods(typeof(SpawnSystem), "UpdateSpawning", 1);
        AddMethods(typeof(SpawnSystem), "Spawn", 1);
        AddMethods(typeof(DungeonGenerator), "Generate", 1);
        AddMethods(typeof(DungeonGenerator), "Spawn", 1);
        AddMethods(typeof(ClutterSystem), "LateUpdate", 1);
        AddMethods(typeof(BaseAI), "UpdateAI", 1);
        AddMethods(typeof(MonsterAI), "UpdateAI", 1);
        AddMethods(typeof(AnimalAI), "UpdateAI", 1);
        AddMethods(typeof(Character), "CustomFixedUpdate", 1);
        foreach (string method in new[] { "CalculateLiquidDepth", "UpdateLayer", "UpdateContinousEffects", "UpdateWater",
            "UpdateGroundTilt", "SetVisible", "UpdateLookTransition", "UpdateGroundContact", "UpdateNoise",
            "UpdateStagger", "UpdatePushback", "UpdateMotion", "UpdateSwimming", "UpdateWalking", "UpdateFlying",
            "UpdateSmoke", "UpdateLava", "UpdateAshlandsWater", "UpdateHeatDamage", "UnderWorldCheck",
            "UpdatePheromones", "SyncVelocity", "CheckDeath", "UpdateHeatEffects" })
            AddMethods(typeof(Character), method, 1);
        AddMethods(typeof(SEMan), "Update", 1);
        AddMethods(typeof(SEMan), "AddStatusEffect", 1);
        foreach (string method in new[] { "UpdateRotation", "UpdateEyeRotation", "AddPushbackForce",
            "GetAttackSpeedFactorMovement", "InMinorActionSlowdown", "HaveRider", "AddNoise" })
            AddMethods(typeof(Character), method, 1, swimmingOnly: true);
        AddMethods(typeof(SEMan), "ApplyStatusEffectSpeedMods", 1, swimmingOnly: true);
        AddMethods(typeof(StatusEffect), "ModifySpeed", 1, swimmingOnly: true);
        AddMethods(typeof(SE_Stats), "ModifySpeed", 1, swimmingOnly: true);
        AddMethods(typeof(SE_Frost), "ModifySpeed", 1, swimmingOnly: true);
        foreach (string effect in new[] { "BurningSpeed", "StormRider", "TravelLight" })
        {
            string typeName = "EpicLoot.MagicItemEffects.Shards." + effect + "+ApplyStatusEffectSpeedMods_Patch";
            Type? patchType = AccessTools.TypeByName(typeName);
            if (patchType != null) AddMethods(patchType, "Postfix", 1, swimmingOnly: true);
            else coverage.Add("MISSING optional speed patch " + typeName);
        }
        AddMethods(typeof(ZSyncAnimation), "SetFloat", 1, swimmingOnly: true);
        AddMethods(typeof(ZSyncAnimation), "SetBool", 1, swimmingOnly: true);
        AddMethods(typeof(ZoneSystem), "GetGroundData", 1);
        AddMethods(typeof(ZoneSystem), "GetGroundHeight", 1);
        AddMethods(typeof(EnvMan), "Update", 1);
        AddMethods(typeof(EnvMan), "FixedUpdate", 1);
        AddMethods(typeof(EnvMan), "SetParticleArrayEnabled", 1);
        AddMethods(typeof(EnvZone), "Update", 1);
        AddMethods(typeof(MistEmitter), "Update", 1);
        AddMethods(typeof(ParticleMist), "Update", 1);
        AddMethods(typeof(Smoke), "CustomUpdate", 1);
        AddMethods(typeof(SmokeSpawner), "CustomUpdate", 1);
        AddMethods(typeof(SmokeRenderer), "LateUpdate", 1);
        AddMethods(typeof(WaterVolume), "StaticUpdate", 1);
        Type? renderer = AccessTools.TypeByName("Riverheim.Rendering.WorldRenderer");
        if (renderer != null)
        {
            // Sample hot point queries to reduce observer overhead; all completions are counted.
            AddMethods(renderer, "RenderPoint", 64);
            AddMethods(renderer, "GetBiome", 128);
        }
        else coverage.Add("MISSING Riverheim.Rendering.WorldRenderer");

        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        InstallLoop(ref loop);
        PlayerLoop.SetPlayerLoop(loop);
        DiscoverCounters();
        try { frameTimingAvailable = FrameTimingManager.IsFeatureEnabled(); }
        catch (Exception error) { coverage.Add("FrameTimingManager unavailable: " + error.Message); }
        coverage.Add("FrameTimingManager enabled=" + frameTimingAvailable);
        Directory.CreateDirectory(directory);
        string inventory = Path.Combine(directory, "coverage-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".txt");
        File.WriteAllLines(inventory, coverage, new UTF8Encoding(false));
        log.LogInfo("Detailed trace probes installed: " + names.Count + " timing probes, " + counters.Count + " Unity counters. Coverage: " + inventory);
        foreach (string entry in coverage)
            if (entry.StartsWith("MISSING", StringComparison.Ordinal) || entry.StartsWith("FAILED", StringComparison.Ordinal))
                log.LogWarning(entry);
    }

    private int AddSlot(string name, int stride)
    {
        int slot = names.Count;
        names.Add(name);
        strides.Add(stride);
        return slot;
    }

    private void AddMethods(Type type, string name, int stride, bool swimmingOnly = false)
    {
        int found = 0;
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.Name != name || method.GetMethodBody() == null) continue;
            found++;
            string label = type.FullName + "." + name + "(" + method.GetParameters().Length + ")";
            if (type == typeof(SEMan) && name == "AddStatusEffect") label += "." + method.GetParameters()[0].ParameterType.Name;
            if (type == typeof(ZSyncAnimation)) label += "." + method.GetParameters()[0].ParameterType.Name;
            if (swimmingOnly) label += ".SwimmingScope";
            int slot = AddSlot(label, stride);
            if (swimmingOnly) SwimmingSlots.Add(slot);
            MethodSlots.Add(method, slot);
            try
            {
                string prefixName = type == typeof(Character) && name == "CustomFixedUpdate" ? nameof(CharacterPrefix)
                    : type == typeof(Character) && name == "UpdateSwimming" ? nameof(SwimmingPrefix) : nameof(MethodPrefix);
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(DetailedTrace), prefixName) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(DetailedTrace), nameof(MethodFinalizer)) { priority = Priority.Last });
                coverage.Add("METHOD " + label + " sampleEvery=" + stride);
            }
            catch (Exception error)
            {
                MethodSlots.Remove(method);
                coverage.Add("FAILED " + label + ": " + error);
            }
        }
        if (found == 0) coverage.Add("MISSING " + type.FullName + "." + name);
    }

    private static void MethodPrefix(MethodBase __originalMethod, out Call __state)
    {
        __state = default;
        if (Volatile.Read(ref active) != null && MethodSlots.TryGetValue(__originalMethod, out int slot)
            && (!SwimmingSlots.Contains(slot) || swimmingDepth > 0))
            __state = Enter(slot);
    }

    private static void MethodFinalizer(Call __state, Exception? __exception)
    {
        try { Exit(__state, __exception != null); }
        finally { if (__state.SwimmingScope) swimmingDepth--; }
    }

    private static void SwimmingPrefix(MethodBase __originalMethod, out Call __state)
    {
        __state = default;
        if (Volatile.Read(ref active) == null || !MethodSlots.TryGetValue(__originalMethod, out int slot)) return;
        __state = Enter(slot);
        __state.SwimmingScope = true;
        swimmingDepth++;
    }

    private static void CharacterPrefix(Character __instance, MethodBase __originalMethod, out Call __state)
    {
        __state = default;
        Session? session = Volatile.Read(ref active);
        if (session == null || !MethodSlots.TryGetValue(__originalMethod, out int slot)) return;
        CharacterTrace.Entry? entity = Thread.CurrentThread.ManagedThreadId == session.MainThread
            ? session.Characters.Get(__instance) : null;
        __state = Enter(slot);
        __state.Character = entity;
    }

    private static Call Enter(int slot)
    {
        Session? session = Volatile.Read(ref active);
        if (session == null) return default;
        long sequence = Interlocked.Increment(ref session.Metrics[slot].Sequence);
        bool sample = (sequence - 1) % session.Strides[slot] == 0;
        var call = new Call { Session = session, Slot = slot,
            Main = Thread.CurrentThread.ManagedThreadId == session.MainThread };
        if (sample)
        {
            call.AllocatedStart = allocationSupported ? GC.GetAllocatedBytesForCurrentThread() : 0;
            call.GcStart = GC.CollectionCount(0);
            call.Start = Stopwatch.GetTimestamp();
        }
        return call;
    }

    private static void Exit(Call call, bool error = false)
    {
        Session? session = call.Session;
        if (session == null || !ReferenceEquals(session, Volatile.Read(ref active))) return;
        Metric metric = session.Metrics[call.Slot];
        Interlocked.Increment(ref metric.Calls);
        if (error) Interlocked.Increment(ref metric.Errors);
        if (call.Start == 0) return;
        long ticks = Stopwatch.GetTimestamp() - call.Start;
        long allocated = allocationSupported ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - call.AllocatedStart) : 0;
        bool collected = GC.CollectionCount(0) != call.GcStart;
        Interlocked.Add(ref metric.AllocatedBytes, allocated);
        if (collected) Interlocked.Increment(ref metric.GcCalls);
        Interlocked.Increment(ref metric.Samples);
        Interlocked.Add(ref metric.Ticks, ticks);
        if (call.Main) Interlocked.Add(ref metric.MainTicks, ticks);
        else Interlocked.Add(ref metric.WorkerTicks, ticks);
        long previous;
        do { previous = Interlocked.Read(ref metric.Maximum); if (ticks <= previous) break; }
        while (Interlocked.CompareExchange(ref metric.Maximum, ticks, previous) != previous);
        call.Character?.Complete(ticks, allocated, collected);
    }

    private void InstallLoop(ref PlayerLoopSystem parent)
    {
        if (parent.subSystemList == null) return;
        var children = new List<PlayerLoopSystem>();
        foreach (PlayerLoopSystem original in parent.subSystemList)
        {
            PlayerLoopSystem child = original;
            InstallLoop(ref child);
            string label = child.type?.FullName ?? "";
            bool selected = label == "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate" ||
                label == "UnityEngine.PlayerLoop.PreUpdate+PhysicsUpdate" ||
                label == "UnityEngine.PlayerLoop.PreLateUpdate+PhysicsLateUpdate" ||
                label == "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate" ||
                label == "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate" ||
                label == "UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate" ||
                label == "UnityEngine.PlayerLoop.PostLateUpdate+UpdateAllRenderers" ||
                label == "UnityEngine.PlayerLoop.PostLateUpdate+UpdateAllSkinnedMeshes" ||
                label == "UnityEngine.PlayerLoop.PostLateUpdate+FinishFrameRendering" ||
                label == "UnityEngine.PlayerLoop.PostLateUpdate+PresentAfterDraw" ||
                label == "UnityEngine.PlayerLoop.PreLateUpdate+ParticleSystemBeginUpdateAll" ||
                label == "UnityEngine.PlayerLoop.PostLateUpdate+ParticleSystemEndUpdateAll" ||
                label == "UnityEngine.PlayerLoop.TimeUpdate+WaitForLastPresentationAndUpdateTime";
            if (selected)
            {
                int slot = AddSlot(label, 1);
                Call call = default;
                children.Add(new PlayerLoopSystem { type = typeof(LoopBegin), updateDelegate = () => call = Enter(slot) });
                children.Add(child);
                children.Add(new PlayerLoopSystem { type = typeof(LoopEnd), updateDelegate = () => Exit(call) });
                coverage.Add("LOOP " + label);
            }
            else children.Add(child);
        }
        parent.subSystemList = children.ToArray();
    }

    private static void RemoveLoop(ref PlayerLoopSystem parent)
    {
        if (parent.subSystemList == null) return;
        var children = new List<PlayerLoopSystem>();
        foreach (PlayerLoopSystem original in parent.subSystemList)
        {
            if (original.type == typeof(LoopBegin) || original.type == typeof(LoopEnd)) continue;
            PlayerLoopSystem child = original;
            RemoveLoop(ref child);
            children.Add(child);
        }
        parent.subSystemList = children.ToArray();
    }

    private void DiscoverCounters()
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal) { "Main Thread", "Render Thread", "GPU Frame Time",
            "Batches Count", "Draw Calls Count", "SetPass Calls Count", "Triangles Count", "Vertices Count",
            "GC Allocated In Frame", "GC Used Memory", "Total Used Memory", "Physics.Simulate",
            "Particle Count", "Particle System Count", "Shadow Casters Count" };
        try
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                if (!wanted.Contains(description.Name)) continue;
                try
                {
                    var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default);
                    if (!recorder.Valid) { recorder.Dispose(); continue; }
                    counters.Add(new Counter { Name = description.Name, Unit = description.UnitType.ToString(), Recorder = recorder });
                    coverage.Add("COUNTER " + description.Name + " unit=" + description.UnitType);
                    wanted.Remove(description.Name);
                }
                catch (Exception error) { coverage.Add("FAILED COUNTER " + description.Name + ": " + error.Message); }
            }
        }
        catch (Exception error) { coverage.Add("FAILED counter enumeration: " + error); }
        foreach (string name in wanted) coverage.Add("UNAVAILABLE COUNTER " + name);
    }

    internal void Prepare()
    {
        captured = new Session(names.Count, strides.ToArray(), capacity, counters.Count, 0, 0, mainThread);
    }
    internal void Begin(long start, long utc)
    {
        foreach (Counter counter in counters) { counter.Recorder.Reset(); counter.Recorder.Start(); }
        if (captured == null) throw new InvalidOperationException("Deep session is not prepared.");
        captured.Start = captured.LastWindow = start; captured.Utc = utc;
        Volatile.Write(ref active, captured);
    }

    internal void SampleFrame(int index, long timestamp)
    {
        long sampleStart = Stopwatch.GetTimestamp();
        Session? session = captured;
        if (session == null) return;
        if (index >= session.Frames.Length)
        {
            if (index == session.Frames.Length)
            {
                const string warning = "Deep per-frame details reached their 360,000-frame buffer limit. Streaming frame intervals and method windows continue until the requested time limit.";
                log.LogWarning(warning);
                File.WriteAllText(Path.Combine(directory, "deep-quality.txt"), warning);
            }
            if (timestamp - session.LastWindow >= Stopwatch.Frequency / 4)
            { Snapshot(session, timestamp); session.Characters.Snapshot(timestamp); }
            return;
        }
        ref FrameData data = ref session.Frames[index];
        data.Timestamp = timestamp;
        data.Focus = Application.isFocused;
        Player? player = Player.m_localPlayer;
        data.HasPlayer = player != null;
        if (player != null)
        {
            Vector3 p = player.transform.position;
            data.X = p.x; data.Y = p.y; data.Z = p.z;
            data.Biome = (int)player.GetCurrentBiome();
        }
        data.Characters = Character.GetAllCharacters().Count;
        EnvMan? environment = EnvMan.instance;
        if (environment != null)
        {
            EnvSetup? setup = environment.GetCurrentEnvironment();
            data.Environment = setup?.m_name;
            data.Wet = setup?.m_isWet ?? false;
        }
        data.Gc0 = GC.CollectionCount(0); data.Gc1 = GC.CollectionCount(1); data.Gc2 = GC.CollectionCount(2);
        for (int i = 0; i < counters.Count; i++)
            session.CounterValues[i][index] = counters[i].Recorder.Count == 0 ? long.MinValue : counters[i].Recorder.LastValue;
        if (frameTimingAvailable)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, latestTiming) > 0)
            {
                FrameTiming timing = latestTiming[0];
                data.HasTiming = true; data.TimingStamp = timing.frameStartTimestamp;
                data.CpuMs = timing.cpuFrameTime; data.MainMs = timing.cpuMainThreadFrameTime;
                data.RenderMs = timing.cpuRenderThreadFrameTime; data.GpuMs = timing.gpuFrameTime;
                data.PresentMs = timing.cpuMainThreadPresentWaitTime;
            }
        }
        session.Count = index + 1;
        if (timestamp - session.LastWindow >= Stopwatch.Frequency / 4)
        {
            Snapshot(session, timestamp);
            session.Characters.Snapshot(timestamp);
        }
        data.SamplerMs = (Stopwatch.GetTimestamp() - sampleStart) * 1000.0 / Stopwatch.Frequency;
    }

    private static void Snapshot(Session session, long timestamp)
    {
        var window = new Window { Timestamp = timestamp, Values = new long[session.Metrics.Length * 10] };
        for (int i = 0; i < session.Metrics.Length; i++)
        {
            Metric m = session.Metrics[i]; int offset = i * 10;
            window.Values[offset] = Interlocked.Read(ref m.Calls);
            window.Values[offset + 1] = Interlocked.Read(ref m.Samples);
            window.Values[offset + 2] = Interlocked.Read(ref m.Ticks);
            window.Values[offset + 3] = Interlocked.Read(ref m.MainTicks);
            window.Values[offset + 4] = Interlocked.Read(ref m.WorkerTicks);
            window.Values[offset + 5] = Interlocked.Read(ref m.Maximum);
            window.Values[offset + 6] = Interlocked.Read(ref m.Errors);
            window.Values[offset + 7] = Interlocked.Read(ref m.Sequence);
            window.Values[offset + 8] = Interlocked.Read(ref m.AllocatedBytes);
            window.Values[offset + 9] = Interlocked.Read(ref m.GcCalls);
        }
        session.Windows.Add(window);
        session.LastWindow = timestamp;
    }

    internal void End()
    {
        Interlocked.Exchange(ref active, null);
        if (captured != null && captured.Start != 0 && captured.LastWindow != long.MaxValue)
        {
            long timestamp = Stopwatch.GetTimestamp();
            Snapshot(captured, timestamp);
            captured.Characters.Snapshot(timestamp);
            captured.LastWindow = long.MaxValue;
        }
        foreach (Counter counter in counters) counter.Recorder.Stop();
    }

    private static StreamWriter Writer(string path) => new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Timestamp(Session session, long stamp) =>
        new DateTime(session.Utc + (long)((stamp - session.Start) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency), DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    internal void Save()
    {
        Session? session = captured;
        if (session == null) return;
        string suffix = new DateTime(session.Utc, DateTimeKind.Utc).ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        try
        {
            using (StreamWriter writer = Writer(Path.Combine(directory, "details.csv")))
            {
                writer.Write("UtcTimestamp,FrameIndex,Focused,HasPlayer,X,Y,Z,Biome,LoadedCharacters,Gc0,Gc1,Gc2,HasFrameTiming,TimingTimestamp,CpuFrameMs,CpuMainMs,CpuRenderMs,GpuFrameMs,PresentWaitMs,Environment,EnvironmentWet,RecorderSampleMs");
                foreach (Counter counter in counters) writer.Write(",\"" + counter.Name + " [" + counter.Unit + "] previous completed frame\"");
                writer.WriteLine();
                for (int i = 0; i < session.Count; i++)
                {
                    FrameData f = session.Frames[i];
                    writer.Write(string.Join(",", Timestamp(session, f.Timestamp), i.ToString(CultureInfo.InvariantCulture),
                        f.Focus ? "1" : "0", f.HasPlayer ? "1" : "0", Number(f.X), Number(f.Y), Number(f.Z),
                        f.Biome.ToString(CultureInfo.InvariantCulture), f.Characters.ToString(CultureInfo.InvariantCulture),
                        f.Gc0.ToString(CultureInfo.InvariantCulture), f.Gc1.ToString(CultureInfo.InvariantCulture), f.Gc2.ToString(CultureInfo.InvariantCulture),
                        f.HasTiming ? "1" : "0", f.TimingStamp.ToString(CultureInfo.InvariantCulture),
                        f.HasTiming ? Number(f.CpuMs) : "", f.HasTiming ? Number(f.MainMs) : "", f.HasTiming ? Number(f.RenderMs) : "",
                        f.HasTiming ? Number(f.GpuMs) : "", f.HasTiming ? Number(f.PresentMs) : "",
                        "\"" + (f.Environment ?? "").Replace("\"", "\"\"") + "\"", f.Wet ? "1" : "0", Number(f.SamplerMs)));
                    for (int j = 0; j < counters.Count; j++)
                    {
                        long value = session.CounterValues[j][i];
                        writer.Write(',');
                        if (value != long.MinValue) writer.Write(value.ToString(CultureInfo.InvariantCulture));
                    }
                    writer.WriteLine();
                }
            }
            using (StreamWriter writer = Writer(Path.Combine(directory, "methods.csv")))
            {
                writer.WriteLine("UtcTimestamp,Probe,SampleEvery,CumulativeCompletedCalls,CumulativeTimedCalls,CumulativeMeasuredMs,CumulativeMainMs,CumulativeWorkerMs,MaxMeasuredCallMs,CumulativeErrors,CumulativeStartedCalls,CumulativeSampledAllocatedBytes,CumulativeTimedCallsWithGc");
                foreach (Window window in session.Windows)
                    for (int i = 0; i < names.Count; i++)
                    {
                        int o = i * 10; long[] v = window.Values;
                        writer.WriteLine(string.Join(",", Timestamp(session, window.Timestamp), names[i], strides[i].ToString(CultureInfo.InvariantCulture),
                            v[o].ToString(CultureInfo.InvariantCulture), v[o+1].ToString(CultureInfo.InvariantCulture),
                            Number(v[o+2] * 1000.0 / Stopwatch.Frequency), Number(v[o+3] * 1000.0 / Stopwatch.Frequency),
                            Number(v[o+4] * 1000.0 / Stopwatch.Frequency), Number(v[o+5] * 1000.0 / Stopwatch.Frequency),
                            v[o+6].ToString(CultureInfo.InvariantCulture), v[o+7].ToString(CultureInfo.InvariantCulture),
                            allocationSupported ? v[o+8].ToString(CultureInfo.InvariantCulture) : "", v[o+9].ToString(CultureInfo.InvariantCulture)));
                    }
            }
            session.Characters.Save(Path.Combine(directory, "characters.csv"), stamp => Timestamp(session, stamp), allocationSupported);
            log.LogInfo("Detailed trace saved: " + session.Count + " frames and " + session.Windows.Count + " timing snapshots; suffix=" + suffix);
        }
        catch (Exception error) { log.LogError("Detailed trace save failed: " + error); throw; }
        finally { captured = null; }
    }

    public void Dispose()
    {
        End();
        foreach (Counter counter in counters) counter.Recorder.Dispose();
        harmony.UnpatchSelf();
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        RemoveLoop(ref loop);
        PlayerLoop.SetPlayerLoop(loop);
        MethodSlots.Clear();
        SwimmingSlots.Clear();
    }

    internal void WritePatchInventory(long utc)
    {
        string suffix = new DateTime(utc, DateTimeKind.Utc).ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        using StreamWriter writer = Writer(Path.Combine(directory, "patches-" + suffix + ".csv"));
        writer.WriteLine("Target,Kind,Owner,Assembly,PatchMethod,Priority,TargetMvid,TargetToken,PatchMvid,PatchToken");
        foreach (MethodBase target in Harmony.GetAllPatchedMethods().ToArray())
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            if (patches == null) continue;
            void Write(string kind, IEnumerable<Patch> list)
            {
                foreach (Patch patch in list)
                {
                    MethodInfo method = patch.PatchMethod;
                    writer.WriteLine(string.Join(",", Quote(Companion.Shared.IlText.Signature(target)), kind, Quote(patch.owner),
                        Quote(method.DeclaringType?.Assembly.GetName().Name ?? ""), Quote(method.DeclaringType?.FullName + "." + method.Name),
                        patch.priority.ToString(CultureInfo.InvariantCulture), target.Module.ModuleVersionId, Token(target), method.Module.ModuleVersionId, Token(method)));
                }
            }
            Write("Prefix", patches.Prefixes); Write("Postfix", patches.Postfixes);
            Write("Transpiler", patches.Transpilers); Write("Finalizer", patches.Finalizers);
        }
    }

    private static string Token(MethodBase method)
    {
        try { return "0x" + method.MetadataToken.ToString("x8"); }
        catch (InvalidOperationException) { return "unavailable-dynamic"; }
        catch (NotSupportedException) { return "unavailable-dynamic"; }
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
