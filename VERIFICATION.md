# Verification — 2026-10-28

## Version 0.1.10 connection recovery and distribution status

Running games are contacted before recorder installation checks. The handshake
verifies the actual game/profile and permits a compatible recorder already loaded
in the game even when its disk copy differs from the portable bundle. No recorder
files are replaced in this path. Closed-game automatic installation is retained.

Read-only requests normally receive a fresh game-thread response. If Unity does
not update, the recorder replies from its last immutable snapshot after at most
250 ms of waiting for the game thread. Old snapshots do not accumulate more
read-only work while Unity remains stalled. A pending capture-changing command
returns an explicit error instead of misleading cached Idle, preserving session
ownership during ambiguous-reply recovery. Start/Prepare/Begin/Stop remain on the
game thread. The client sends requests without a buffered writer whose disposal
could hide a lost reply behind a second broken-pipe exception.

The late-attachment WPF test failed against the old preparation path and passes
with the repair. The actual recorder's no-Update handshake test timed out against
0.1.9. Release gates cover 43 Core tests, 28 recovery/timing/metadata checks, WPF
setup/timer, close, history, slow-start retry, late attachment and disconnect/
reconnect, plus transport, recorder contracts, no-Update recovery and native GC
clock checks on Valheim's actual Mono runtime. No real profile is changed by
these tests. Valheim gameplay and a live capture with this version remain
unverified; no game is started by this release verification.

Microsoft Defender 4.18.26090.9 / engine 1.1.26100.4 / definitions 1.463.13.0
reported no threats in the original 0.1.9 portable directory on 2026-10-28, with
remediation disabled. This does not establish clearance from Google Drive or
other vendors. The no-cost release remains unsigned; optional explicit signing
requires an existing trusted certificate and does not provision a service,
purchase anything, install trust roots, or modify antivirus settings. The
self-signed test certificate is rejected before signing/network operations.
Google Drive review and other vendors' detections remain unresolved.

## Version 0.1.9 native GC clock repair

The clock correction belongs directly in Performance Companion. Its recorder now
maps the Mono capture anchor to the Windows QPC origin before starting native GC
phase collection. Frame timing, callback phase semantics and protocol remain
unchanged. Invalid clock observations or differing frequencies disable native
phase timing for that capture with an explicit quality warning. Three isolated
captures on Valheim's actual Mono runtime produced 24 native events within the
measured capture bounds. Invalid-anchor capture startup remained operational and
exported no misleading GC timestamps. No PerformancePack clock patch is used.
App, recorder and launcher versions are 0.1.9. Full profile startup and a fresh
in-game capture remain pending; the game was not launched.

## Version 0.1.8 collector coverage

Core and review probes pass, including collector size/free-space boundaries. The
live ETL budget is now 5 GiB with a 3 GiB reserve and 20 GiB preflight requirement;
the collector writes its end reason and coverage. A full 300-second trace is not
guaranteed. The separate merged-trace limits remain unchanged. The self-contained
Windows build retains the accepted 10.0.12 runtime and exact installed recorder
DLL. App and native launcher versions are 0.1.8. No game was launched. Fresh
elevated collection, GC clock alignment and matched overhead remain pending.

Status: 0.1.7 restores recording and comparison history and repairs report ZIP export. Isolated WPF and real-capture checks pass; a fresh launch of the packaged app, elevated capture, and gameplay verification remain outstanding. The live gameplay results below belong to earlier versions.

## Version 0.1.7 history and export repair

- The four existing Riverheim frame recordings remain in their original capture folders. The latest real recording imported with 25,457 frames, and a redacted 33,747,105-byte report ZIP was generated and opened by the test suite.
- An isolated WPF window reopened a stored Results recording and its comparison, restored comparison options, then exported and opened a report ZIP. The WPF close and automatic discovery tests also passed. No live Valheim profile was modified by these tests.
- The prior export failure came from reading the WPF raw-content checkbox on a worker thread after choosing a ZIP filename. The app now reads the checkbox on the UI thread before starting export.
- Both 0.1.7 executables embed the shared icon at seven sizes (16 through 256 pixels). The native launcher was rebuilt from another working directory, and the packaged startup checker returned exit code 0.
- Google Drive restricted sharing of the earlier 0.1.6 archive under its malware policy without identifying a component. This local build does not establish whether 0.1.7 will be accepted by Drive.

## Version 0.1.6 diagnostic evidence

