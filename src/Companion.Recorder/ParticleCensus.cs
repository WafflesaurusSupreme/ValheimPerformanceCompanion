using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ValheimPerformanceCompanion.Recorder;

internal sealed class ParticleCensus : IDisposable
{
	internal sealed class Owner
	{
		internal string Kind;

		internal string Name = "";

		internal string Slot = "";

		internal string PrefabName = "unknown";

		internal string References = "unknown_current_references_not_creation_identity";

		internal int Id;

		internal int RootId;

		internal int InstanceSlot = -1;

		internal int? PrefabHash;

		internal Owner(string kind, int id)
		{
			Kind = kind;
			Id = id;
		}

		internal Owner Copy()
		{
			return (Owner)MemberwiseClone();
		}
	}

	internal sealed class Row
	{
		internal int SystemId;

		internal int SceneHandle;

		internal int RootId;

		internal int ReadErrors;

		internal int? RendererId;

		internal string SystemName = "";

		internal string SimulationSpace = "unknown";

		internal string CullingMode = "unknown";

		internal string Qualification = "current_references_not_instance_creation_identity;visibility_is_last_camera_observation_not_culled";

		internal Owner Owner;

		internal Row(Owner owner) => Owner = owner;

		internal bool Active;

		internal bool? Playing;

		internal bool? Stopped;

		internal bool? Paused;

		internal bool? Emission;

		internal bool? Unscaled;

		internal bool? InheritVelocity;

		internal bool? Collision;

		internal bool? Trails;

		internal bool? Lights;

		internal bool? SubEmitters;

		internal bool? RendererEnabled;

		internal bool? LastVisible;
	}

	[DataContract]
	internal sealed class Snapshot
	{
		[DataMember]
		public int Id;

		[DataMember]
		public int Frame;

		[DataMember]
		public int SceneHandle;

		[DataMember]
		public int Biome;

		[DataMember]
		public int Enumerated;

		[DataMember]
		public int NotAdmitted;

		[DataMember]
		public int Destroyed;

		[DataMember]
		public int NonSceneSkipped;

		[DataMember]
		public int DuplicateIds;

		[DataMember]
		public int RowErrors;

		[DataMember]
		public int ReadErrors;

		[DataMember]
		public int CharactersObserved;

		[DataMember]
		public int CharactersVisited;

		[DataMember]
		public int DestroyedOwners;

		[DataMember]
		public int OwnerErrors;

		[DataMember]
		public int MissingOwnershipFields;

		[DataMember]
		public int AmbiguousBindings;

		[DataMember]
		public long Generation;

		[DataMember]
		public long StartTicks;

		[DataMember]
		public long EndTicks;

		[DataMember]
		public long? AllocatedBytes;

		[DataMember]
		public double DurationMilliseconds;

		[DataMember]
		public string Trigger = "";

		[DataMember]
		public string Scene = "";

		[DataMember]
		public string Environment = "";

		[DataMember]
		public string State = "";

		[DataMember]
		public string Error = "";

		[DataMember]
		public string OwnershipFields = "";

		[DataMember]
		public bool Focused;

		[DataMember]
		public bool HasPlayer;

		[DataMember]
		public bool UnstableScene;

		[DataMember]
		public bool TimeBudgetReached;

		[DataMember]
		public bool SystemCapReached;

		[DataMember]
		public bool CharacterCapReached;

		[DataMember]
		public bool BindingCapReached;

		[DataMember]
		public bool AncestorCapReached;

		[DataMember]
		public bool OwnershipTraversalTruncated;

		[DataMember]
		public bool Truncated;

		[DataMember]
		public float X;

		[DataMember]
		public float Y;

		[DataMember]
		public float Z;

		[DataMember]
		public int Admitted;

		internal readonly List<Row> Rows = new List<Row>();
	}

	[DataContract]
	internal sealed class Quality
	{
		[DataMember]
		public int Schema = 1;

		[DataMember]
		public int MaximumAttempts = 3;

