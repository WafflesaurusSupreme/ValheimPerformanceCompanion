using System;
using System.Collections.Generic;

namespace ValheimPerformanceCompanion.Recorder;

internal sealed class ParticleCensusPlan
{
	internal enum SceneKind
	{
		Other,
		Meadows,
		SwampRain
	}

	internal enum Checkpoint
	{
		Meadows,
		EarlySwamp,
		LaterSwamp
	}

	internal enum AttemptState
	{
		Pending,
		Completed,
		Failed
	}

	internal readonly struct Context
	{
		internal readonly long Ticks;

		internal readonly long Generation;

		internal readonly int Frame;

		internal readonly SceneKind Scene;

		internal readonly string Environment;

		internal readonly bool Focused;

		internal readonly bool HasPlayer;

		internal readonly bool Outdoors;

		internal readonly bool Loading;

		internal Context(long ticks, int frame, long generation, SceneKind scene, string environment, bool focused, bool hasPlayer, bool outdoors, bool loading)
		{
			Ticks = ticks;
			Frame = frame;
			Generation = generation;
			Scene = scene;
			Environment = environment ?? "";
			Focused = focused;
			HasPlayer = hasPlayer;
			Outdoors = outdoors;
			Loading = loading;
		}
	}

	internal sealed class Attempt
	{
		internal readonly Checkpoint Trigger;

		internal readonly long RequestedTicks;

		internal readonly long Generation;

		internal readonly long ExclusionStartTicks;

		internal readonly int Frame;

		internal readonly string Environment;

		internal int ExclusionStartFrame => Math.Max(0, Frame - 1);

		internal long GuardEndFrame => (long)Frame + 3L;

		internal AttemptState State { get; private set; }

		internal long? StartTicks { get; private set; }

		internal long? EndTicks { get; private set; }

		internal long? ExclusionEndTicks { get; private set; }

		internal bool GuardClippedAtStop { get; private set; }

		internal string Reason { get; private set; } = "";

		internal Attempt(Checkpoint trigger, Context context, long exclusionStart)
		{
			Trigger = trigger;
			RequestedTicks = context.Ticks;
			Generation = context.Generation;
			Frame = context.Frame;
			Environment = context.Environment;
			ExclusionStartTicks = exclusionStart;
		}

		internal void Finish(long start, long end, bool success, string reason)
		{
			if (State != AttemptState.Pending)
			{
				throw new InvalidOperationException("Census attempt already finished.");
			}
			if (start < RequestedTicks || end < start)
			{
				throw new ArgumentOutOfRangeException("start");
			}
			StartTicks = start;
			EndTicks = end;
			if (ExclusionEndTicks.HasValue && ExclusionEndTicks.Value < end)
			{
				ExclusionEndTicks = end;
			}
			State = (success ? AttemptState.Completed : AttemptState.Failed);
			Reason = reason ?? "";
		}

		internal void CloseGuard(long ticks, bool clipped)
		{
			if (!ExclusionEndTicks.HasValue)
			{
				ExclusionEndTicks = Math.Max(ticks, EndTicks ?? RequestedTicks);
				GuardClippedAtStop = clipped;
			}
		}
	}

	internal sealed class MissingCheckpoint
	{
		internal readonly Checkpoint Trigger;

		internal readonly string Reason;

		internal MissingCheckpoint(Checkpoint trigger, string reason)
		{
			Trigger = trigger;
			Reason = reason;
		}
	}

	private readonly long settleTicks;

	private readonly long laterTicks;

	private readonly long maximumObservationGapTicks;

	private readonly List<Attempt> attempts = new List<Attempt>(3);

	private readonly List<MissingCheckpoint> missing = new List<MissingCheckpoint>(3);

	private readonly bool[] used = new bool[3];

	private bool hasPrevious;

	private bool hasResidence;

	private bool stopped;

	private bool sawMeadows;

	private bool sawSwamp;

	private Context previous;

	private long residenceStart;

	internal IReadOnlyList<Attempt> Attempts => attempts;

	internal IReadOnlyList<MissingCheckpoint> Missing => missing;

	internal bool Stopped => stopped;

	internal ParticleCensusPlan(long frequency)
	{
		if (frequency <= 0 || frequency > 307445734561825860L)
		{
			throw new ArgumentOutOfRangeException("frequency");
		}
		settleTicks = frequency * 2;
		laterTicks = frequency * 30;
		maximumObservationGapTicks = frequency / 2;
	}