- Core checks cover all spikes beyond the old twelve-item summary, repeated GC, clipped/unioned pause intervals, main-thread versus worker samples, patch evidence without causal blame, ambiguous overloads, sustained load, invalid code hashes and default export privacy.
- Isolated Valheim Mono checks pass for native GC phase timestamps, buffer reset across repeated captures, exact loaded module identities and original IL with resolved method operands. Existing contract, clock and transport/security checks remain in the build.
- The historical Deep ETL was copied and reprocessed without changing the original: 26,373 CPU samples, 652,213 off-CPU intervals, zero disk completions, 60 samples without stacks, zero reported lost events. The trace still lacks its earlier CPU samples and recorded main-thread identity. The enriched report labels those limitations.
- Sequential ETW collection, the native probe and after-measurement IL snapshots need a fresh in-game capture and matched overhead comparison. No game or desktop app was launched, and no live recorder or mod was replaced for these checks.
- Allocation call stacks remain unavailable; no report asserts an exact allocation producer from collection counts alone. See ADVERSARIAL-REVIEW-0.1.6.md for review findings and remaining limits.

## Version 0.1.5 review repairs

- Reviewed changed source before building. All four previously open findings have code corrections and focused checks; see ADVERSARIAL-REVIEW-0.1.5.md. Existing source, the previous app and recorder backups are retained.
- 35 core checks and 16 recovery/timing/metadata checks pass. These cover lost Start/Prepare/Begin/Stop acknowledgements, an unavailable reconciliation followed by Stop retry, preservation of another session, preparation delayed longer than a five-second capture in both Standard and Deep, collector failure, old-recorder compatibility, duplicate-start collector preservation, and malformed optional JSON.
- Valheim Mono transport/security checks, four actual recorder JSON contracts, and the actual recorder clock test pass in isolated test executables. Preparation is excluded from elapsed measurement and repeated Begin preserves the anchor. The isolated Mono host emits the previously documented cleanup notices after its transport assertions.
- Framework-dependent packaging is rejected before path validation or build work. Script parsing succeeds. Managed and native builds complete without compiler warnings or errors. The first restricted build could not reach NuGet vulnerability data; the authorized network retry completed the audit/restore without disabling checks.
- The startup checker is ValheimPerformanceCompanionLauncher.exe; the desktop and elevated collector are ValheimPerformanceCompanion.exe. Windows version resources are checked without executing either file. Display names have spaces; executable filenames do not. The binaries remain unsigned.
- GUI window tests, launcher execution, elevated collector execution and gameplay replay are skipped at the user's explicit request. These changes still require a later live capture to verify their integration with the game; prior 0.1.4 captures do not establish that.

## Version 0.1.4 capture repair and adversarial review

- The shipped 0.1.3 Receipt, Completion and ConfigurationReceipt types all reproduced InvalidDataContractException under the game's Mono runtime. Explicit DataContract/DataMember attributes repair each actual recorder type while retaining existing JSON member names. Round-trip checks of every public property now pass; StatusData also passes. These checks are part of TestMono.ps1 and the release build.
- Removed the blanket Deep prohibition when the legacy recorder type is loaded. Companion instrumentation has its own Harmony owner and player-loop callback types and does not stop, reconfigure or unpatch the legacy recorder. The app discloses additional instrumentation overhead.
- Full source review completed before the subsequent desktop build. See ADVERSARIAL-REVIEW-0.1.4.md for five additional findings, evidence, recommendations and which findings remain open. The polling failure/reconnection defect was also encountered in live world loading and repaired. The WPF test now verifies automatic reconnection and re-enabled capture after a failed poll.
- All 36 core regression checks pass. Native startup and actual Mono transport/serialization checks pass. The reviewed desktop was installed without replacing the already-loaded 0.1.4 game recorder.
- Live Standard capture on PID 59164: 807 frames, zero dropped samples, 12,179 CPU samples, 20 missing stacks, zero ETW events lost. Recorder/configuration/completion JSON, merged trace, CPU sample analysis and result import all completed.
- Live Deep capture on the same game process: 946 frames, zero dropped samples, 16,584 CPU samples, 36 missing stacks, zero ETW events lost. Deep installed 92 timing probes and 10 Unity counters alongside the loaded legacy recorder. Its output contains 3,239 method rows with measured time and 1,528 character rows. Recorder/configuration/completion JSON, trace analysis, method/character CSVs and result import all completed. No unrelated mod was replaced or disabled.
- Each live check used the running companion's console-control endpoint for Start/Stop and sampled for ten seconds after collector readiness. These were short captures, not full 600-second tests. Evidence receipts are retained under artifacts/live-standard-014.json and artifacts/live-deep-014.json; raw captures remain in the selected profile's BepInEx/PerformanceCompanion/Captures folder.

