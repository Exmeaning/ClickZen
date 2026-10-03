using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;
using ClickZen.Core.Settings;
using ClickZen.Core.Variables;
using ClickZen.Device.Adb;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>A task fired on one device (raised on the UI thread).</summary>
public sealed record TaskFiredInfo(DeviceRun Run, TaskFiredEventArgs Args, DateTime At);

/// <summary>
/// The open .czscheme document (scheme + embedded recordings) and the engines running it.
/// Singleton, so the document and running engines survive page navigation.
/// <para>Threading: every member must be used on the UI thread; engine events are marshalled to it.</para>
/// </summary>
public sealed partial class AutomationService : ObservableObject, IDisposable
{
    public const int MaxLogEntries = 2000;

    private readonly DeviceHub _hub;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly IImageMatcher _matcher;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AutomationService> _log;
    private readonly VariableSyncService _sync;
    private readonly Dictionary<string, (Scheme Scheme, AutomationEngine Engine, long Version)> _stepEngines = new(StringComparer.Ordinal);
    private DispatcherQueue? _ui;
    private bool _syncingTasks;
    private long _editVersion;
    private string _runSchemeName = "";

    public AutomationService(DeviceHub hub, SettingsService settings, AppPaths paths, ILocalizer loc, IImageMatcher matcher,
        ILoggerFactory loggers, VariableStore globals, VariableSyncService sync)
    {
        _hub = hub;
        GlobalVariables = globals;
        _sync = sync;
        _settings = settings;
        _paths = paths;
        Loc = loc;
        _matcher = matcher;
        _loggers = loggers;
        _log = loggers.CreateLogger<AutomationService>();
        Tasks.CollectionChanged += OnTasksCollectionChanged;
        Replace(CreateScheme(), new Dictionary<string, RecordingDocument>(StringComparer.Ordinal), null);
    }

    internal ILocalizer Loc { get; }

    public Scheme Scheme { get; private set; } = new();

    /// <summary>Recordings embedded in the package (saved under recordings/{id}.czrec).</summary>
    public Dictionary<string, RecordingDocument> Recordings { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>Task rows in <see cref="Scheme.Tasks"/> order; reordering this collection reorders the scheme.</summary>
    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public ObservableCollection<TemplateItem> Templates { get; } = [];

    /// <summary>Devices of the current (or last) run.</summary>
    public ObservableCollection<DeviceRun> Runs { get; } = [];

    /// <summary>Run log (newest last, capped at <see cref="MaxLogEntries"/>).</summary>
    public ObservableCollection<RunLogItem> Log { get; } = [];

    /// <summary>Global-scope variables shared by every device run of this instance.</summary>
    public VariableStore GlobalVariables { get; }

    /// <summary>Raised when engines start or stop (the Variables page shows their scheme-scope stores).</summary>
    public event EventHandler? EnginesChanged;

    /// <summary>Engines of the current run (empty when idle).</summary>
    public IReadOnlyList<(DeviceEntry Device, AutomationEngine Engine)> RunningEngines =>
        Runs.Where(r => r.Engine is not null && r.State == EngineState.Running).Select(r => (r.Entry, r.Engine!)).ToArray();

    /// <summary>Serials of the devices "Run" targets; empty = the current device.</summary>
    public HashSet<string> TargetSerials { get; } = new(StringComparer.Ordinal);

    /// <summary>Raised after the document was replaced (new / open).</summary>
    public event EventHandler? DocumentReplaced;

    /// <summary>Raised when a task fired on any device.</summary>
    public event EventHandler<TaskFiredInfo>? TaskFired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsStepping { get; private set; }

    /// <summary>Rounds of all devices together in the current run.</summary>
    [ObservableProperty]
    public partial long TotalRounds { get; private set; }

    /// <summary>"task / rule" of the latest firing, for the status line.</summary>
    [ObservableProperty]
    public partial string? LastFired { get; private set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Scheme.Name) ? Scheme.Name
        : FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath)
        : Loc["Auto_Untitled"];

    /// <summary>Status bar text: idle, or running with scheme name and device count.</summary>
    public string StatusText => IsRunning
        ? Loc.Format("Status_EngineRunning", _runSchemeName, Runs.Count(r => r.State == EngineState.Running))
        : Loc["Status_EngineIdle"];

