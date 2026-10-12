using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderTools.Core;

namespace FolderTools;

/// <summary>
/// "Flatten folder": lists the files of the subfolders with the name each gets in the folder, offers to drop the
/// identical ones, moves them on request, deletes the emptied subfolders, then offers to undo. Nothing moves before
/// the user clicks Flatten.
/// </summary>
public partial class FlattenWindow : Window, IRebuildable
{
    private static readonly FlattenDepth[] Depths = [FlattenDepth.AllLevels, FlattenDepth.OneLevel];

    private readonly string _path;
    private readonly bool _initialized;
    private CancellationTokenSource? _planning;
    private bool _moving; // a flatten or an undo is running
    private AppSettings _settings = AppSettings.Load();
    private FolderFlattener _flattener = null!;
    private FlattenPlan? _plan;
    private IReadOnlyList<FlattenTarget> _targets = [];
    private FlattenResult? _result;

    public FlattenWindow(string path, FlattenResult? result = null)
    {
        _path = path;
        InitializeComponent();
        Title = Strings.Format("FlattenWindowTitle", FolderName);
        PathText.Text = path;
        DepthBox.Items.Add(Strings.Get("FlattenDepthAllLevels"));
        DepthBox.Items.Add(Strings.Get("FlattenDepthOneLevel"));
        DepthBox.SelectedIndex = Math.Max(0, Array.IndexOf(Depths, _settings.FlattenDepth));
        ShowNaming();
        _initialized = true;

        // Rebuilt after a flatten (new language): it can still be undone.
        Loaded += (_, _) =>
        {
            if (result is null)
                _ = PlanAsync();
            else
                ShowResult(result);
        };
        Closing += (_, e) => e.Cancel = _moving; // files are moving: wait for the end
        Closed += (_, _) => _planning?.Cancel();
    }

    public ObservableCollection<FlattenRow> Rows { get; } = [];

    public ObservableCollection<IdenticalRow> Identical { get; } = [];

    public Window? Rebuild() => _moving ? null : new FlattenWindow(_path, _result);

