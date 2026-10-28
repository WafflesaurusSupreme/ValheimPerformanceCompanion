# Valheim Performance Companion 0.1.10

A Windows x64 WPF companion with a standalone BepInEx recorder, PerfView capture, offline interpretation, comparison and report export. Data stays on your computer unless you export and share it yourself.

Version 0.1.10 connects to a compatible recorder already running inside Valheim before checking recorder installation files. Read-only recorder replies remain available when Unity pauses its update loop. Native GC clock alignment from 0.1.9 is retained.
It maps the Mono stopwatch capture anchor to the native QPC origin, preserving
the frame clock and callback phase semantics. Invalid or uncertain alignment
disables native phase timing for that capture and writes a quality warning.
No PerformancePack integration is required. Historical captures without a
trustworthy clock anchor are not silently rebased.

## Use

1. Run `ValheimPerformanceCompanionLauncher.exe` from the extracted release folder for startup dependency checks. It starts the desktop application, `ValheimPerformanceCompanion.exe`, displayed by Windows as **Valheim Performance Companion**. Keep the complete extracted folder together.

   The complete folder can be moved anywhere you can read/run it. Dependencies are resolved beside the executable, independently of the current working directory. Settings are kept under the current user's LocalAppData; no development-machine path is required.
2. Select your Valheim installation/profile. Steam registry entries, Steam library files, Thunderstore and r2modman profile folders are discovered automatically. Add custom locations using **Locate game** or **Add profile folder**.
3. Recorder setup is automatic for the selected profile while Valheim is closed. Existing recorder versions are backed up outside the plugins directory. BepInEx must already exist in the selected profile. If Valheim is already running, the app first connects to its loaded recorder and verifies its actual profile; an older compatible recorder or an initially unknown profile does not force a restart. Updates are deferred until the game closes. BepInEx loads plugins during game startup, so a session started before the recorder's first installation still needs one normal relaunch after setup. The companion does not inject or hot-replace plugins in a running game.
4. Launch Valheim through your normal mod manager. Either the companion or Valheim can start first once the recorder is installed. The app refreshes every **30 seconds** until it finds the selected profile's PID, then connects automatically. At startup, a single detected running profile takes priority over an old saved selection. An explicit selection in the current session is preserved across refreshes. It never borrows the PID of another profile. When several closed-game candidates exist and there is no previous selection, select one first. A recorder handshake verifies the running profile. With the 0.1.10 recorder, hello/status requests can use its last game-thread snapshot if Unity is not processing frames; capture changes still require the game thread. Bring Valheim forward if an older recorder or a capture-changing command is waiting for a game frame.
5. Select Monitor, Standard or Deep and a limit from 5 to 600 seconds. Start, play normally, then stop to open the results. Lag spikes are identified automatically; no player marking is required.

The app must be running and connected for console commands. Add `-console` to Valheim's launch options and use F5:

```text
perf start standard
perf start monitor
perf start deep
perf status
perf stop
```

The GUI and console control the same capture session. Standard and Deep request Windows elevation for a bounded collector helper. The game, normal GUI and analysis do not need administrator rights. Console operations may take time while PerfView starts or finalizes. A pre-existing `perf` command is preserved rather than overwritten.

Standard and Deep prepare the recorder and wait for PerfView readiness before starting the selected measurement duration. Preparation is bounded separately. If an acknowledgement is lost, the app reconciles the exact session and attempts cleanup; if the recorder remains unreachable, Stop stays available to retry. Another session is never stopped as part of this recovery.

## Capture modes

| Mode | Collection |
| --- | --- |
| Monitor | Every retained game-loop interval, focus/player state, GC counter, biome, available Unity CPU/GPU counters and game log. No ETW. Maximum 600 seconds. |
| Standard | Monitor plus bounded PerfView CPU sampling. Maximum 600 seconds. |
| Deep | Standard plus method/character probes and disk/context-switch ETW events. Maximum 600 seconds. Higher overhead. Deep per-frame details retain up to 360,000 frames; streaming frame intervals and method windows continue if that buffer fills, with a quality warning. |

The live graph shows one observation per second; the saved recording contains every retained frame. Buffers and ETW file size are bounded. Frame CSVs flush approximately once per second on a background writer. A crash can lose the last unflushed frames; imports recover the valid prefix of incomplete companion recordings. Deep method details are buffered until Stop and can be lost on a game crash. Missing completion receipts, dropped samples and partial collector failures are shown as quality warnings.

The older recorder embedded in Riverheim installs probes independently. All companion modes remain available. Companion Deep uses a separate Harmony owner and removes only its own probes and player-loop callbacks on stop. Legacy instrumentation stays untouched and can add measurement overhead; the app reports its presence. Existing Riverheim capture CSVs are importable without installing the new recorder.

## Results and interpretation

Version 0.1.6 adds a code-investigation section and a self-contained Codex evidence export. Every detected spike is retained, along with recurring GC and sustained-load findings. Deep adds native GC phase timing and an after-recording managed IL snapshot with assembly MVIDs, method tokens and hashes. All Harmony patch relationships are inventoried. CPU sampling identifies executed paths; a hot method or installed patch alone does not prove inefficient code.

