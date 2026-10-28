using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Companion.Core;
using Microsoft.Win32;
using Frame = Companion.Core.Frame;

namespace Companion.App;

public partial class MainWindow : Window
{
    private readonly CaptureController controller = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer discoveryTimer = new() { Interval = TargetSelection.RefreshInterval };
    private readonly bool preview;
    private GameTarget? preferredTarget;
    private bool updatingTargets, connected, waitingForExit, selectionPinned;
    private int? observedPid;
    private DateTimeOffset? retryConnectionAt;
    private string SelectedTargetFile => Path.Combine(Path.GetDirectoryName(SettingsFile)!, "selected-target.json");
    private readonly List<string> extraRoots = [];
    private readonly List<Frame> live = [];
    private Capture? capture;
    private Analysis? analysis;
    private string? currentCapturePath;
    private readonly CaptureHistory history;
    private readonly bool historyEnabled;
    private bool busy, polling, closing, closeReady;
    private string ModeName => (Mode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard";
    private string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValheimPerformanceCompanion", "settings.json");
    public MainWindow(bool rendering = false, CaptureHistory? historyOverride = null)
    {
        preview = rendering;
        history = historyOverride ?? new CaptureHistory(Path.Combine(Path.GetDirectoryName(SettingsFile)!, "recent-captures.json"));
        historyEnabled = !rendering || historyOverride is not null;
        InitializeComponent();
        Timeline.FrameSelected += SelectFrame;
        if (!rendering) Loaded += async (_, _) => { await RunAsync(async () => { await RefreshAsync(); await RestoreHistoryAsync(); }); timer.Start(); };
        discoveryTimer.Tick += async (_, _) => await BackgroundRefreshAsync();
        timer.Tick += async (_, _) =>
        {
            try { await TickAsync(); }
            catch (Exception ex) { AppLog.Write("Status timer", ex); StatusText.Text = "Action needed: Status check failed. Click Check now to retry. " + ex.Message; }
        };
        Closing += OnClosing;
        controller.ConsoleCommand = command => Dispatcher.InvokeAsync(() => HandleConsoleAsync(command)).Task.Unwrap();
        try
        {
            if (File.Exists(SettingsFile)) extraRoots.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(SettingsFile)) ?? []);
            if (File.Exists(SelectedTargetFile)) preferredTarget = JsonSerializer.Deserialize<GameTarget>(File.ReadAllText(SelectedTargetFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { StatusText.Text = "Saved locations could not be read: " + ex.Message; }
    }
    public async Task PrepareRecordPreviewAsync() { await RefreshAsync(); Tabs.SelectedIndex = 0; }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) { StatusText.Text = "Finishing the current operation. Please try again when it completes."; return; }
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception ex) { StatusText.Text = "Action needed: " + ex.Message; AppLog.Write("User operation", ex); }
        finally { busy = false; UpdateControls(); }
    }
    private void UpdateControls()
    {
        StartButton.IsEnabled = !busy && connected && !controller.Active;
        StopButton.IsEnabled = !busy && controller.Active; Targets.IsEnabled = !busy && !controller.Active;
        ConnectButton.IsEnabled = !busy && !connected && !waitingForExit;
        OpenCaptureButton.IsEnabled = !busy;
        ExportReportButton.IsEnabled = !busy;
        ConnectButton.Content = connected ? "Connected" : waitingForExit ? "Waiting for game to close" : "Check now";
        ActivityProgress.IsIndeterminate = busy;
        ActivityProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Fill = connected ? System.Windows.Media.Brushes.MediumAquamarine
            : waitingForExit ? System.Windows.Media.Brushes.Goldenrod : System.Windows.Media.Brushes.LightSteelBlue;
    }
    private async Task RefreshAsync()
    {
        if (controller.Active) return;
        StatusText.Text = "Detecting game · Checking Valheim installations, profiles and running processes…";
        var result = await Task.Run(() => Discovery.Find(extraRoots));
        GameTarget? selected = TargetSelection.Choose(result.Targets, preferredTarget, followDetected: !selectionPinned);
        var items = result.Targets.ToList();
        if (selected is not null && !items.Contains(selected)) items.Insert(0, selected);
        updatingTargets = true;
        try { Targets.ItemsSource = items; Targets.SelectedItem = selected; }
        finally { updatingTargets = false; }
        StatusText.Text = result.Notes.Count > 0 ? string.Join(" · ", result.Notes) : $"Found {result.Targets.Count} installation/profile candidates. Closed-game profiles are candidates, not verified active.";
        if (selected is null)
        {
            TargetEvidence.Text = "Several installations/profiles may be available; no profile has been selected.";
            TargetPath.Text = "";
            StatusText.Text = "Action needed: Select your profile above, or launch Valheim. Searching for its PID every 30 seconds.";
            if (!preview) discoveryTimer.Start();
            return;
        }
        if (!preview) { preferredTarget = selected; SaveSelectedTarget(); await PrepareTargetAsync(selected); RefreshHistory(); }
    }
    private async void RefreshTargets(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void TargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Targets.SelectedItem is not GameTarget target) return;
        TargetEvidence.Text = target.Evidence;
        TargetPath.Text = "Game: " + target.GameDirectory + "\nProfile: " + (target.ProfileDirectory ?? "Unverified / none");
        if (updatingTargets || preview) return;
        selectionPinned = true;
        preferredTarget = target;
        await RunAsync(async () => { SaveSelectedTarget(); await PrepareTargetAsync(target); RefreshHistory(); });
    }
    private async void Connect(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private void SaveSelectedTarget()
    {
        if (preview) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SelectedTargetFile)!);
        string temporary = SelectedTargetFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferredTarget));
        File.Move(temporary, SelectedTargetFile, true);
    }
    private async Task PrepareTargetAsync(GameTarget target)
    {
        observedPid = target.ProcessId; connected = false; waitingForExit = false; retryConnectionAt = null;
        if (observedPid.HasValue)
        {
            discoveryTimer.Stop();
            try { await ConnectAsync(target); connected = true; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                retryConnectionAt = DateTimeOffset.UtcNow + TargetSelection.RefreshInterval;
                StatusText.Text = "Game detected · Waiting for its recorder. Retrying every 30 seconds; Check now retries sooner. Bring Valheim to the foreground if loading is paused. " + ex.Message;
                AppLog.Write("Recorder connection", ex);
            }
            UpdateControls();
            return;
        }
        StatusText.Text = "Setting up recorder · Checking the selected profile and bundled recorder…";
        if (!preview) AppLog.State("Checking recorder for the detected/selected target");
        if (observedPid.HasValue) discoveryTimer.Stop(); else discoveryTimer.Start();
        SetupResult setup = await Task.Run(() => RecorderSetup.Ensure(target));
        waitingForExit = setup.WaitingForExit;
        StatusText.Text = setup.Message;
        if (!preview) AppLog.State($"Recorder ready={setup.Ready}; waiting for game exit={setup.WaitingForExit}; PID={observedPid}");
        UpdateControls();
    }
    private async Task BackgroundRefreshAsync()
    {
        if (busy || polling || closing || controller.Active) return;
        busy = true; UpdateControls();
        try { await RefreshAsync(); }
        catch (Exception ex) { StatusText.Text = "Action needed: Automatic setup/search could not finish. " + ex.Message; AppLog.Write("Automatic setup/search", ex); }
        finally { busy = false; UpdateControls(); }
    }
    private async Task TickAsync()
    {
        if (busy || polling || closing) return;
        bool exited = false;
        if (observedPid is int pid)
        {
            try { using var game = Process.GetProcessById(pid); exited = game.HasExited; }
            catch (ArgumentException) { exited = true; }
        }
        if (exited || (waitingForExit && !RecorderSetup.GameRunning()))
        {
            if (controller.Active) await RunAsync(StopAsync);
            connected = false; observedPid = null;
            await BackgroundRefreshAsync();
        }
        else if (connected) await PollAsync();
        else if (retryConnectionAt <= DateTimeOffset.UtcNow && preferredTarget is { ProcessId: not null } target)
        {
            busy = true; UpdateControls(); retryConnectionAt = null;
            try { await ConnectAsync(target); connected = true; }
            catch (Exception ex)
            {
                retryConnectionAt = DateTimeOffset.UtcNow + TargetSelection.RefreshInterval;
                StatusText.Text = "Game detected · Recorder has not responded. Retrying every 30 seconds. Click Check now to retry sooner; if the game has finished loading, the recorder may have failed. Connection details are saved in the companion log. " + ex.Message;
                AppLog.Write("Recorder connection retry", ex);
            }
            finally { busy = false; UpdateControls(); }
        }
    }
    private async Task ConnectAsync(GameTarget target)
    {
        if (target.ProcessId is null) throw new InvalidOperationException("Valheim is not running.");
        StatusText.Text = $"Connecting · Waiting for the recorder inside Valheim (PID {target.ProcessId})…";
        RecorderStatus status = await controller.ConnectAsync(target.ProcessId.Value, target);
        string? profile = Path.GetDirectoryName(status.BepInExRoot);
        var verified = new GameTarget(status.GameDirectory, profile, "Active profile verified by the recorder inside the selected game process.", status.ProcessId);
        var items = ((IEnumerable<GameTarget>?)Targets.ItemsSource ?? []).Where(t => t.ProcessId != status.ProcessId).Prepend(verified).ToArray();
        updatingTargets = true;
        try { Targets.ItemsSource = items; Targets.SelectedIndex = 0; }
        finally { updatingTargets = false; }
        preferredTarget = verified; SaveSelectedTarget();
        StatusText.Text = $"Connected · PID {status.ProcessId} · Ready. Click Start capture or use perf start in the game console." + (status.LegacyRecorder ? " Legacy recorder detected; existing instrumentation may add measurement overhead. All capture modes are available." : "");
        LiveDetail.Text = "Ready · PID " + status.ProcessId;
    }
    private async void LocateGame(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new OpenFileDialog { Title = "Locate Valheim", Filter = "Valheim executable|valheim.exe" };
        if (dialog.ShowDialog(this) != true) return;
        string path = Path.GetDirectoryName(dialog.FileName)!;
        if (!Discovery.IsGame(path)) throw new InvalidDataException("This folder does not contain a Valheim installation.");
        extraRoots.Add(path);
        if (preferredTarget is not null && !string.Equals(preferredTarget.GameDirectory, path, StringComparison.OrdinalIgnoreCase)) preferredTarget = null;
        SaveLocations(); await RefreshAsync();
    });
    private async void AddProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var dialog = new OpenFolderDialog { Title = "Choose a mod profile or a profiles folder" };
        if (dialog.ShowDialog(this) != true) return;
        extraRoots.Add(dialog.FolderName);
        if (Directory.Exists(Path.Combine(dialog.FolderName, "BepInEx")))
        {
            string? game = preferredTarget?.GameDirectory ?? (await Task.Run(() => Discovery.Find(extraRoots))).Targets.FirstOrDefault()?.GameDirectory;
            if (game is not null) { preferredTarget = new(game, dialog.FolderName, "Profile explicitly selected by the player."); selectionPinned = true; }
        }
        SaveLocations(); await RefreshAsync();
    });
    private void SaveLocations()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(extraRoots.Distinct(StringComparer.OrdinalIgnoreCase)));
        File.Move(temporary, SettingsFile, true);
    }
    private async void StartCapture(object sender, RoutedEventArgs e) => await RunAsync(() => StartAsync(ModeName));
    private async Task StartAsync(string mode)
    {
        if (!connected || Targets.SelectedItem is not GameTarget target || target.ProcessId != controller.Pid)
            throw new InvalidOperationException("Connect to the selected game/profile before starting a capture.");
        if (!int.TryParse(Duration.Text, out int seconds) || seconds is < 5 or > 600) throw new ArgumentException("Choose a duration between 5 and 600 seconds.");
        StatusText.Text = mode == "Monitor" ? "Starting recorder…" : "Starting recorder and PerfView. Approve the Windows collector prompt if shown…";
        live.Clear(); await controller.StartAsync(mode, seconds, Scenario.Text);
        StatusText.Text = $"Recording · {mode} · Automatic lag spike detection · console: perf stop"; UpdateControls();
    }
    private async void StopCapture(object sender, RoutedEventArgs e) => await RunAsync(StopAsync);
    private async Task StopAsync()
    {
        StatusText.Text = "Finishing capture and CPU stack export…";
        await controller.StopAsync();
        if (controller.SessionDirectory is string directory) await LoadCaptureAsync(directory);
        if (controller.Diagnostics.Count > 0) StatusText.Text = string.Join(" · ", controller.Diagnostics);
        UpdateControls();
    }
    private async Task<string> HandleConsoleAsync(string command)
    {
        if (busy && !command.StartsWith("status", StringComparison.OrdinalIgnoreCase)) return "A capture operation is already in progress.";
        string[] parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Use perf start [monitor|standard|deep], perf status, or perf stop.";
        if (parts[0].Equals("status", StringComparison.OrdinalIgnoreCase)) return controller.Active ? "Recording " + controller.Status?.ElapsedSeconds.ToString("F1") + "s" : connected ? "Connected; idle." : "Disconnected; waiting to reconnect.";
        busy = true;
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "start":
                    string mode = parts.Length > 1 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[1].ToLowerInvariant()) : ModeName;
                    if (mode is not ("Monitor" or "Standard" or "Deep")) return "Choose monitor, standard, or deep.";
                    await StartAsync(mode); return "Capture started: " + mode + ".";
                case "stop": await StopAsync(); return "Capture stopped. Results are ready in the companion.";
                default: return "Use perf start, perf stop, or perf status.";
            }
        }
        finally { busy = false; UpdateControls(); }
    }
    private async Task PollAsync()
    {
        if (busy || polling || !connected || controller.Pid is null) return;
        polling = true;
        try
        {
            RecorderStatus? status = await controller.PollAsync();
            if (status is null) return;
            if (status.LastError.Length > 0) StatusText.Text = status.LastError;
            LiveValue.Text = status.LastMs > 0 ? status.LastMs.ToString("F1") + " ms" : "—";
            LiveDetail.Text = status.State + $" · {status.ElapsedSeconds:F0}s · {status.Frames:N0} frames · {status.DroppedFrames} dropped";
            if (status.State == "Recording")
            {
                live.Add(new Frame(status.ElapsedSeconds, status.LastMs, DateTimeOffset.UtcNow));
                if (live.Count > 90) live.RemoveAt(0);
                LiveChart.SetFrames(live);
            }
            else if (controller.Active) await RunAsync(StopAsync);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Recorder connection: " + ex.Message;
            if (controller.Active) await RunAsync(StopAsync);
            connected = false;
            retryConnectionAt = DateTimeOffset.UtcNow + TargetSelection.RefreshInterval;
            StatusText.Text = "Recorder connection interrupted · Retrying in 30 seconds. Click Check now to reconnect sooner. " + ex.Message;
            AppLog.Write("Recorder polling", ex);
            UpdateControls();
        }
        finally { polling = false; }
    }
    private static OpenFileDialog CaptureDialog() => new() { Title = "Open an existing frame recording", Filter = "Frame recording|frames*.csv|CSV files|*.csv", CheckFileExists = true };
    private async void OpenCapture(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        StatusText.Text = "Choose an existing capture to open…";
        var dialog = CaptureDialog();
        if (dialog.ShowDialog(this) == true) await LoadCaptureAsync(dialog.FileName);
        else StatusText.Text = "Open capture canceled.";
    });
    private void RefreshHistory()
    {
        if (!historyEnabled) return;
        var snapshot = history.Read(preferredTarget?.ProfileDirectory);
        RecentCaptures.ItemsSource = snapshot.Recent;
        RecentCaptures.SelectedItem = snapshot.Recent.FirstOrDefault(item => string.Equals(item.Path, currentCapturePath, StringComparison.OrdinalIgnoreCase)) ?? snapshot.Recent.FirstOrDefault();
        PreviousComparisons.ItemsSource = snapshot.Comparisons;
        PreviousComparisons.SelectedIndex = snapshot.Comparisons.Count > 0 ? 0 : -1;
    }
    private async Task RestoreHistoryAsync()
    {
        var snapshot = history.Read(preferredTarget?.ProfileDirectory);
        RefreshHistory();
        string? path = snapshot.LastOpened ?? snapshot.Recent.FirstOrDefault()?.Path;
        if (path is null) return;
        try { await LoadCaptureAsync(path, remember: false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        { AppLog.Write("Restore previous recording", ex); StatusText.Text = "Previous recording could not be opened. Choose another from Recent recordings. " + ex.Message; }
    }
    private async void OpenRecentCapture(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (RecentCaptures.SelectedItem is not RecentCapture item) throw new InvalidOperationException("Choose a recent recording first.");
        await LoadCaptureAsync(item.Path);
    });
    public async Task LoadCaptureAsync(string path, bool remember = true, bool restoreComparison = true)
    {
        StatusText.Text = "Reading capture and correlating evidence…";
        Capture loaded = await Task.Run(() => CaptureReader.Read(path));
        capture = loaded;
        currentCapturePath = File.Exists(path) ? Path.GetFullPath(path) : Directory.GetFiles(loaded.Directory, "frames*.csv").Single();
        Comparison.Text = "";
        RangeStart.Text = "0"; RangeEnd.Text = "";
        ApplyAnalysis(); Tabs.SelectedIndex = 1;
        string stacks = Path.Combine(capture.Directory, "cpu-stacks.perfView.xml.zip");
        if (capture.CpuSamples.Count > 0)
            CpuEvidence.Text = string.Join("\n", capture.CpuSamples.GroupBy(s => s.Leaf).OrderByDescending(g => g.Count()).Take(30).Select(g => $"{g.Count()} samples · {g.Key}"));
        else if (File.Exists(stacks))
        {
            try
            {
                if (!capture.Metadata.TryGetValue("ProcessId", out string? rawPid) || !int.TryParse(rawPid, out int pid))
                    throw new InvalidDataException("Capture process identity is missing; refusing to attribute a machine-wide CPU export.");
                var hotspots = await Task.Run(() => CpuStacks.Read(stacks, processId: pid)); CpuEvidence.Text = string.Join("\n", hotspots.Take(30).Select(h => $"{h.Weight:F2} weight · {h.Samples} samples · {h.Name}"));
            }
            catch (Exception ex) { CpuEvidence.Text = "CPU stack export could not be read: " + ex.Message; }
        }
        else CpuEvidence.Text = "No CPU stack export accompanies this recording. Use Standard or Deep capture; raw traces can be opened in PerfView.";
        StatusText.Text = "Capture loaded · Local analysis · Raw evidence preserved";
        if (historyEnabled)
        {
            if (remember) history.Remember(currentCapturePath);
            RefreshHistory();
            if (restoreComparison) await RestoreComparisonAsync();
        }
    }
    private async Task RestoreComparisonAsync()
    {
        if (currentCapturePath is null) return;
        SavedComparison? saved = history.Read(preferredTarget?.ProfileDirectory).Comparisons.FirstOrDefault(item =>
            string.Equals(item.BaselinePath, currentCapturePath, StringComparison.OrdinalIgnoreCase));
        if (saved is null) return;
        try { ApplySavedOptions(saved.Options); await ShowComparisonAsync(saved.OtherPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        { AppLog.Write("Restore previous comparison", ex); StatusText.Text = "Recording loaded; previous comparison could not be opened. " + ex.Message; }
    }
    private async Task ShowComparisonAsync(string otherPath)
    {
        if (analysis is null) throw new InvalidOperationException("Open a baseline capture first.");
        Capture other = await Task.Run(() => CaptureReader.Read(otherPath));
        Comparison.Text = Analyzer.Compare(analysis, Analyzer.Analyze(other, analysis.Options));
    }
    private void ApplySavedOptions(AnalysisOptions options)
    {
        Budget.Text = options.BudgetMs.ToString(CultureInfo.InvariantCulture);
        ExcludeUnfocused.IsChecked = options.ExcludeUnfocused;
        ExcludeMenus.IsChecked = options.ExcludeMenus;
        RangeStart.Text = options.StartSeconds.ToString(CultureInfo.InvariantCulture);
        RangeEnd.Text = options.EndSeconds == double.MaxValue ? "" : options.EndSeconds.ToString(CultureInfo.InvariantCulture);
        ApplyAnalysis();
    }
    private void ApplyAnalysis()
    {
        if (capture is null) return;
        double Parse(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        analysis = Analyzer.Analyze(capture, new AnalysisOptions(Parse(Budget.Text), ExcludeUnfocused.IsChecked == true, ExcludeMenus.IsChecked == true,
            Parse(RangeStart.Text), string.IsNullOrWhiteSpace(RangeEnd.Text) ? double.MaxValue : Parse(RangeEnd.Text)));
        Metrics m = analysis.Metrics;
        CaptureTitle.Text = capture.Name;
        CaptureSubtitle.Text = $"{m.Duration:F1}s included · {m.Frames:N0} frames · {m.ExcludedFrames:N0} excluded · {m.RelativeSpikes} relative spikes · worst {m.WorstMs:F1} ms";
        Average.Text = m.AverageFps.ToString("F1"); Low.Text = m.OnePercentLow.ToString("F1"); P99.Text = m.P99Ms.ToString("F1") + " ms"; LagSpikes.Text = m.LagSpikes.ToString();
        Findings.ItemsSource = analysis.Diagnostics.Issues.Count == 0 ? analysis.Findings.ToArray() : analysis.Diagnostics.Issues.Select(i => new Finding(i.Summary, i.Status,
            string.Join("\n", i.Evidence.Concat(i.Gaps)), string.Join("\n", i.NextSteps), i.Start)).ToArray(); Events.ItemsSource = capture.Events;
        Timeline.BudgetMs = analysis.Options.BudgetMs; Timeline.Markers = capture.Events; Timeline.SetFrames(analysis.Included);
        DetectedSpikes.ItemsSource = analysis.DetectedSpikes.OrderByDescending(s => s.Frame.Milliseconds).Take(100).ToArray();
        DetectedSpikes.SelectedIndex = analysis.DetectedSpikes.Count > 0 ? 0 : -1;
        if (analysis.DetectedSpikes.Count == 0) SelectedFrame.Text = "No lag spikes detected in this selection.";
        Quality.Text = string.Join("\n\n", capture.Warnings) + "\n\n1% low = 1000 / mean of slowest ceil(1%) intervals. Relative spikes exceed max(8 ms, 2.5 × preceding 60-frame median). The live monitor shows one observation per second; saved captures contain every retained frame. Previous-frame counters are contextual, not exact lag spike attribution.";
    }
    private async void FiltersChanged(object sender, RoutedEventArgs e) => await RunAsync(() => { ApplyAnalysis(); return Task.CompletedTask; });
    private void DetectedSpikeChanged(object sender, SelectionChangedEventArgs e)
    { if (DetectedSpikes.SelectedItem is LagSpike spike) Timeline.SelectFrame(spike.Frame); }
    private void SelectFrame(Frame frame)
    {
        if (capture is null) return;
        string methods = string.Join("\n", capture.Methods.Where(m => m.Start < frame.Seconds && m.End >= frame.Seconds - frame.Milliseconds / 1000).OrderByDescending(m => m.MainMs).Take(5)
            .Select(m => $"{m.Name}: {m.MainMs:F2} ms inclusive across {m.End - m.Start:F3}s"));
        string events = string.Join("\n", capture.Events.Where(e => e.Seconds.HasValue && Math.Abs(e.Seconds.Value - frame.Seconds) <= 1).Take(4).Select(e => e.Message));
        var cpu = capture.CpuSamples.Where(s => s.Seconds >= frame.Seconds - frame.Milliseconds / 1000 && s.Seconds <= frame.Seconds).ToArray();
        string cpuText = cpu.Length == 0 ? "No CPU samples in this frame interval." : string.Join("\n", cpu.GroupBy(s => s.Leaf).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} CPU samples · {g.Key}"));
        SelectedFrame.Text = $"{frame.Seconds:F3}s · {frame.Milliseconds:F2} ms · {(1000 / Math.Max(frame.Milliseconds, .001)):F1} interval FPS\n{frame.Context}\n" +
            $"CPU observation: {(frame.CpuMs?.ToString("F2") ?? "unavailable")} ms · GPU observation: {(frame.GpuMs?.ToString("F2") ?? "unavailable")} ms\n" +
            (methods.Length > 0 ? methods : "No method timings overlap this interval.") + "\n" + cpuText + (events.Length > 0 ? "\n" + events : "");
        DiagnosticIssue? issue = analysis?.Diagnostics.Issues.FirstOrDefault(i => i.Id.StartsWith("frame-", StringComparison.Ordinal) && i.FrameIndices.Contains(frame.Index));
        if (issue is not null) SelectedFrame.Text += "\n\n" + issue.Status + "\n" + string.Join("\n", issue.Evidence) + "\n" + string.Join("\n", issue.Stacks.Take(3).Select(s => $"{s.ThreadRole} {s.ThreadId}: {s.Samples} samples\n{s.Stack}"));
    }
    private async void ChooseComparison(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (analysis is null) throw new InvalidOperationException("Open a baseline capture first.");
        var dialog = CaptureDialog(); if (dialog.ShowDialog(this) != true) return;
        await ShowComparisonAsync(dialog.FileName);
        history.RememberComparison(currentCapturePath!, dialog.FileName, analysis!.Options);
        RefreshHistory();
    });
    private async void OpenSavedComparison(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (PreviousComparisons.SelectedItem is not SavedComparison saved) throw new InvalidOperationException("Choose a previous comparison first.");
        await LoadCaptureAsync(saved.BaselinePath, remember: false, restoreComparison: false);
        ApplySavedOptions(saved.Options);
        await ShowComparisonAsync(saved.OtherPath);
        history.RememberComparison(saved.BaselinePath, saved.OtherPath, saved.Options);
        RefreshHistory();
        Tabs.SelectedIndex = 2;
    });
    private async void ExportReport(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (analysis is null) throw new InvalidOperationException("Open a capture before exporting a report.");
        StatusText.Text = "Choose where to save the report…";
        var dialog = ReportDialog();
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "Report export canceled."; return; }
        if (File.Exists(dialog.FileName)) throw new IOException("Choose a new filename; exports do not overwrite existing files.");
        bool includeRaw = dialog.FilterIndex == 1;
        await ExportSelectedReportAsync(dialog.FileName, includeRaw);
    });
    private static SaveFileDialog ReportDialog() => new()
    {
        Title = "Save capture report",
        Filter = "Full diagnostics ZIP with raw CPU trace (*.zip)|*.zip|Redacted report ZIP without raw trace (*.zip)|*.zip",
        FilterIndex = 1,
        FileName = "Valheim-performance-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
        DefaultExt = ".zip",
        AddExtension = true,
        OverwritePrompt = false
    };
    private async Task ExportSelectedReportAsync(string destination, bool includeRaw)
    {
        if (analysis is null) throw new InvalidOperationException("Open a capture before exporting a report.");
        await Task.Run(() => ReportExporter.Export(analysis, destination, includeRaw));
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0) throw new IOException("The report ZIP was not created.");
        StatusText.Text = (includeRaw ? "Full diagnostics ZIP saved: " : "Redacted report ZIP saved: ") + Path.GetFileName(destination);
    }
    private async void OpenPerfView(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (capture is null) throw new InvalidOperationException("Open a capture first.");
        PerfViewCollector.ValidateTool();
        string trace = new[] { "cpu.etl.zip", "cpu.etl" }.Select(n => Path.Combine(capture.Directory, n)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("This recording has no raw PerfView trace.");
        var start = new ProcessStartInfo(PerfViewCollector.Executable) { UseShellExecute = true }; start.ArgumentList.Add(trace); Process.Start(start)?.Dispose(); return Task.CompletedTask;
    });
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) { e.Cancel = !closeReady; return; }
        e.Cancel = true;
        if (busy) { StatusText.Text = "Wait for the current capture operation to finish before closing."; return; }
        closing = true; timer.Stop(); discoveryTimer.Stop();
        try { await controller.DisposeAsync(); }
        catch (Exception ex) { AppLog.Write("Close cleanup", ex); }
        // DisposeAsync may complete synchronously. Close only after the first Closing event returns.
        closeReady = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
    }
}