## Version 0.1.3 Mono communication repair

- The active Thunderstore process command line identified the expected profile; BepInEx confirmed loading recorder 0.1.2. No recorder pipe existed. The same server constructor executed under the installed Valheim Mono runtime threw NotImplementedException from WindowsIdentity.Owner. This established a runtime incompatibility in the recorder, not an incorrect player launch.
- The replacement creates the local asynchronous Windows pipe with a protected current-user DACL and rejects remote clients. Before sending commands, clients verify the pipe server's Windows user; the desktop additionally verifies the actual server PID. No identity restriction was removed to accommodate Mono.
- A cross-runtime regression uses the game's Mono runtime and managed reference assemblies, the production transport source and the actual desktop RecorderClient. It verifies handshake/JSON, console command/reply, pipe cancellation, and the native pipe's current-user-only ACL. These checks passed. The isolated embedded Mono test host emitted abort_threads cleanup notices after completing the checks; it does not run inside or modify the player's game.
- The companion continues bounded connection attempts every 30 seconds instead of giving up after three attempts. Missing responses no longer assert that the player needs to relaunch through a different profile. Actual recorder server failures are logged with their exception.
- All 36 core checks pass, including rejecting a pipe owned by a process other than its advertised PID before transmitting a command. The real WPF timer/handshake/close check and the slow-start retry regression pass. The timer test initially raced its own asynchronous discovery; it now waits for the same busy gate that the actual UI respects before invoking preparation.
- After the player closed Valheim, the updated companion automatically installed recorder 0.1.3 in its selected profile. The player then used Thunderstore Play Modded. BepInEx loaded 0.1.3, and a live read-only handshake with game PID 57004 returned protocol 1, Idle, and empty Error/LastError fields. The running companion's controller endpoint returned `Connected; idle.` This verifies the connection repair in the actual game session. No new gameplay capture was started by these checks.

A recorder update requires the old game process to exit because copying a DLL cannot replace an already loaded assembly. That update/relaunch was completed for this repair; the current game loaded the new recorder normally.

## Version 0.1.2 connection feedback and close repair

- Automatic setup was exercised through the normal desktop startup against the app's selected profile with Valheim closed. Its startup log reported recorder ready; the installed recorder hash matches the bundled version 0.1.2. No profile path is hard-coded in runtime source. At startup a single detected running profile overrides a stale saved selection; an explicit current-session choice remains pinned.
- Native startup checks pass for the complete portable folder. Isolated packages missing coreclr.dll, vcruntime140_cor3.dll or the recorder return exit 2, identify the exact component and explain complete extraction. The native launcher forwards Unicode, spaces, brackets and apostrophes and launches the desktop rendering entry point from a different working directory. These are package/startup checks, not gameplay evidence.

- Reproduced the player's exact `Cannot set Visibility to Visible ... while a Window is closing` exception in a real WPF window before the fix. The close handler awaited cleanup that could complete synchronously, then reentered Close inside the original Closing event. Final Close is now queued after that event returns; additional close requests wait for cleanup.
- Idle-close regression passes after the fix. A separate actual-window integration test waits for the real 30-second timer, supplies a simulated recorder handshake, verifies Connected status and enabled Start capture, and closes with a connected controller. This test passes and never starts the production app's own startup handler or changes real profiles.
- All 35 core checks pass; app publishes and recorder builds with zero warnings/errors.
- Prominent status panel mirrors current operation/instruction, with activity progress and a state indicator. Check now produces immediate detection/connection feedback. Waiting for game closure disables pointless reconnect attempts and explains precisely what to do.
- The separate reported dotnet.exe dialog has not been attributed conclusively. Inspected dotnet processes were MSBuild/.NET compiler processes; no matching Application event was available. Application error logging was added rather than claiming that unrelated dialog was fixed.

## Version 0.1.1 automatic setup update

- 35 core checks passed. New checks cover setup from a relocated bundle path with Unicode/spaces/brackets/apostrophe, idempotence, exact-version reuse while a game is running, deferred install/update, a process-start race, old-DLL backup preservation, and keeping the selected profile rather than borrowing another profile's PID.
- The actual WPF preparation method installed into an isolated fixture profile. Its real DispatcherTimer fired after approximately 30 seconds; the same production preparation path stopped the PID search when supplied a found PID. This check did not write to real profiles.
- Manual setup was removed. Selection triggers setup automatically; game exit is watched while installation is pending. The selected target persists separately from the existing custom-root settings. PID discovery runs every 30 seconds until a match. Recorder handshake retries after PID discovery are separately bounded to allow startup to finish.
- App and recorder release version is 0.1.1. The existing player's extracted 0.1.0 app has not been overwritten. No game was launched or closed by these checks.
- A mod-manager profile is optional for a direct BepInEx installation. Completely unmodded capture still needs an additional capture backend or BepInEx installation and is not claimed supported by this update.