    private string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(_path)) is { Length: > 0 } name ? name : _path;

    private void ShowNaming() =>
        NamingRun.Text = Strings.Get(_settings.FlattenNaming == FlattenNaming.Prefix ? "FlattenNamingPrefix" : "FlattenNamingSuffix") + " · ";

    // ---------------------------------------------------------------- Preview

    private async Task PlanAsync()
    {
        _planning?.Cancel();
        using var cts = new CancellationTokenSource();
        _planning = cts;
        _flattener = new FolderFlattener(new EmptyFolderCleaner(_settings.IgnoredFiles, _settings.IgnoredFolders));
        var depth = _settings.FlattenDepth;
        var naming = _settings.FlattenNaming;

        _plan = null;
        Rows.Clear();
        Identical.Clear();
        IdenticalBar.Visibility = Visibility.Collapsed;
        SummaryText.Text = Strings.Get("FlattenReading");
        DetailText.Text = "";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        SetButtons(State.Reading);

        int found = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => DetailText.Text = Strings.Plural("PhotosFiles", Volatile.Read(ref found), WindowsShell.FormatCount(Volatile.Read(ref found)));
        timer.Start();
        var progress = new InlineProgress<int>(count => Volatile.Write(ref found, count));

        FlattenPlan? plan = null;
        string? error = null;
        try
        {
            var flattener = _flattener;
            plan = await Task.Run(() => flattener.Plan(_path, depth, naming, progress, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            timer.Stop();
            if (_planning == cts)
                _planning = null;
        }
        if (_planning is not null || !IsLoaded)
            return; // replaced by a new preview (another depth), or the window is closed

        Progress.Visibility = Visibility.Collapsed;
        if (plan is null)
        {
            SummaryText.Text = error is null ? "" : Strings.Format("ErrorSearch", error);
            DetailText.Text = "";
            SetButtons(State.Nothing);
            return;
        }
        _plan = plan;
        foreach (var item in plan.Identical)
            Identical.Add(Track(new IdenticalRow(item, plan.Root)));
        IdenticalBar.Visibility = Identical.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPlan();
    }

    private IdenticalRow Track(IdenticalRow row)
    {
        row.PropertyChanged += (_, _) => ShowPlan();
        return row;
    }

    // Names depend on the identical files kept: computed again whenever a box changes.
    private void ShowPlan()
    {
        if (_plan is not { } plan)
            return;
        var recycle = Identical.Where(r => r.IsChecked).Select(r => r.Item).ToHashSet();
        _targets = FolderFlattener.Resolve(plan, recycle);

        var moving = _targets.Where(t => t.Name is not null).ToList();
        int files = moving.Count(t => !t.Item.IsFolder), folders = moving.Count(t => t.Item.IsFolder);
        int renamed = moving.Count(t => t.IsRenamed);

        SummaryText.Text = plan.Items.Count == 0 && plan.EmptiedFolders.Count == 0 ? Strings.Get("FlattenNoSubfolders")
            : _targets.Count == 0 ? Strings.Get("FlattenNothingToMove")
            : Strings.Format("FlattenPlanSummary", Items(files, folders), FolderName);
        var details = new List<string>();
        if (renamed > 0)
            details.Add(Strings.Plural("FlattenDetailRenamed", renamed, WindowsShell.FormatCount(renamed)));
        if (recycle.Count > 0)
            details.Add(Strings.Plural("FlattenDetailRecycled", recycle.Count, WindowsShell.FormatCount(recycle.Count)));
        if (plan.EmptiedFolders.Count > 0 && _targets.Count > 0)
            details.Add(Strings.Plural("FlattenDetailEmptied", plan.EmptiedFolders.Count, WindowsShell.FormatCount(plan.EmptiedFolders.Count)));
        if (plan.InaccessibleCount > 0)
            details.Add(Strings.Plural("FlattenDetailInaccessible", plan.InaccessibleCount, WindowsShell.FormatCount(plan.InaccessibleCount)));
        DetailText.Text = string.Join(" · ", details);

        Rows.Clear();
        foreach (var target in _targets)
            Rows.Add(FlattenRow.Of(target, plan.Root));
        FlattenButton.Content = Strings.Format("FlattenButton", WindowsShell.FormatCount(moving.Count));
        SetButtons(_targets.Count > 0 ? State.Ready : State.Nothing);
    }

    // "248 files", or "240 files and 3 folders" (one level).
    private static string Items(int files, int folders)
    {
        string text = Strings.Plural("PhotosFiles", files, WindowsShell.FormatCount(files));
        return folders == 0 ? text
            : Strings.Format("FlattenFilesAndFolders", text, Strings.Plural("PhotosFolders", folders, WindowsShell.FormatCount(folders)));
    }

    private enum State
    {
        Reading,
        Nothing,
        Ready,
        Moving,
        Done,
    }

    private void SetButtons(State state)
    {
        FlattenButton.Visibility = state == State.Ready ? Visibility.Visible : Visibility.Collapsed;
        Note.Visibility = state == State.Ready ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.Visibility = state == State.Done && _result is { Moves.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = state == State.Done ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = state == State.Moving ? Visibility.Collapsed : Visibility.Visible;
        Options.IsEnabled = state is not (State.Moving or State.Done);
        IdenticalBar.IsEnabled = state == State.Ready;
        if (state is State.Done)
            IdenticalBar.Visibility = Visibility.Collapsed;
        Preview.Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The choice made here is kept for the next times.
    private void DepthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
            return;
        var depth = Depths[Math.Max(0, DepthBox.SelectedIndex)];
        if (depth == _settings.FlattenDepth)
            return;
        _settings.FlattenDepth = depth;
        _settings.Save();
        _ = PlanAsync();
    }

    // ---------------------------------------------------------------- Flatten and undo

    private async void Flatten_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || _targets.Count == 0)
            return;

        _moving = true;
        var targets = _targets;
        SetButtons(State.Moving);
        Preview.IsEnabled = false;
        SummaryText.Text = Strings.Get("FlattenWorking");
        DetailText.Text = "";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        int total = targets.Count;
        var progress = new Progress<int>(done => Progress.Value = (double)done / total);
        var flattener = _flattener;
        FlattenResult result;
        try
        {
            result = await Task.Run(() => flattener.Execute(plan, targets, WindowsShell.RecycleSilently, progress));
        }
        finally
        {
            _moving = false;
            Preview.IsEnabled = true;
        }
        ShowResult(result);
    }

    private void ShowResult(FlattenResult result)
    {
        _result = result;
        _plan = null;
        Progress.Visibility = Visibility.Collapsed;
        int files = result.Moves.Count(m => !m.IsFolder), folders = result.Moves.Count(m => m.IsFolder);
        SummaryText.Text = Strings.Format("FlattenDone", Items(files, folders),
            Strings.Plural("FlattenDoneRemoved", result.RemovedFolders.Count, WindowsShell.FormatCount(result.RemovedFolders.Count)));
        var details = new List<string>();
        if (result.Recycled.Count > 0)
            details.Add(Strings.Plural("FlattenDetailRecycledDone", result.Recycled.Count, WindowsShell.FormatCount(result.Recycled.Count)));
        if (result.Failures.Count > 0)
            details.Add(Strings.Plural("PhotosFailures", result.Failures.Count, WindowsShell.FormatCount(result.Failures.Count)));
        DetailText.Text = string.Join(" · ", details);

        Rows.Clear();
        foreach (var failure in result.Failures)
            Rows.Add(FlattenRow.Failure(failure, _path));
        foreach (var move in result.Moves)
            Rows.Add(FlattenRow.Moved(move, _path));
        SetButtons(State.Done);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } result)
            return;

        _moving = true;
        SetButtons(State.Moving);
        UndoButton.Visibility = OpenFolderButton.Visibility = Visibility.Collapsed;
        SummaryText.Text = Strings.Get("FlattenUndoing");
        DetailText.Text = "";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;

        IReadOnlyList<FlattenFailure> failures;
        try
        {
            failures = await Task.Run(() => FolderFlattener.Undo(result));
        }
        finally
        {
            _moving = false;
        }
        _result = null;

        // Back to the preview: the files are where they were.
        await PlanAsync();
        int back = result.Moves.Count - failures.Count;
        DetailText.Text = SummaryText.Text;
        SummaryText.Text = Strings.Plural("FlattenUndone", back, WindowsShell.FormatCount(back))
            + (result.Recycled.Count > 0 ? " " + Strings.Plural("FlattenUndoRecycled", result.Recycled.Count, WindowsShell.FormatCount(result.Recycled.Count)) : "");
        if (failures.Count > 0)
        {
            DetailText.Text = Strings.Plural("PhotosUndoFailures", failures.Count, WindowsShell.FormatCount(failures.Count));
            foreach (var failure in failures.Reverse())
                Rows.Insert(0, FlattenRow.Failure(failure, _path));
            Preview.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- Other

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => WindowsShell.OpenInExplorer(_path);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_moving)
            return;
        App.OpenSettings(this, SettingsPage.Flatten);
        // The naming or the ignored files may have changed (this window may be rebuilt).
        if (IsLoaded && _result is null)
        {
            var settings = AppSettings.Load();
            bool changed = settings.FlattenNaming != _settings.FlattenNaming
                || !settings.IgnoredFiles.SequenceEqual(_settings.IgnoredFiles)
                || !settings.IgnoredFolders.SequenceEqual(_settings.IgnoredFolders);
            settings.FlattenDepth = _settings.FlattenDepth;
            _settings = settings;
            ShowNaming();
            if (changed)
                _ = PlanAsync();
        }
    }

    /// <summary>Calls back on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>A file whose name is taken by a file with the same content: checked, it goes to the recycle bin.</summary>