	internal Attempt? Observe(Context context)
	{
		if (stopped)
		{
			return null;
		}
		if (context.Frame < 0 || context.Ticks < 0)
		{
			throw new ArgumentOutOfRangeException("context");
		}
		if (hasPrevious && (context.Frame <= previous.Frame || context.Ticks < previous.Ticks))
		{
			throw new InvalidOperationException("Census observations must advance monotonically.");
		}
		foreach (Attempt attempt2 in attempts)
		{
			if (attempt2.State == AttemptState.Pending)
			{
				throw new InvalidOperationException("Finish the synchronous census attempt before observing another frame.");
			}
		}
		foreach (Attempt attempt3 in attempts)
		{
			if (!attempt3.ExclusionEndTicks.HasValue && (long)context.Frame - (long)attempt3.Frame >= 2)
			{
				attempt3.CloseGuard(context.Ticks, clipped: false);
			}
		}
		int num;
		if (context.Focused && context.HasPlayer && context.Outdoors)
		{
			num = ((!context.Loading) ? 1 : 0);
			if (num != 0 && context.Scene == SceneKind.Meadows)
			{
				sawMeadows = true;
			}
		}
		else
		{
			num = 0;
		}
		if (num != 0 && context.Scene == SceneKind.SwampRain)
		{
			sawSwamp = true;
		}
		bool flag = hasPrevious && context.Frame == previous.Frame + 1 && context.Ticks - previous.Ticks <= maximumObservationGapTicks && context.Generation == previous.Generation && context.Scene == previous.Scene && StringComparer.Ordinal.Equals(context.Environment, previous.Environment) && previous.Focused && previous.HasPlayer && previous.Outdoors && !previous.Loading;
		if (num == 0 || context.Scene == SceneKind.Other)
		{
			hasResidence = false;
		}
		else if (!hasResidence || !flag)
		{
			residenceStart = context.Ticks;
			hasResidence = true;
		}
		long exclusionStart = (hasPrevious ? previous.Ticks : context.Ticks);
		previous = context;
		hasPrevious = true;
		if (!hasResidence)
		{
			return null;
		}
		long num2 = context.Ticks - residenceStart;
		Checkpoint? checkpoint = null;
		if (context.Scene == SceneKind.Meadows && num2 >= settleTicks && !used[0])
		{
			checkpoint = Checkpoint.Meadows;
		}
		else if (context.Scene == SceneKind.SwampRain)
		{
			if (num2 >= settleTicks && !used[1])
			{
				checkpoint = Checkpoint.EarlySwamp;
			}
			else if (num2 >= laterTicks && !used[2])
			{
				checkpoint = Checkpoint.LaterSwamp;
			}
		}
		if (!checkpoint.HasValue)
		{
			return null;
		}
		used[(int)checkpoint.Value] = true;
		Attempt attempt = new Attempt(checkpoint.Value, context, exclusionStart);
		attempts.Add(attempt);
		return attempt;
	}

	internal void Finish(Attempt attempt, long startTicks, long endTicks, bool success, string reason = "")
	{
		if (!attempts.Contains(attempt))
		{
			throw new ArgumentException("Attempt belongs to another session.", "attempt");
		}
		attempt.Finish(startTicks, endTicks, success, reason);
	}

	internal void InvalidateResidence()
	{
		hasResidence = false;
	}

	internal void Stop(long stopTicks, string reason = "stopped")
	{
		if (stopped)
		{
			return;
		}
		if (stopTicks < 0 || (hasPrevious && stopTicks < previous.Ticks))
		{
			throw new ArgumentOutOfRangeException("stopTicks");
		}
		stopped = true;
		hasResidence = false;
		foreach (Attempt attempt in attempts)
		{
			if (attempt.State == AttemptState.Pending)
			{
				attempt.Finish(attempt.RequestedTicks, stopTicks, success: false, reason + "_before_completion");
			}
			attempt.CloseGuard(stopTicks, clipped: true);
		}
		foreach (Checkpoint value in Enum.GetValues(typeof(Checkpoint)))
		{
			if (!used[(int)value])
			{
				string text = ((!((value == Checkpoint.Meadows) ? sawMeadows : sawSwamp)) ? "eligible_scene_never_observed" : ((value == Checkpoint.LaterSwamp) ? "continuous_30_second_residence_not_reached" : "stable_2_second_scene_not_reached"));
				missing.Add(new MissingCheckpoint(value, reason + ":" + text));
			}
		}
	}

	internal bool ExcludesWindow(long startTicks, long endTicks)
	{
		if (endTicks < startTicks)
		{
			throw new ArgumentOutOfRangeException("endTicks");
		}
		foreach (Attempt attempt in attempts)
		{
			if (endTicks >= attempt.ExclusionStartTicks && (!attempt.ExclusionEndTicks.HasValue || startTicks <= attempt.ExclusionEndTicks.Value))
			{
				return true;
			}
		}
		return false;
	}
}