## Verified

- C# WPF desktop, core and BepInEx recorder compile against the installed game assemblies. Recorder build: zero warnings/errors. Self-contained Windows x64 publish: successful.
- 32 automated checks passed, including CSV corruption/recovery, metrics, frame-state filtering, automatic lag spike detection without player annotations, exact interval export, redaction, raw export opt-in, no-overwrite export, discovery argument parsing, process-identity validation and controller start/stop. Tests use isolated folders. The discovery test is explicitly a host integration check.
- Existing real recording imported: 7,909 included frames, 96.3409486 seconds, average 82.0938564 FPS, P99 24.296 ms. Independent PowerShell aggregation agreed with the frame count and average FPS. These measurements describe the existing recording, not a new gameplay session with this tool.
- Steam installation and profile candidates discovered on this machine. While Valheim was running, its Doorstop command line identified Riverheim-Nov-2026 and PID 61424. Closed-game profiles are labelled candidates rather than claimed active. Recorder-handshake identification has a mock integration test; the new plugin has not yet performed a real gameplay handshake.
- Actual elevated collector helper started and stopped its own unique PerfView session around a bounded synthetic process named valheim.exe. The process was a test workload, not the game. Helper exit code 0 and a nonempty trace receipt were recorded under artifacts/helper-lifecycle-test.
- Microsoft TraceEvent imported that actual ETW trace: 199 CPU samples for the exact synthetic PID, one missing stack, zero lost events. Original PerfView XML export was found unreliable on control characters in method names; production analysis reads ETL directly. Raw traces remain available.
- Updated Record and Results views rendered using the packaged WPF app's offscreen rendering mode and inspected visually. This does not constitute mouse/keyboard UI or gameplay testing.
- Console integration defines `perf start [monitor|standard|deep]`, `perf stop`, and `perf status`. GUI and console share one controller. No player marker command, button or hotkey is present. Lag spike detection is automatic; reports include all detected intervals in lag-spikes.csv.
- All modes accept 5–600 seconds; Deep has no shorter duration clamp. Streaming capture does not stop at a frame-count threshold. Deep details have a bounded 360,000-frame buffer and surface a warning if exhausted while interval recording/method windows continue.
- PerfView 3.2.6 matches its pinned SHA-256 and had a valid Authenticode signature when downloaded. NuGet restore/audit completed with no vulnerability warnings. The Core dependency lock is enforced for win-x64. Microsoft license texts, package metadata and dependency lock are included in Licenses.

## Remaining validation

The new recorder was installed automatically into the app-selected live mod profile while Valheim was closed. Unity startup, actual Mono pipe communication, short Standard/Deep capture/stop/import and PerfView collection have now been exercised in the game. A full 600-second capture in each mode, measurement-overhead benchmarks, multiplayer, game shutdown/crash, disk-full behavior and elevation refusal still need runtime validation. The new live tests invoked the same controller endpoint used by the console; they did not type commands through the game's console UI.

Deep mode now remains available with the existing embedded Riverheim recorder, using separate probe ownership and warning about existing measurement overhead. Do not interpret associated CPU/log/GC evidence as a proven cause.

## Scope and retained artifacts

All source, project-local SDK/compiler, dependencies, captures, synthetic workload and test artifacts were created inside ValheimPerformanceCompanion. Automatic setup added this app's recorder to the selected live profile; other mods were unchanged. The player's extracted companion app was backed up before updating. The live game was not launched or closed by this work. Nothing was uploaded. Earlier previews, backups and failed test/export artifacts are retained rather than deleted.

The initial .NET SDK first-run setup generated an ASP.NET development certificate; it was not explicitly trusted. Subsequent build commands disable certificate generation. PerfView was invoked with its AcceptEula option during collector validation. No persistent collector/service or scheduled task was installed.

## Reproduction

Use Build.ps1 with the installed game folder and selected profile's BepInEx/core folder. Supply -LegacyCapture to reproduce the real-recording import check. The script creates a unique artifacts/build-* output and ZIP. Game assemblies are compile references only and are not distributed.