    public string DefaultDirectory
    {
        get
        {
            Directory.CreateDirectory(_paths.SchemesDirectory);
            return _paths.SchemesDirectory;
        }
    }

    /// <summary>Round interval of the open scheme; changes apply to running engines immediately.</summary>
    public int CheckIntervalMs
    {
        get => Scheme.Settings.CheckIntervalMs;
        set
        {
            value = Math.Clamp(value, 0, 600_000);
            if (Scheme.Settings.CheckIntervalMs == value)
            {
                return;
            }

            Scheme.Settings.CheckIntervalMs = value;
            foreach (var r in Runs)
            {
                if (r.Scheme is not null)
                {
                    r.Scheme.Settings.CheckIntervalMs = value;
                }
            }

            OnPropertyChanged();
            MarkDirty();
        }
    }

    private DispatcherQueue Ui => _ui ??= DispatcherQueue.GetForCurrentThread() ?? App.Current.MainWindow!.DispatcherQueue;

    // ------------------------------------------------------------------ document

    /// <summary>A fresh scheme with the defaults from settings.</summary>
    public Scheme CreateScheme()
    {
        var a = _settings.Current.Automation;
        return new Scheme
        {
            Settings = new SchemeSettings
            {
                CheckIntervalMs = Math.Max(0, a.DefaultCheckIntervalMs),
                ClearVariablesOnStop = a.ClearVariablesOnStop,
                Humanize = new HumanizeOptions
                {
                    PositionJitterDp = a.DefaultPositionJitterDp,
                    DelayJitterPercent = a.DefaultDelayJitterPercent,
                    DurationJitterPercent = a.DefaultDurationJitterPercent,
                },
            },
        };
    }

    public void New() => Replace(CreateScheme(), new Dictionary<string, RecordingDocument>(StringComparer.Ordinal), null);

    public void Open(string path)
    {
        var contents = SchemePackage.Load(path);
        foreach (var t in contents.Scheme.Templates.Where(t => t.Size.IsEmpty && t.Png.Length > 0))
        {
            t.Size = Vision.ImageCodec.MeasurePng(t.Png);
        }

        Replace(contents.Scheme, contents.Recordings, path);
        _log.LogInformation("Scheme opened from {Path}: {Tasks} task(s), {Templates} template(s), {Recordings} recording(s)",
            path, Scheme.Tasks.Count, Scheme.Templates.Count, Recordings.Count);
    }

    public void Save(string path)
    {
        if (!path.EndsWith(SchemePackage.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += SchemePackage.FileExtension;
        }

        if (string.IsNullOrWhiteSpace(Scheme.Name))
        {
            Scheme.Name = Path.GetFileNameWithoutExtension(path);
        }

        SchemePackage.Save(path, Scheme, Recordings);
        FilePath = path;
        IsDirty = false;
        OnPropertyChanged(nameof(DisplayName));
        _log.LogInformation("Scheme saved to {Path}: {Tasks} task(s), {Templates} template(s)", path, Scheme.Tasks.Count, Scheme.Templates.Count);
    }

    public void Rename(string name)
    {
        name = name.Trim();
        if (Scheme.Name == name)
        {
            return;
        }

        Scheme.Name = name;
        OnPropertyChanged(nameof(DisplayName));
        MarkDirty();
    }

    /// <summary>Marks the document unsaved (called after every edit).</summary>
    public void MarkDirty()
    {
        _editVersion++;
        IsDirty = true;
    }

    private void Replace(Scheme scheme, Dictionary<string, RecordingDocument> recordings, string? path)
    {
        _matcher.ClearCache();
        _stepEngines.Clear();
        Scheme = scheme;
        Recordings = new Dictionary<string, RecordingDocument>(recordings, StringComparer.Ordinal);
        FilePath = path;
        IsDirty = false;
        _editVersion++;
        RebuildTasks();
        RebuildTemplates();
        OnPropertyChanged(nameof(Scheme));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CheckIntervalMs));
        _sync.ApplyScheme(scheme);
        DocumentReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads variable sync directions after the scheme's variables were edited.</summary>
    public void VariablesEdited()
    {
        _sync.ApplyScheme(Scheme);
        MarkDirty();
    }