public sealed class IdenticalRow(FlattenItem item, string root) : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FlattenItem Item { get; } = item;

    public string Name => Path.GetFileName(Item.Path);

    // "Clients\Dupont and Archives\2024 · keep one"
    public string Detail => Strings.Format("FlattenIdenticalDetail", Folder(Item.Path), Folder(Item.IdenticalTo!));

    public string ToolTip => Item.Path + Environment.NewLine + Item.IdenticalTo;

    // A file already in the flattened folder shows that folder's name, not ".".
    private string Folder(string path) =>
        Path.GetRelativePath(root, Path.GetDirectoryName(path)!) is var relative && relative == "." ? Path.GetFileName(root) : relative;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>A line of the flatten list: a file or folder with its new name, or one that could not move.</summary>
public sealed class FlattenRow
{
    private const string FileGlyph = "";
    private const string FolderGlyph = "";
    private const string WarningGlyph = "";

    public required string Name { get; init; }

    public string From { get; init; } = "";

    public required string Glyph { get; init; }

    public bool IsFolder { get; init; }

    public string Tag { get; init; } = "";

    public bool IsError { get; init; }

    public string? ToolTip { get; init; }

    private static string FromText(string path, string root) =>
        Path.GetRelativePath(root, Path.GetDirectoryName(path)!);

    public static FlattenRow Of(FlattenTarget target, string root) => new()
    {
        Name = target.Name ?? Path.GetFileName(target.Item.Path),
        From = FromText(target.Item.Path, root),
        Glyph = target.Item.IsFolder ? FolderGlyph : FileGlyph,
        IsFolder = target.Item.IsFolder,
        Tag = target.Name is null ? Strings.Get("FlattenTagRecycle") : target.IsRenamed ? Strings.Get("FlattenTagRenamed") : "",
        ToolTip = target.Item.Path,
    };

    public static FlattenRow Moved(FlattenMove move, string root) => new()
    {
        Name = Path.GetFileName(move.To),
        From = FromText(move.From, root),
        Glyph = move.IsFolder ? FolderGlyph : FileGlyph,
        IsFolder = move.IsFolder,
        Tag = !string.Equals(Path.GetFileName(move.From), Path.GetFileName(move.To), StringComparison.OrdinalIgnoreCase)
            ? Strings.Get("FlattenTagRenamed") : "",
        ToolTip = move.To,
    };

    public static FlattenRow Failure(FlattenFailure failure, string root) => new()
    {
        Name = Path.GetFileName(failure.Path),
        From = FromText(failure.Path, root),
        Glyph = WarningGlyph,
        Tag = failure.Reason ?? Strings.Get("FailureInUse"),
        IsError = true,
        ToolTip = failure.Path,
    };
}