		[DataMember]
		public int SystemCap = 8192;

		[DataMember]
		public int CharacterCap = 512;

		[DataMember]
		public int BindingCap = 2048;

		[DataMember]
		public int AncestorCap = 64;

		[DataMember]
		public double SoftBudgetMilliseconds = 50.0;

		[DataMember]
		public bool EnumerationArrayPrebounded = false;

		[DataMember]
		public bool ApiAdmitted;

		[DataMember]
		public long StopwatchFrequency;

		[DataMember]
		public long RecorderStopwatchStart;

		[DataMember]
		public int ContextErrors;

		[DataMember]
		public string FirstContextError = "";

		[DataMember]
		public string OwnershipIdentity = "";

		[DataMember]
		public string CoreModuleIdentity = "";

		[DataMember]
		public string ParticleModuleIdentity = "";

		[DataMember]
		public string Limits = "AllocatedBytes measures managed current-thread allocation delta only, not native/all-thread allocations. DurationMilliseconds is synchronous wall time including native getter waits. Public scene census is not Unity internal active manager membership or emitter CPU attribution. Current prefab references are not creation provenance. No per-particle reads. Native enumeration cannot be interrupted; caps and soft time budget apply after it returns. Inactive systems included and flagged. Renderer visibility is camera-dependent, not a culled flag.";

		[DataMember]
		public List<Snapshot> Snapshots = new List<Snapshot>();

		[DataMember]
		public List<string> Missing = new List<string>();
	}

	internal const int MaxSystems = 8192;

	internal const int MaxCharacters = 512;

	internal const int MaxBindings = 2048;

	internal const int MaxAncestors = 64;

	private const int MaxReferences = 24;

	private const int MaxText = 128;

	private const double BudgetMilliseconds = 50.0;

	private readonly ParticleCensusPlan plan = new ParticleCensusPlan(Stopwatch.Frequency);

	private readonly List<Snapshot> snapshots = new List<Snapshot>(3);

	private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;

	private long generation;

	private long start;

	private int lastPlayer;

	private int lastNetwork;

	private int lastScene;

	private bool stopped;

	private int contextErrors;

	private string firstContextError = "";

	private readonly bool allocationsAvailable;

	private readonly bool apiAdmitted;

	private static readonly FieldInfo? EnvParticles = Field(typeof(EnvMan), "m_currentPSystems", typeof(GameObject[]));

	private static readonly FieldInfo? EnvObject = Field(typeof(EnvMan), "m_currentEnvObject", typeof(GameObject));

	private static readonly FieldInfo? Water = Field(typeof(Character), "m_waterEffects_instances", typeof(GameObject[]));

	private static readonly FieldInfo? Slide = Field(typeof(Character), "m_slideEffects_instances", typeof(GameObject[]));

	private static readonly FieldInfo? Flying = Field(typeof(Character), "m_flyingEffects_instances", typeof(GameObject[]));

	private static FieldInfo? Field(Type type, string name, Type expected)
	{
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
		if (!(field?.FieldType == expected))
		{
			return null;
		}
		return field;
	}

	internal ParticleCensus(bool allocationsAvailable)
	{
		this.allocationsAvailable = allocationsAvailable;
		apiAdmitted = typeof(Character).Assembly.ManifestModule.ModuleVersionId.ToString() == "7ac23c58-e834-4776-b9c1-ae8a703440f1" && typeof(UnityEngine.Object).Assembly.ManifestModule.ModuleVersionId.ToString() == "ae1a76aa-edb9-41e4-918d-03433c7d36d1" && typeof(ParticleSystem).Assembly.ManifestModule.ModuleVersionId.ToString() == "6056054a-642d-4b26-b408-6631249d2a8e" && Application.unityVersion == "6000.0.75f1" && EnvParticles != null && EnvObject != null && Water != null && Slide != null && Flying != null;
		SceneManager.sceneUnloaded += SceneUnloaded;
		SceneManager.activeSceneChanged += ActiveSceneChanged;
	}