    // ------------------------------------------------------------------ tasks

    public TaskItem AddTask(AutomationTask? task = null, int index = -1)
    {
        task ??= new AutomationTask
        {
            Name = SchemeTools.UniqueName(Scheme.Tasks.Select(t => t.Name), Loc["Auto_NewTaskName"]),
            Rules = [new Rule()],
        };
        index = index < 0 || index > Tasks.Count ? Tasks.Count : index;
        var item = new TaskItem(task, this);
        _syncingTasks = true;
        try
        {
            Scheme.Tasks.Insert(index, task);
            Tasks.Insert(index, item);
        }
        finally
        {
            _syncingTasks = false;
        }

        MarkDirty();
        return item;
    }

    public TaskItem DuplicateTask(TaskItem item)
    {
        var copy = SchemeTools.CloneTask(item.Task, newId: true);
        copy.Name = SchemeTools.UniqueName(Scheme.Tasks.Select(t => t.Name), Loc.Format("Auto_CopyOf", item.Task.Name));
        return AddTask(copy, Tasks.IndexOf(item) + 1);
    }

    public void DeleteTask(TaskItem item)
    {
        _syncingTasks = true;
        try
        {
            Scheme.Tasks.Remove(item.Task);
            Tasks.Remove(item);
        }
        finally
        {
            _syncingTasks = false;
        }

        RebuildTemplates();
        MarkDirty();
    }

    public void MoveTask(TaskItem item, int newIndex)
    {
        var old = Tasks.IndexOf(item);
        newIndex = Math.Clamp(newIndex, 0, Tasks.Count - 1);
        if (old < 0 || old == newIndex)
        {
            return;
        }

        Tasks.Move(old, newIndex);
    }

    internal void OnTaskEnabledChanged(TaskItem item) => MarkDirty();

