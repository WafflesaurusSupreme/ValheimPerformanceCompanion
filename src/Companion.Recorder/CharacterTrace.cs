using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ValheimPerformanceCompanion.Recorder;

// Accessed only from the main thread. Entity discovery precedes the timed character call.
internal sealed class CharacterTrace
{
    internal sealed class Entry
    {
        internal Character? Instance;
        internal int Id;
        internal string Prefab = "";
        internal bool Player;
        internal long Calls, Ticks, Allocated, GcCalls, Maximum, LastSnapshotCalls;

        internal void Complete(long ticks, long allocated, bool collected)
        {
            Calls++; Ticks += ticks; Allocated += allocated;
            if (collected) GcCalls++;
            Maximum = Math.Max(Maximum, ticks);
        }
    }

    private struct Sample
    {
        internal long Timestamp, Calls, Ticks, Allocated, GcCalls, Maximum;
        internal int Id;
        internal string Prefab;
        internal bool Player, Alive, Water, Swimming, Ground;
        internal Vector3 Position;
    }

    private readonly Dictionary<int, Entry> entities = new Dictionary<int, Entry>();
    private readonly List<Sample> samples = new List<Sample>(65536);
    private int overflow;

    internal Entry? Get(Character character)
    {
        int id = character.GetInstanceID();
        if (entities.TryGetValue(id, out Entry? entry)) return entry;
        if (entities.Count >= 4096) { overflow++; return null; }
        entry = new Entry { Instance = character, Id = id, Prefab = character.gameObject.name, Player = character is Player };
        entities.Add(id, entry);
        return entry;
    }

    internal void Snapshot(long timestamp)
    {
        foreach (Entry entry in entities.Values)
        {
            if (entry.Calls == entry.LastSnapshotCalls) continue;
            if (samples.Count >= 250000) { overflow++; continue; }
            entry.LastSnapshotCalls = entry.Calls;
            Character? instance = entry.Instance;
            bool alive = instance != null;
            samples.Add(new Sample { Timestamp = timestamp, Id = entry.Id, Prefab = entry.Prefab,
                Player = entry.Player, Alive = alive, Position = alive ? instance!.transform.position : default,
                Water = alive && instance!.InWater(), Swimming = alive && instance!.IsSwimming(), Ground = alive && instance!.IsOnGround(),
                Calls = entry.Calls, Ticks = entry.Ticks, Allocated = entry.Allocated, GcCalls = entry.GcCalls, Maximum = entry.Maximum });
        }
    }

    internal void Save(string path, Func<long, string> timestamp, bool allocationsAvailable)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        writer.WriteLine("UtcTimestamp,InstanceId,Prefab,IsPlayer,StillExists,X,Y,Z,InWater,Swimming,OnGround,CumulativeCalls,CumulativeMs,CumulativeAllocatedBytes,CumulativeCallsWithGc,MaxCallMs,UntrackedCallsAtCapacity");
        foreach (Sample s in samples)
            writer.WriteLine(string.Join(",", timestamp(s.Timestamp), Number(s.Id), "\"" + s.Prefab.Replace("\"", "\"\"") + "\"",
                s.Player ? "1" : "0", s.Alive ? "1" : "0", Number(s.Position.x), Number(s.Position.y), Number(s.Position.z),
                s.Water ? "1" : "0", s.Swimming ? "1" : "0", s.Ground ? "1" : "0", Number(s.Calls),
                Number(s.Ticks * 1000.0 / Stopwatch.Frequency), allocationsAvailable ? Number(s.Allocated) : "", Number(s.GcCalls),
                Number(s.Maximum * 1000.0 / Stopwatch.Frequency), Number(overflow)));
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