	internal void Begin(long ticks)
	{
		start = ticks;
	}

	private void SceneUnloaded(Scene scene)
	{
		generation++;
		plan.InvalidateResidence();
	}

	private void ActiveSceneChanged(Scene before, Scene after)
	{
		generation++;
		plan.InvalidateResidence();
	}

	internal void Sample(int frame, long ticks, Player? player, int biome, string? environment, bool focused)
	{
		try
		{
			SampleCore(frame, ticks, player, biome, environment, focused);
		}
		catch (Exception ex)
		{
			plan.InvalidateResidence();
			if (contextErrors < int.MaxValue)
			{
				contextErrors++;
			}
			if (firstContextError.Length == 0)
			{
				firstContextError = Text(ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	private void SampleCore(int frame, long ticks, Player? player, int biome, string? environment, bool focused)
	{
		if (stopped || start == 0L || !apiAdmitted)
		{
			return;
		}
		if (plan.Attempts.Count == 3)
		{
			bool flag = true;
			foreach (ParticleCensusPlan.Attempt attempt2 in plan.Attempts)
			{
				flag &= attempt2.ExclusionEndTicks.HasValue;
			}
			if (flag)
			{
				return;
			}
		}
		if (Thread.CurrentThread.ManagedThreadId != mainThread)
		{
			throw new InvalidOperationException("Particle census must run on the recording game thread.");
		}
		Scene activeScene = SceneManager.GetActiveScene();
		int num = ((player != null) ? player.GetInstanceID() : 0);
		int num2 = ((ZNet.instance != null) ? ZNet.instance.GetInstanceID() : 0);
		if (num != lastPlayer || num2 != lastNetwork || activeScene.handle != lastScene)
		{
			generation++;
			lastPlayer = num;
			lastNetwork = num2;
			lastScene = activeScene.handle;
			plan.InvalidateResidence();
		}
		bool outdoors = player != null && !player.InInterior();
		bool loading = player == null || num2 == 0 || ZNetScene.instance == null || ZoneSystem.instance == null || !activeScene.IsValid() || !activeScene.isLoaded || player.IsTeleporting();
		ParticleCensusPlan.SceneKind scene = ((biome == 1) ? ParticleCensusPlan.SceneKind.Meadows : ((biome == 2 && environment == "SwampRain") ? ParticleCensusPlan.SceneKind.SwampRain : ParticleCensusPlan.SceneKind.Other));
		ParticleCensusPlan.Context context = new ParticleCensusPlan.Context(ticks, frame, generation, scene, environment ?? "", focused, player != null, outdoors, loading);
		ParticleCensusPlan.Attempt? attempt = plan.Observe(context);
		if (attempt == null)
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		long num3 = (allocationsAvailable ? GC.GetAllocatedBytesForCurrentThread() : 0);
		Snapshot snapshot = new Snapshot
		{
			Id = plan.Attempts.Count,
			Frame = frame,
			Generation = generation,
			Trigger = attempt.Trigger.ToString(),
			Environment = Text(environment),
			Scene = Text(activeScene.name),
			SceneHandle = activeScene.handle,
			Biome = biome,
			StartTicks = timestamp,
			Focused = focused,
			HasPlayer = (player != null)
		};
		try
		{
			if (player != null)
			{
				Vector3 position = player.transform.position;
				snapshot.X = position.x;
				snapshot.Y = position.y;
				snapshot.Z = position.z;
			}
			Census(snapshot, timestamp);
			snapshot.UnstableScene = generation != snapshot.Generation || SceneManager.GetActiveScene().handle != snapshot.SceneHandle || Player.m_localPlayer != player;
			snapshot.TimeBudgetReached |= ElapsedMs(timestamp) > 50.0;
			snapshot.Truncated = snapshot.NotAdmitted > 0 || snapshot.SystemCapReached || snapshot.CharacterCapReached || snapshot.BindingCapReached || snapshot.AncestorCapReached || snapshot.OwnershipTraversalTruncated;
			snapshot.State = (snapshot.UnstableScene ? "unstable_scene" : (snapshot.Truncated ? "completed_truncated" : (snapshot.TimeBudgetReached ? "completed_budget_overrun" : "completed")));
		}
		catch (Exception ex)
		{
			snapshot.State = "failed";
			snapshot.Error = Text(ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			snapshot.Admitted = snapshot.Rows.Count;
			snapshot.NotAdmitted = Math.Max(0, snapshot.Enumerated - snapshot.Admitted - snapshot.Destroyed - snapshot.NonSceneSkipped - snapshot.DuplicateIds - snapshot.RowErrors);
			snapshot.EndTicks = Stopwatch.GetTimestamp();
			snapshot.DurationMilliseconds = (double)(snapshot.EndTicks - snapshot.StartTicks) * 1000.0 / (double)Stopwatch.Frequency;
			snapshot.AllocatedBytes = (allocationsAvailable ? new long?(GC.GetAllocatedBytesForCurrentThread() - num3) : ((long?)null));
			snapshots.Add(snapshot);
			plan.Finish(attempt, timestamp, snapshot.EndTicks, snapshot.State.StartsWith("completed", StringComparison.Ordinal), snapshot.State);
		}
	}

	private void Census(Snapshot snapshot, long began)
	{
		ParticleSystem[] array = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
		snapshot.Enumerated = array.Length;
		Dictionary<int, Owner> dictionary = new Dictionary<int, Owner>();
		Dictionary<int, Owner> cache = new Dictionary<int, Owner>();
		BuildBindings(snapshot, dictionary, began);
		HashSet<int> hashSet = new HashSet<int>();
		int num = Math.Min(array.Length, 8192);
		for (int i = 0; i < num; i++)
		{
			if (ElapsedMs(began) > 50.0)
			{
				snapshot.TimeBudgetReached = true;
				break;
			}
			ParticleSystem system = array[i];
			if (system == null)
			{
				snapshot.Destroyed++;
				continue;
			}
			try
			{
				GameObject gameObject = system.gameObject;
				if (!gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
				{
					snapshot.NonSceneSkipped++;
					continue;
				}
				int instanceID = system.GetInstanceID();
				if (!hashSet.Add(instanceID))
				{
					snapshot.DuplicateIds++;
					continue;
				}
				Transform transform = system.transform;
				Owner owner = ResolveOwner(transform, dictionary, cache, snapshot);
				Row row = new Row(owner)
				{
					SystemId = instanceID,
					SceneHandle = gameObject.scene.handle,
					RootId = transform.root.gameObject.GetInstanceID(),
					SystemName = Text(system.name),
					Active = gameObject.activeInHierarchy
				};
				row.Playing = Read(() => system.isPlaying, row);
				row.Stopped = Read(() => system.isStopped, row);
				row.Paused = Read(() => system.isPaused, row);
				row.Emission = Read(() => system.emission.enabled, row);
				row.SimulationSpace = ReadText(() => system.main.simulationSpace.ToString(), row);
				row.CullingMode = ReadText(() => system.main.cullingMode.ToString(), row);
				row.Unscaled = Read(() => system.main.useUnscaledTime, row);
				row.InheritVelocity = Read(() => system.inheritVelocity.enabled, row);
				row.Collision = Read(() => system.collision.enabled, row);
				row.Trails = Read(() => system.trails.enabled, row);
				row.Lights = Read(() => system.lights.enabled, row);
				row.SubEmitters = Read(() => system.subEmitters.enabled, row);
				ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
				if (renderer != null)
				{
					row.RendererId = renderer.GetInstanceID();
					row.RendererEnabled = Read(() => renderer.enabled, row);
					row.LastVisible = Read(() => renderer.isVisible, row);
				}
				else
				{
					row.Qualification += ";renderer_absent";
				}
				snapshot.Rows.Add(row);
				snapshot.ReadErrors += row.ReadErrors;
			}
			catch (MissingReferenceException)
			{
				snapshot.Destroyed++;
			}
			catch (Exception ex2)
			{
				snapshot.RowErrors++;
				if (snapshot.Error.Length == 0)
				{
					snapshot.Error = Text(ex2.GetType().Name);
				}
			}
		}
		snapshot.Admitted = snapshot.Rows.Count;
		snapshot.SystemCapReached = array.Length > 8192;
		snapshot.NotAdmitted = array.Length - snapshot.Rows.Count - snapshot.Destroyed - snapshot.NonSceneSkipped - snapshot.DuplicateIds - snapshot.RowErrors;
		if (snapshot.NotAdmitted < 0)
		{
			throw new InvalidOperationException("Particle census accounting invalid.");
		}
		snapshot.OwnershipFields = "EnvParticles=" + (EnvParticles != null) + ";EnvObject=" + (EnvObject != null) + ";Water=" + (Water != null) + ";Slide=" + (Slide != null) + ";Flying=" + (Flying != null);
	}

	private void BuildBindings(Snapshot snapshot, Dictionary<int, Owner> map, long began)
	{
		EnvMan instance = EnvMan.instance;
		if (instance != null)
		{
			EnvSetup currentEnvironment = instance.GetCurrentEnvironment();
			BindArray(map, EnvParticles?.GetValue(instance) as GameObject[], new Owner("env_current_particle_root", instance.GetInstanceID()), snapshot);
			Bind(map, EnvObject?.GetValue(instance) as GameObject, new Owner("env_current_object_root", instance.GetInstanceID()), snapshot);
			if (currentEnvironment != null)
			{
				BindArray(map, currentEnvironment.m_psystems, new Owner("env_definition_particle_root", instance.GetInstanceID()), snapshot, onlyIfAbsent: true);
				Bind(map, currentEnvironment.m_envObject, new Owner("env_definition_object_root", instance.GetInstanceID()), snapshot, onlyIfAbsent: true);
			}
		}
		List<Character> allCharacters = Character.GetAllCharacters();
		snapshot.CharactersObserved = allCharacters.Count;
		int num = Math.Min(allCharacters.Count, 512);
		for (int i = 0; i < num; i++)
		{
			if (ElapsedMs(began) > 50.0)
			{
				snapshot.TimeBudgetReached = true;
				snapshot.OwnershipTraversalTruncated = true;
				break;
			}
			snapshot.CharactersVisited++;
			Character character = allCharacters[i];
			if (!(character == null))
			{
				try
				{
					Owner owner = new Owner("character_effect_root", character.GetInstanceID());
					owner.Name = Text(character.name);
					SetPrefab(character.GetComponent<ZNetView>(), owner);
					BindEffects(map, character, Water, "water_or_tar_instances", owner, References(character.m_waterEffects) + "|tar-current:" + References(character.m_tarEffects), snapshot);
					BindEffects(map, character, Slide, "slide_instances", owner, References(character.m_slideEffects), snapshot);
					BindEffects(map, character, Flying, "flying_instances", owner, References(character.m_flyingContinuousEffect), snapshot);
				}
				catch (MissingReferenceException)
				{
					snapshot.DestroyedOwners++;
				}
				catch (Exception)
				{
					snapshot.OwnerErrors++;
				}
			}
		}
		snapshot.CharacterCapReached = allCharacters.Count > 512;
	}

	private static string References(EffectList effects)
	{
		if (effects == null || effects.m_effectPrefabs == null)
		{
			return "unknown";
		}
		StringBuilder stringBuilder = new StringBuilder();
		EffectList.EffectData[] effectPrefabs = effects.m_effectPrefabs;
		for (int i = 0; i < Math.Min(effectPrefabs.Length, 24); i++)
		{
			EffectList.EffectData effectData = effectPrefabs[i];
			if (i > 0)
			{
				stringBuilder.Append(';');
			}
			stringBuilder.Append(i).Append(':');
			if (effectData == null)
			{
				stringBuilder.Append("null_entry");
				continue;
			}
			GameObject prefab = effectData.m_prefab;
			stringBuilder.Append(effectData.m_enabled ? "enabled:" : "disabled:");
			stringBuilder.Append((prefab != null) ? (prefab.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ":" + Text(prefab.name)) : "missing_prefab");
		}
		if (effectPrefabs.Length > 24)
		{
			stringBuilder.Append(";references_truncated:").Append(effectPrefabs.Length);
		}
		return stringBuilder.ToString();
	}

	private static void BindEffects(Dictionary<int, Owner> map, Character character, FieldInfo? field, string slot, Owner basis, string references, Snapshot snapshot)
	{
		if (field == null)
		{
			snapshot.MissingOwnershipFields++;
			return;
		}
		Owner owner = basis.Copy();
		owner.Slot = slot;
		owner.References = references;
		BindArray(map, field.GetValue(character) as GameObject[], owner, snapshot);
	}

	private static void BindArray(Dictionary<int, Owner> map, GameObject[]? roots, Owner owner, Snapshot snapshot, bool onlyIfAbsent = false)
	{
		if (roots != null)
		{
			for (int i = 0; i < Math.Min(roots.Length, 2048); i++)
			{
				Owner owner2 = owner.Copy();
				owner2.InstanceSlot = i;
				Bind(map, roots[i], owner2, snapshot, onlyIfAbsent);
			}
			if (roots.Length > 2048)
			{
				snapshot.BindingCapReached = true;
			}
		}
	}

	private static void Bind(Dictionary<int, Owner> map, GameObject? root, Owner owner, Snapshot snapshot, bool onlyIfAbsent = false)
	{
		if (root == null || !root.scene.IsValid() || !root.scene.isLoaded)
		{
			return;
		}
		int instanceID = root.transform.GetInstanceID();
		if (map.TryGetValue(instanceID, out Owner value))
		{
			if (!onlyIfAbsent && (value.Id != owner.Id || value.Slot != owner.Slot || value.InstanceSlot != owner.InstanceSlot))
			{
				value.Kind = "ambiguous_actual_root_references";
				value.Name = "ambiguous_owner_ids:" + value.Id + "/" + owner.Id;
				value.Id = 0;
				value.PrefabHash = null;
				value.PrefabName = "unknown_ambiguous_owner";
				value.Slot = Text(value.Slot + "|" + owner.Slot);
				value.InstanceSlot = -1;
				snapshot.AmbiguousBindings++;
			}
		}
		else if (map.Count >= 2048)
		{
			snapshot.BindingCapReached = true;
		}
		else
		{
			owner.RootId = root.GetInstanceID();
			map.Add(instanceID, owner);
		}
	}

	private static Owner ResolveOwner(Transform transform, Dictionary<int, Owner> bindings, Dictionary<int, Owner> cache, Snapshot snapshot)
	{
		List<int> list = new List<int>(8);
		Transform? transform2 = transform;
		Owner? value = null;
		ZNetView? zNetView = null;
		int num = 0;
		while (transform2 != null && num < 64)
		{
			int instanceID = transform2.GetInstanceID();
			if (bindings.TryGetValue(instanceID, out value) || cache.TryGetValue(instanceID, out value))
			{
				break;
			}
			list.Add(instanceID);
			if (zNetView == null)
			{
				zNetView = transform2.GetComponent<ZNetView>();
			}
			if (num == 63 && transform2.parent != null)
			{
				snapshot.AncestorCapReached = true;
			}
			num++;
			transform2 = transform2.parent;
		}
		if (value == null)
		{
			value = new Owner((zNetView != null) ? "network_ancestor" : "unclassified_scene_root", (zNetView != null) ? zNetView.GetInstanceID() : 0);
			value.RootId = transform.root.gameObject.GetInstanceID();
			value.Name = Text(transform.root.name);
			if (zNetView != null)
			{
				value.RootId = zNetView.gameObject.GetInstanceID();
				value.Name = Text(zNetView.name);
				SetPrefab(zNetView, value);
			}
		}
		if (value.Kind != "network_ancestor" && value.Kind != "unclassified_scene_root")
		{
			foreach (int item in list)
			{
				cache[item] = value;
			}
		}
		return value;
	}

	private static void SetPrefab(ZNetView? view, Owner owner)
	{
		if (!(view == null) && view.IsValid() && view.GetZDO() != null)
		{
			owner.PrefabHash = view.GetZDO().GetPrefab();
			GameObject? gameObject = ((ZNetScene.instance != null) ? ZNetScene.instance.GetPrefab(owner.PrefabHash.Value) : null);
			owner.PrefabName = ((gameObject != null) ? Text(gameObject.name) : "unresolved_hash");
		}
	}

	private static bool? Read(Func<bool> getter, Row row)
	{
		try
		{
			return getter();
		}
		catch (Exception)
		{
			row.ReadErrors++;
			return null;
		}
	}

	private static string ReadText(Func<string> getter, Row row)
	{
		try
		{
			return getter();
		}
		catch (Exception)
		{
			row.ReadErrors++;
			return "unknown";
		}
	}

	private static string Text(string? value)
	{
		value = value ?? "";
		if (value.Length > 128)
		{
			return value.Substring(0, 128) + "[truncated]";
		}
		return value;
	}

	private static double ElapsedMs(long began)
	{
		return (double)(Stopwatch.GetTimestamp() - began) * 1000.0 / (double)Stopwatch.Frequency;
	}

	internal void End(long ticks)
	{
		if (!stopped)
		{
			stopped = true;
			SceneManager.sceneUnloaded -= SceneUnloaded;
			SceneManager.activeSceneChanged -= ActiveSceneChanged;
			plan.Stop(ticks, apiAdmitted ? "stopped" : "unsupported_runtime_identity_or_fields");
		}
	}

	public void Dispose()
	{
		End(Stopwatch.GetTimestamp());
	}

	internal void Save(string directory)
	{
		if (!stopped)
		{
			throw new InvalidOperationException("Census must be ended on game thread before save.");
		}
		using (StreamWriter streamWriter = Writer(directory, "particle-census.csv"))
		{
			streamWriter.WriteLine("SnapshotId,FrameIndex,Generation,Trigger,StartSeconds,EndSeconds,Scene,SceneHandle,Biome,Environment,SystemId,SystemName,SystemSceneHandle,SceneRootId,OwnerKind,OwnerId,OwnerRootId,OwnerName,EffectArray,InstanceSlot,OwnerPrefabHash,OwnerPrefabName,CurrentPrefabReferences,ActiveHierarchy,Playing,Stopped,Paused,EmissionEnabled,SimulationSpace,CullingMode,UseUnscaledTime,InheritVelocity,Collision,Trails,Lights,SubEmitters,RendererId,RendererEnabled,LastVisible,ReadErrors,Qualification");
			foreach (Snapshot snapshot in snapshots)
			{
				foreach (Row row in snapshot.Rows)
				{
					streamWriter.WriteLine(Csv(snapshot.Id, snapshot.Frame, snapshot.Generation, snapshot.Trigger, Seconds(snapshot.StartTicks), Seconds(snapshot.EndTicks), snapshot.Scene, snapshot.SceneHandle, snapshot.Biome, snapshot.Environment, row.SystemId, row.SystemName, row.SceneHandle, row.RootId, row.Owner.Kind, row.Owner.Id, row.Owner.RootId, row.Owner.Name, row.Owner.Slot, row.Owner.InstanceSlot, row.Owner.PrefabHash, row.Owner.PrefabName, row.Owner.References, row.Active, row.Playing, row.Stopped, row.Paused, row.Emission, row.SimulationSpace, row.CullingMode, row.Unscaled, row.InheritVelocity, row.Collision, row.Trails, row.Lights, row.SubEmitters, row.RendererId, row.RendererEnabled, row.LastVisible, row.ReadErrors, row.Qualification));
				}
			}
		}
		using (StreamWriter streamWriter2 = Writer(directory, "particle-groups.csv"))
		{
			streamWriter2.WriteLine("SnapshotId,OwnerKind,OwnerId,OwnerRootId,EffectArray,OwnerPrefabHash,StateModuleKey,SystemCount");
			foreach (Snapshot snapshot2 in snapshots)
			{
				Dictionary<string, int> dictionary = new Dictionary<string, int>();
				foreach (Row row2 in snapshot2.Rows)
				{
					string key = Csv(snapshot2.Id, row2.Owner.Kind, row2.Owner.Id, row2.Owner.RootId, row2.Owner.Slot, row2.Owner.PrefabHash, Csv(row2.Active, row2.Playing, row2.Stopped, row2.Paused, row2.Emission, row2.SimulationSpace, row2.CullingMode, row2.Unscaled, row2.InheritVelocity, row2.Collision, row2.Trails, row2.Lights, row2.SubEmitters, row2.RendererEnabled, row2.LastVisible, row2.ReadErrors));
					dictionary.TryGetValue(key, out var value);
					dictionary[key] = value + 1;
				}
				int num = 0;
				foreach (KeyValuePair<string, int> item in dictionary)
				{
					streamWriter2.WriteLine(item.Key + "," + item.Value.ToString(CultureInfo.InvariantCulture));
					num += item.Value;
				}
				if (num != snapshot2.Rows.Count)
				{
					throw new InvalidOperationException("Census group totals do not reproduce admitted rows.");
				}
			}
		}
		using (StreamWriter streamWriter3 = Writer(directory, "particle-census-exclusions.csv"))
		{
			streamWriter3.WriteLine("SnapshotId,StartSeconds,EndSeconds,StartFrame,EndFrame,Reason");
			for (int i = 0; i < plan.Attempts.Count; i++)
			{
				ParticleCensusPlan.Attempt attempt = plan.Attempts[i];
				streamWriter3.WriteLine(Csv(i + 1, Seconds(attempt.ExclusionStartTicks), Seconds(attempt.ExclusionEndTicks ?? attempt.EndTicks ?? attempt.RequestedTicks), attempt.ExclusionStartFrame, attempt.GuardEndFrame, "particle_census_plus_guard_frames"));
			}
		}
		Quality quality = new Quality
		{
			Snapshots = snapshots,
			ContextErrors = contextErrors,
			FirstContextError = firstContextError,
			ApiAdmitted = apiAdmitted,
			StopwatchFrequency = Stopwatch.Frequency,
			RecorderStopwatchStart = start,
			OwnershipIdentity = typeof(Character).Assembly.ManifestModule.ModuleVersionId.ToString(),
			CoreModuleIdentity = typeof(UnityEngine.Object).Assembly.ManifestModule.ModuleVersionId.ToString(),
			ParticleModuleIdentity = typeof(ParticleSystem).Assembly.ManifestModule.ModuleVersionId.ToString()
		};
		foreach (ParticleCensusPlan.MissingCheckpoint item2 in plan.Missing)
		{
			quality.Missing.Add(item2.Trigger.ToString() + ":" + item2.Reason);
		}
		File.WriteAllText(Path.Combine(directory, "particle-census-quality.json"), RecorderPlugin.Json(quality), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private double Seconds(long ticks)
	{
		return (double)(ticks - start) / (double)Stopwatch.Frequency;
	}

	private static StreamWriter Writer(string directory, string name)
	{
		return new StreamWriter(new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private static string Csv(params object?[] values)
	{
		string[] array = new string[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			object? obj = values[i];
			string text = ((obj == null) ? "unknown" : ((!(obj is bool)) ? ((obj is IFormattable formattable) ? formattable.ToString(null, CultureInfo.InvariantCulture) : (obj.ToString() ?? "")) : (((bool)obj) ? "1" : "0")));
			array[i] = "\"" + text.Replace("\"", "\"\"") + "\"";
		}
		return string.Join(",", array);
	}
}