Allocation call stacks are unavailable in this release: enabling that Mono facility requires startup instrumentation. GPU/presentation delays, native methods, generated patch replacements and old recordings may also lack exact code. Each issue states its evidence and gaps. A fresh Deep capture is needed for the new code snapshot and GC timestamps; opening an old recording cannot reconstruct missing evidence. GC timing covers the suspended-world phase and excludes stop/restart overhead.

Open a `frames*.csv`, or load the capture directory through the application API. Where a directory contains several recordings, select the specific frames CSV. The app discovers recordings in the selected profile and remembers recordings opened from elsewhere. It restores the last Results recording and its last comparison on launch; use **Open recent** or **Open previous comparison** for earlier work. The recordings remain in their original folders, while the recent list is stored under `%LOCALAPPDATA%/ValheimPerformanceCompanion/recent-captures.json`. Click a spike to inspect overlapping method windows and timestamped log events; use the wheel to zoom and double-click to reset. Range and state filters recalculate the results. Compare applies the same selected range/filters to both recordings.

- Average FPS = number of positive included intervals / sum of their duration in seconds.
- 1% low = 1000 / mean of the slowest `ceil(frameCount × .01)` intervals.
- Percentiles use linear interpolation.
- Large lag spike = interval greater than `max(50 ms, 3 × selected frame budget)`.
- Relative spike = interval greater than `max(8 ms, 2.5 × preceding up-to-60-frame median)`, after at least ten observations.
- Time over budget sums the full duration of over-budget intervals, not only the excess.
- State exclusions apply only where a state was recorded. Unknown state is retained and reported.

The recorder measures game-loop intervals, not frames actually presented to the display. Unity counters may describe a previous completed frame. Method measurements are inclusive and nested; never sum them as independent costs. Collection-count changes do not measure GC pause duration. Findings distinguish observations from associations and give a next test, not a guaranteed diagnosis. An observational comparison is not a controlled benchmark.

PerfView 3.2.6 is pinned and SHA-256 checked before execution. ETW captures are machine-wide even when user-mode providers are focused on the game. Microsoft TraceEvent 3.2.6 reads the merged trace directly, selects the exact process ID and aligns absolute ETW timestamps to the recorder's UTC/Stopwatch anchor. This avoids PerfView's fragile XML export (verified to fail on control characters in method names). CPU samples overlapping a frame are observations, not exact method durations or proof of causality. Missing symbols remain module/address labels; event loss and missing stacks are reported. Trace conversion and local symbol processing require no cloud service. Existing PerfView XML imports retain independent validation. Use **Open in PerfView** for deeper inspection. New captures use sequential ETW files to preserve the beginning. Collection requires 20 GiB free before starting and stops at 5 GiB of live ETL files or below 3 GiB free. The 5 GiB bound is a starting budget for a 300-second workload, not a full-duration guarantee. collector-end.json records the exact end reason, observed byte counts and elapsed time; resource stops also retain a quality warning. Merged ETL analysis still has a separate 4 GiB limit; CPU tables retain their one-million-sample/512 MiB limits. Live ETL sizes differ from merged output sizes. Larger or busier captures can still produce partial coverage. Older circular captures can contain only the end; zero lost events does not establish full coverage.

Log interpretation recognizes exceptions, generation, spawning and save messages. ISO timestamps with explicit offsets can be aligned. Local-time-only or untimestamped lines stay unaligned. Unrecognized messages remain in the original log; the app does not invent timestamps or blame a mod just because it patches a method.

## Files and privacy

Recordings are stored under the connected game's `BepInEx/PerformanceCompanion/Captures` directory. Each includes recorder metadata, frames, logs and, where available, Deep CSVs and raw ETW. A bounded helper stops its own uniquely named ETW session when its time limit expires or the game/owning app exits. Do not forcibly terminate the elevated helper during trace finalization.

Exports create a new ZIP containing HTML, metrics, every detected spike, a redacted event summary, diagnostics.json, CODEX-INVESTIGATION.md, matching managed IL and redacted measurement CSVs. Existing files are never overwritten. Review the sharing preview. Raw recorder files, logs and ETW require a separate opt-in and are **not redacted**. Automatic redaction covers common user paths, IPv4 addresses, Steam account IDs and token/password fields, not every possible private string.

Custom discovery locations are saved under `%LOCALAPPDATA%/ValheimPerformanceCompanion/settings.json`. Closed-game manager selections are not inferred from recent file timestamps. Relocated profile roots can be added manually; a running game's Doorstop path or recorder handshake can find them automatically. Capture metadata fingerprints loaded plugin identities and top-level BepInEx `.cfg` contents without copying those settings into the report. Capture-time configuration reads are not an atomic snapshot of files modified concurrently.

## Signing and antivirus detections

The no-cost portable build is unsigned. A clean local scan does not establish that Google Drive or another antivirus vendor will accept it. A self-signed test certificate is not a public trust solution; this build does not install certificates, change antivirus settings, use a signing service, or incur a charge by default.