    /// <summary>Refreshes list summaries after the editor changed something.</summary>
    public void RefreshSummaries()
    {
        foreach (var t in Tasks)
        {
            t.Refresh();
        }

        RebuildTemplates();
    }

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingTasks)
        {
            return;
        }

        // ListView drag-reorder removes and re-inserts items: mirror the new order into the scheme.
        if (Tasks.Count == Scheme.Tasks.Count && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move)
        {
            Scheme.Tasks = Tasks.Select(t => t.Task).ToList();
            MarkDirty();
        }
    }

    private void RebuildTasks()
    {
        _syncingTasks = true;
        try
        {
            Tasks.Clear();
            foreach (var t in Scheme.Tasks)
            {
                Tasks.Add(new TaskItem(t, this));
            }
        }
        finally
        {
            _syncingTasks = false;
        }
    }

    public TaskItem? FindTask(string id) => Tasks.FirstOrDefault(t => t.Task.Id == id);

    // ------------------------------------------------------------------ templates

    public void AddTemplate(TemplateAsset template)
    {
        if (string.IsNullOrEmpty(template.Id) || Scheme.Templates.Any(t => t.Id == template.Id))
        {
            template.Id = SchemeTools.NewId();
        }

        template.Name = SchemeTools.UniqueName(Scheme.Templates.Select(t => t.Name),
            string.IsNullOrWhiteSpace(template.Name) ? Loc["Auto_TemplateDefaultName"] : template.Name.Trim());
        Scheme.Templates.Add(template);
        RebuildTemplates();
        MarkDirty();
    }

    public void RenameTemplate(TemplateAsset template, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == template.Name)
        {
            return;
        }

        template.Name = SchemeTools.UniqueName(Scheme.Templates.Where(t => t != template).Select(t => t.Name), name);
        RebuildTemplates();
        MarkDirty();
    }

    /// <summary>Removes a template; conditions that used it keep the dangling id (validation reports them).</summary>
    public void DeleteTemplate(TemplateAsset template)
    {
        if (Scheme.Templates.Remove(template))
        {
            RebuildTemplates();
            MarkDirty();
        }
    }

    public void RebuildTemplates()
    {
        var usage = Scheme.Templates.ToDictionary(t => t.Id, _ => 0, StringComparer.Ordinal);
        foreach (var task in Scheme.Tasks)
        {
            foreach (var id in SchemeTools.TemplateIds(task))
            {
                if (usage.TryGetValue(id, out var n))
                {
                    usage[id] = n + 1;
                }
            }
        }

        var existing = new Dictionary<TemplateAsset, TemplateItem>(ReferenceEqualityComparer.Instance);
        foreach (var row in Templates)
        {
            existing.TryAdd(row.Template, row);
        }

        Templates.Clear();
        foreach (var t in Scheme.Templates)
        {
            // Keep rows (and their decoded thumbnails) when nothing about them changed.
            if (existing.TryGetValue(t, out var old) && old.Usage == usage[t.Id] && old.Name == (string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name))
            {
                Templates.Add(old);
            }
            else
            {
                Templates.Add(new TemplateItem(t, usage[t.Id], Loc));
            }
        }
    }

    // ------------------------------------------------------------------ recordings

    /// <summary>Embeds a .czrec file in the scheme; returns its id.</summary>
    public string EmbedRecording(string path)
    {
        var doc = RecordingDocument.Load(path);
        var baseId = new string(Path.GetFileNameWithoutExtension(path).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (baseId.Length == 0)
        {
            baseId = "rec";
        }

        baseId = baseId.Length > 48 ? baseId[..48] : baseId;
        var id = baseId;
        for (var i = 2; Recordings.ContainsKey(id); i++)
        {
            id = string.Create(CultureInfo.InvariantCulture, $"{baseId}-{i}");
        }

        Recordings[id] = doc;
        MarkDirty();
        return id;
    }

    // ------------------------------------------------------------------ import / export

    /// <summary>Saves a package with just this task and the templates / recordings it uses.</summary>
    public void ExportTask(TaskItem item, string path)
    {
        if (!path.EndsWith(SchemePackage.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            path += SchemePackage.FileExtension;
        }

        var (scheme, recs) = SchemeTools.ExtractTask(Scheme, item.Task, Recordings);
        SchemePackage.Save(path, scheme, recs);
        _log.LogInformation("Task {Task} exported to {Path}", item.Task.Name, path);
    }

    /// <summary>Adds every task of another package to this scheme.</summary>
    public TaskImportResult ImportTasks(string path)
    {
        var contents = SchemePackage.Load(path);
        if (RefScaling.OrientationDiffers(contents.Scheme.RefSize, Scheme.RefSize))
        {
            throw new InvalidOperationException(Loc["Auto_ImportOrientation"]);
        }

        var result = SchemeTools.MergeTasks(Scheme, Recordings, contents.Scheme, contents.Recordings, TemplateImaging.ResizePng);
        RebuildTasks();
        RebuildTemplates();
        MarkDirty();
        _log.LogInformation("Imported {Count} task(s) from {Path} (rescaled: {Rescaled})", result.Tasks.Count, path, result.Rescaled);
        return result;
    }

    // ------------------------------------------------------------------ run

    /// <summary>The devices "Run" uses: the chosen targets that are mirroring, else the current device.</summary>
    public IReadOnlyList<DeviceEntry> ResolveTargets()
    {
        var chosen = _hub.Devices.Where(d => TargetSerials.Contains(d.Serial)).ToList();
        if (chosen.Count == 0 && _hub.Current is { } c)
        {
            chosen.Add(c);
        }

        return chosen;
    }

    /// <summary>Problems found by <see cref="SchemePackage.Validate"/> (localised, for display).</summary>
    public IReadOnlyList<SchemeIssue> Validate() => SchemePackage.Validate(Scheme, Recordings.Keys);

    /// <summary>
    /// Starts one engine per device on a snapshot of the scheme. Returns the devices that could not start
    /// (with a reason); running continues until <see cref="StopAsync"/> or every engine ends by itself.
    /// </summary>
    public async Task<IReadOnlyList<string>> StartAsync(IReadOnlyList<DeviceEntry> devices)
    {
        _ui ??= DispatcherQueue.GetForCurrentThread();
        var problems = new List<string>();
        if (IsRunning || IsStepping)
        {
            return problems;
        }

        if (devices.Count == 0)
        {
            problems.Add(Loc["Auto_NoTarget"]);
            return problems;
        }

        _matcher.ClearCache();
        _stepEngines.Clear();
        Runs.Clear();
        TotalRounds = 0;
        LastFired = null;
        foreach (var t in Tasks)
        {
            t.FireCount = 0;
            t.LastFiredText = "";
        }

        foreach (var issue in Validate())
        {
            AddLog(LogLevel.Warning, "", issue.Message);
        }

        _runSchemeName = DisplayName;
        IsRunning = true;
        foreach (var entry in devices)
        {
            var run = new DeviceRun(entry, Label(entry));
            try
            {
                var (scheme, engine) = await CreateEngineAsync(entry, run.Label, CancellationToken.None);
                run.Scheme = scheme;
                run.Engine = engine;
                Hook(run, engine);
                Runs.Add(run);
            }
            catch (Exception ex)
            {
                problems.Add($"{run.Label}: {ex.Message}");
                AddLog(LogLevel.Error, run.Label, ex.Message);
            }
        }

        if (Runs.Count == 0)
        {
            IsRunning = false;
            return problems;
        }

        foreach (var run in Runs)
        {
            var cts = new CancellationTokenSource();
            run.Cts = cts;
            run.State = EngineState.Running;
            var engine = run.Engine!;
            run.Completion = Task.Run(() => engine.RunAsync(cts.Token), CancellationToken.None)
                .ContinueWith(_ => Ui.TryEnqueue(() => OnRunEnded(run)), TaskScheduler.Default);
        }

        OnPropertyChanged(nameof(StatusText));
        EnginesChanged?.Invoke(this, EventArgs.Empty);
        _log.LogInformation("Scheme {Name} started on {Count} device(s)", _runSchemeName, Runs.Count);
        return problems;
    }

    /// <summary>Stops every engine and waits until they ended.</summary>
    public async Task StopAsync()
    {
        var pending = new List<Task>();
        foreach (var run in Runs)
        {
            try
            {
                run.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }

            if (run.Completion is { } c)
            {
                pending.Add(c);
            }
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _log.LogWarning("Automation engines did not stop within 10 s");
        }

        // Completions post OnRunEnded to the UI queue; make the state final now.
        foreach (var run in Runs)
        {
            OnRunEnded(run);
        }
    }

    /// <summary>Runs exactly one round on each device (keeps cooldowns between steps until the scheme changes).</summary>
    public async Task<IReadOnlyList<string>> StepAsync(IReadOnlyList<DeviceEntry> devices)
    {
        _ui ??= DispatcherQueue.GetForCurrentThread();
        var problems = new List<string>();
        if (IsRunning || IsStepping)
        {
            return problems;
        }

        if (devices.Count == 0)
        {
            problems.Add(Loc["Auto_NoTarget"]);
            return problems;
        }

        IsStepping = true;
        try
        {
            foreach (var entry in devices)
            {
                var label = Label(entry);
                try
                {
                    if (!_stepEngines.TryGetValue(entry.Serial, out var step) || step.Version != _editVersion)
                    {
                        _matcher.ClearCache();
                        var (scheme, engine) = await CreateEngineAsync(entry, label, CancellationToken.None);
                        var run = new DeviceRun(entry, label);
                        Hook(run, engine);
                        engine.Reset();
                        step = (scheme, engine, _editVersion);
                        _stepEngines[entry.Serial] = step;
                    }

                    var e = step.Engine;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var keepGoing = await Task.Run(() => e.RunRoundAsync(cts.Token), cts.Token);
                    AddLog(LogLevel.Information, label, Loc.Format("Auto_StepDone", e.Rounds));
                    if (!keepGoing)
                    {
                        _stepEngines.Remove(entry.Serial);
                    }
                }
                catch (Exception ex)
                {
                    _stepEngines.Remove(entry.Serial);
                    problems.Add($"{label}: {ex.Message}");
                    AddLog(LogLevel.Error, label, ex.Message);
                }
            }
        }
        finally
        {
            IsStepping = false;
        }

        return problems;
    }

    private async Task<(Scheme Scheme, AutomationEngine Engine)> CreateEngineAsync(DeviceEntry entry, string label, CancellationToken ct)
    {
        if (entry.Session is not { } session)
        {
            throw new InvalidOperationException(Loc["Auto_DeviceNotMirroring"]);
        }

        var scheme = SchemeTools.Clone(Scheme);
        var screen = RecordingService.CurrentScreen(entry);
        if (RefScaling.OrientationDiffers(scheme.RefSize, screen))
        {
            AddLog(LogLevel.Warning, label, Loc["Auto_OrientationRunWarning"]);
        }

        var input = await _hub.GetInjectorAsync(entry, ct);
        var recordings = new Dictionary<string, RecordingDocument>(Recordings, StringComparer.Ordinal);
        var baseDir = FilePath is null ? null : Path.GetDirectoryName(FilePath);
        var env = new EngineEnvironment
        {
            Frames = session,
            Input = input,
            Matcher = _matcher,
            ScreenSize = () => RecordingService.CurrentScreen(entry),
            DpScale = entry.Info.DpScale,
            Shell = new AdbDeviceShell(_hub.Adb, entry.Serial),
            ResolveRecording = id => ResolveRecording(id, recordings, baseDir),
            GlobalVariables = GlobalVariables,
            Logger = _loggers.CreateLogger("ClickZen.Automation"),
            DeviceLabel = label,
        };
        _log.LogInformation("Engine for {Device}: input {Input}, screen {Screen}, ref {Ref}", label, input.Name, screen, scheme.RefSize);
        return (scheme, new AutomationEngine(scheme, env));
    }

    private static RecordingDocument? ResolveRecording(string id, Dictionary<string, RecordingDocument> embedded, string? baseDir)
    {
        if (embedded.TryGetValue(id, out var doc))
        {
            return doc;
        }

        if (!id.EndsWith(RecordingDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.IsPathRooted(id) || baseDir is null ? id : Path.Combine(baseDir, id);
        if (!File.Exists(path))
        {
            return null;
        }

        lock (embedded)
        {
            if (!embedded.TryGetValue(id, out doc))
            {
                doc = RecordingDocument.Load(path);
                embedded[id] = doc;
            }
        }

        return doc;
    }

    private void Hook(DeviceRun run, AutomationEngine engine)
    {
        engine.Log += (_, e) => Ui.TryEnqueue(() => AddLog(e.Level, run.Label, e.Message));
        engine.StateChanged += (_, s) => Ui.TryEnqueue(() =>
        {
            run.State = s;
            OnPropertyChanged(nameof(StatusText));
        });
        engine.TaskFired += (_, e) =>
        {
            var at = DateTime.Now;
            var rounds = engine.Rounds;
            Ui.TryEnqueue(() =>
            {
                run.Rounds = rounds;
                if (FindTask(e.TaskId) is { } item)
                {
                    item.FireCount++;
                    item.LastFiredText = at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                }

                LastFired = string.IsNullOrEmpty(e.RuleName) ? e.TaskName : $"{e.TaskName} / {e.RuleName}";
                TaskFired?.Invoke(this, new TaskFiredInfo(run, e, at));
            });
        };
    }

    /// <summary>Refreshes round counters (called by the page's timer).</summary>
    public void PollRounds()
    {
        long total = 0;
        foreach (var r in Runs)
        {
            if (r.Engine is { } e)
            {
                r.Rounds = e.Rounds;
            }

            total += r.Rounds;
        }

        TotalRounds = total;
    }

    private void OnRunEnded(DeviceRun run)
    {
        if (run.Cts is { } cts)
        {
            run.Cts = null;
            cts.Dispose();
        }

        if (run.Engine is { } e)
        {
            run.Rounds = e.Rounds;
            run.State = e.State == EngineState.Running ? EngineState.Stopped : e.State;
        }

        if (IsRunning && Runs.All(r => r.Cts is null))
        {
            IsRunning = false;
            PollRounds();
            _log.LogInformation("Scheme {Name} stopped ({Rounds} round(s))", _runSchemeName, TotalRounds);
        }

        OnPropertyChanged(nameof(StatusText));
        EnginesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Label(DeviceEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Info.DisplayName) ? entry.Serial : entry.Info.DisplayName;

    public void AddLog(LogLevel level, string device, string message)
    {
        Log.Add(new RunLogItem(DateTime.Now, level, device, message));
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(0);
        }
    }

    public void ClearLog() => Log.Clear();

    public void Dispose()
    {
        foreach (var run in Runs)
        {
            try
            {
                run.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }
}