Optional signing uses an existing code-signing certificate in the current user's Personal store. Pass `-SigningCertificateThumbprint`, `-SignToolPath` and `-TimestampServer` to `Build.ps1`. After all build steps, `SignRelease.ps1` signs the two app executables, the app/Core assemblies and the recorder with SHA-256, requires a timestamp, and verifies the selected publisher before packaging. It leaves third-party signatures untouched and rejects self-signed test certificates. This support does not provision a signing identity or guarantee antivirus clearance. Embedded native GC helpers are not separately signed by this step.

For a disputed malware detection, retain the exact release archive and file SHA-256 values and submit the detected file to the vendor for review. Google Drive's file owner can use Share → Request a review; an unanswered request is not evidence that the file has been cleared. Microsoft submission guidance: https://learn.microsoft.com/en-us/defender-xdr/developer-faq

## Build

The native startup launcher is built with project-local Zig 0.16.0, using C++17 and statically linked native libraries. Extract the official `zig-x86_64-windows-0.16.0.zip` from https://ziglang.org/download/0.16.0/zig-x86_64-windows-0.16.0.zip under `.tools/zig-0.16.0`. Verify SHA-256 `68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e` before extraction. No system-wide compiler installation is needed.

The desktop targets .NET 10; the recorder targets .NET Standard 2.1 for the installed Unity/Mono host. No third-party GUI packages are used. Microsoft .NET SDK 10.0.401 is pinned by `global.json`; the project-local SDK is under `.tools/dotnet` on the development machine. The .NET Standard 2.1 reference pack is required. PerfView's SHA-256 is pinned in `PerfViewCollector.cs` and recorded in `THIRD-PARTY-NOTICES.md`.

Create `local.paths.props` in the project root with `GamePath` pointing to the Valheim folder and `BepInExPath` pointing to the selected profile's `BepInEx/core`. These are build references only and are excluded from the portable application package. Host-provided references are explicitly marked externally resolved to avoid traversing unrelated game framework dependencies during build. GitHub builds download their reference/runtime snapshot separately as described in [GITHUB_BUILD.md](GITHUB_BUILD.md); players do not need that CI download.

Run `Build.ps1 -GamePath <path> -BepInExPath <path>`. It builds all three projects, runs the regression suite and publishes a portable Windows x64 release with the .NET runtime. The first portable publish restores Microsoft's version-pinned runtime packs from nuget.org. `-LegacyCapture` optionally verifies one existing recording.

`-SkipGuiTests` explicitly skips tests that open a WPF window; headless core, recovery and Mono checks still run. Framework-dependent packaging is unsupported and rejected before building because the startup launcher requires the bundled runtime.

`TestMono.ps1 -GamePath <path> -BepInExPath <core-path>` runs transport and actual recorder metadata serialization regressions in an isolated process using that game's Mono runtime. It is also part of the release build checks. It does not start Valheim or modify any profile. This test requires the Windows Mono game distribution, not a substituted .NET runtime.

## Verification boundary

Version 0.1.2 adds a prominent status panel above the capture controls. It reports detection, setup, connection and specific next actions. Check now searches immediately; otherwise discovery runs every 30 seconds. Start capture enables only after a verified recorder handshake. Application exceptions are logged under `%LOCALAPPDATA%/ValheimPerformanceCompanion/Logs`. The synchronous window-close regression is covered by a real WPF window test.

Version 0.1.3 replaces the pipe security API that throws inside Valheim's Mono runtime with a Windows pipe restricted to the current user. Both recorder and console connections are tested on the game's Mono runtime. Connection retries continue every 30 seconds while the game runs, including long mod-loading periods. A timeout no longer tells players that they launched their profile incorrectly. Recorder server failures are written to the BepInEx log.

The native entry point checks the bundled application, recorder, PerfView and runtime files before starting .NET. Missing files or native loader failures display a startup dialog naming the component and recovery action. Keep the complete extracted folder together; no separate .NET Framework or Visual C++ redistributable installation is needed for this launcher. `--check-startup` performs the checks without opening the GUI: exit 0 means checks passed, 1 means the launcher path could not be resolved, 2 means files are missing, and 3 means a runtime library could not load. Normal launch returns 4 if Windows cannot create the desktop process. Startup checks do not prove gameplay compatibility.

Builds and offline tests do not prove Unity lifecycle, gameplay compatibility or measurement overhead. Review `VERIFICATION.md` for the current evidence. This initial release requires in-game validation before relying on it for performance conclusions. It does not automatically modify performance settings, uninstall mods, launch the game, change saves or upload diagnostics.

## GitHub builds and SignPath

Source repository: https://github.com/WafflesaurusSupreme/ValheimPerformanceCompanion. The SignPath Foundation application was submitted on 28 October 2026; approval is pending and no SignPath-signed release is available yet. The manual **Build portable companion** GitHub Actions workflow builds and tests on a GitHub-hosted Windows runner. It defaults to an unsigned build. See [GITHUB_BUILD.md](GITHUB_BUILD.md) for build-input setup and the approval activation checklist, [CODE-SIGNING.md](CODE-SIGNING.md) for signing scope, and [PRIVACY.md](PRIVACY.md) for recording and export behavior.
