using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FolderTools.Core;

namespace FolderTools;

/// <summary>
/// "Sort photos by date": reads the dates taken, shows where each photo goes, moves them on request, then offers to
/// undo. Nothing moves before the user clicks Sort.
/// </summary>
public partial class PhotoSortWindow : Window, IRebuildable
{
    private readonly string _path;
    private readonly CachingReader _reader = new(new DateTakenReader());
    private readonly bool _initialized;
    private CancellationTokenSource? _planning;
    private bool _moving; // a sort or an undo is running
    private bool _syncing; // the boxes follow the settings: one preview at the end, not one per box
    private AppSettings _settings = AppSettings.Load();
    private PhotoSortPlan? _plan;
    private PhotoSortResult? _result;

    public PhotoSortWindow(string path, PhotoSortResult? result = null)
    {
        _path = path;
        InitializeComponent();
        Title = Strings.Format("PhotosWindowTitle", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path);
        PathText.Text = path;
        PhotoOptions.Fill(LayoutBox, UndatedBox, _settings);
        _initialized = true;

        // Rebuilt after a sort (new language): the sort can still be undone.
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

    public ObservableCollection<PhotoNode> Nodes { get; } = [];

    public Window? Rebuild() => _moving ? null : new PhotoSortWindow(_path, _result);

    // ---------------------------------------------------------------- Preview

    private async Task PlanAsync()
    {
        _planning?.Cancel();
        using var cts = new CancellationTokenSource();
        _planning = cts;
        var layout = _settings.PhotoLayout;
        var undated = _settings.PhotoUndated;

        _plan = null;
        Nodes.Clear();
        SummaryText.Text = Strings.Get("PhotosReading");
        DetailText.Text = "";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        Options.IsEnabled = true;
        SetButtons(State.Reading);

        // The number of files read, shown at most every 100 ms.
        int read = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => DetailText.Text = Strings.Plural("PhotosReadCount", Volatile.Read(ref read), WindowsShell.FormatCount(Volatile.Read(ref read)));
        timer.Start();
        var progress = new InlineProgress<int>(done => Volatile.Write(ref read, done));

        PhotoSortPlan? plan = null;
        string? error = null;
        try
        {
            var sorter = new PhotoSorter(_reader);
            string noDate = Strings.Get("PhotosNoDateFolder");
            plan = await Task.Run(() => sorter.Plan(_path, layout, undated, noDate, progress, cts.Token), cts.Token);
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
            return; // replaced by a new preview (another option), or the window is closed

        Progress.Visibility = Visibility.Collapsed;
        if (plan is null)
        {
            SummaryText.Text = error is null ? "" : Strings.Format("ErrorSearch", error);
            DetailText.Text = "";
            SetButtons(State.Nothing);
            return;
        }
        _plan = plan;
        ShowPlan(plan);
    }

    private void ShowPlan(PhotoSortPlan plan)
    {
        int moving = plan.FileCount;
        var staying = plan.Groups.Where(g => g.Folder is null).ToList();
        int fileDated = plan.Moving.Where(g => g.Source == DateSource.FileDate).Sum(g => g.Files.Count);
        int noDate = plan.Moving.Where(g => g.Source == DateSource.None).Sum(g => g.Files.Count);
        int stay = staying.Sum(g => g.Files.Count);

        SummaryText.Text = moving == 0
            ? Strings.Get(stay == 0 ? "PhotosNoneFound" : "PhotosNothingToMove")
            : Strings.Format("PhotosPlanSummary",
                Strings.Plural("PhotosFiles", moving, WindowsShell.FormatCount(moving)),
                Strings.Plural("PhotosFolders", plan.FolderCount, WindowsShell.FormatCount(plan.FolderCount)));
        var details = new List<string>();
        if (fileDated > 0)
            details.Add(Strings.Plural("PhotosDetailFileDate", fileDated, WindowsShell.FormatCount(fileDated)));
        if (noDate > 0)
            details.Add(Strings.Plural("PhotosDetailNoDateFolder", noDate, WindowsShell.FormatCount(noDate), Strings.Get("PhotosNoDateFolder")));
        if (stay > 0)
            details.Add(Strings.Plural("PhotosDetailLeft", stay, WindowsShell.FormatCount(stay)));
        DetailText.Text = string.Join(" · ", details);

        Nodes.Clear();
        foreach (var node in PhotoNode.Tree(plan.Moving))
            Nodes.Add(node);
        if (staying.Count > 0)
            Nodes.Add(PhotoNode.Staying(staying));
        // Small sorts are shown open; bigger ones only down to the first level.
        bool openAll = plan.FolderCount <= 3;
        foreach (var node in Nodes)
            node.Expand(openAll ? int.MaxValue : 0);

        SortButton.Content = Strings.Format("PhotosSort", WindowsShell.FormatCount(moving));
        SetButtons(moving > 0 ? State.Ready : State.Nothing);
    }

    private enum State
    {
        Reading,
        Nothing,
        Ready,
        Moving,
        Sorted,
    }

    private void SetButtons(State state)
    {
        SortButton.Visibility = state == State.Ready ? Visibility.Visible : Visibility.Collapsed;
        RenameNote.Visibility = state == State.Ready ? Visibility.Visible : Visibility.Collapsed;
        UndoButton.Visibility = state == State.Sorted && _result is { Moves.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = state == State.Sorted ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = state == State.Moving ? Visibility.Collapsed : Visibility.Visible;
        Options.IsEnabled = state is not (State.Moving or State.Sorted);
        Preview.Visibility = Nodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The choice made here is kept for the next sorts, like in the settings.
    private void Options_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _syncing)
            return;
        var (layout, undated) = PhotoOptions.Read(LayoutBox, UndatedBox);
        if (layout == _settings.PhotoLayout && undated == _settings.PhotoUndated)
            return;
        _settings.PhotoLayout = layout;
        _settings.PhotoUndated = undated;
        _settings.Save();
        _ = PlanAsync(); // the dates already read are not read again
    }

    // ---------------------------------------------------------------- Sort and undo

    private async void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || plan.FileCount == 0)
            return;

        _moving = true;
        SetButtons(State.Moving);
        Preview.IsEnabled = false;
        SummaryText.Text = Strings.Get("PhotosSorting");
        DetailText.Text = "";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        int total = plan.FileCount;
        var progress = new Progress<int>(done => Progress.Value = (double)done / total);
        PhotoSortResult result;
        try
        {
            result = await Task.Run(() => new PhotoSorter(_reader).Execute(plan, progress));
        }
        finally
        {
            _moving = false;
            Preview.IsEnabled = true;
        }
        ShowResult(result);
    }

    private void ShowResult(PhotoSortResult result)
    {
        _result = result;
        _plan = null;
        Progress.Visibility = Visibility.Collapsed;
        SummaryText.Text = Strings.Format("PhotosDone",
            Strings.Plural("PhotosFiles", result.Moves.Count, WindowsShell.FormatCount(result.Moves.Count)),
            Strings.Plural("PhotosFolders", result.FolderCount, WindowsShell.FormatCount(result.FolderCount)));
        DetailText.Text = result.Failures.Count == 0 ? ""
            : Strings.Plural("PhotosFailures", result.Failures.Count, WindowsShell.FormatCount(result.Failures.Count));

        // After the sort the tree shows what went where; the files that could not move come first.
        Nodes.Clear();
        foreach (var failure in result.Failures)
            Nodes.Add(PhotoNode.Failure(failure, _path));
        foreach (var node in PhotoNode.Tree(result, _path))
            Nodes.Add(node);
        foreach (var node in Nodes)
            node.Expand(0);
        SetButtons(State.Sorted);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } result)
            return;

        _moving = true;
        SetButtons(State.Moving);
        UndoButton.Visibility = OpenFolderButton.Visibility = Visibility.Collapsed;
        SummaryText.Text = Strings.Get("PhotosUndoing");
        DetailText.Text = "";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;

        IReadOnlyList<PhotoSortFailure> failures;
        try
        {
            failures = await Task.Run(() => PhotoSorter.Undo(result));
        }
        finally
        {
            _moving = false;
        }
        _result = null;

        // Back to the preview: the photos are where they were, ready to be sorted another way.
        await PlanAsync();
        int back = result.Moves.Count - failures.Count;
        DetailText.Text = SummaryText.Text;
        SummaryText.Text = Strings.Plural("PhotosUndone", back, WindowsShell.FormatCount(back));
        if (failures.Count > 0)
        {
            DetailText.Text = Strings.Plural("PhotosUndoFailures", failures.Count, WindowsShell.FormatCount(failures.Count));
            foreach (var failure in failures.Reverse())
                Nodes.Insert(0, PhotoNode.Failure(failure, _path));
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
        App.OpenSettings(this, SettingsPage.Photos);
        // The options may have changed there: the boxes follow, and the preview with them (this window may be rebuilt).
        if (IsLoaded && _result is null)
        {
            var settings = AppSettings.Load();
            _syncing = true;
            PhotoOptions.Select(LayoutBox, UndatedBox, settings);
            _syncing = false;
            bool changed = settings.PhotoLayout != _settings.PhotoLayout || settings.PhotoUndated != _settings.PhotoUndated;
            _settings = settings;
            if (changed)
                _ = PlanAsync();
        }
    }

    /// <summary>Calls back on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Reads each file once: changing an option previews again without reading the metadata again.</summary>
    private sealed class CachingReader(IDateTakenReader inner) : IDateTakenReader
    {
        private readonly ConcurrentDictionary<(string, DateTime, long), DateTime?> _dates = new();

        public DateTime? Read(string path)
        {
            var info = new FileInfo(path);
            var key = (path.ToUpperInvariant(), info.LastWriteTimeUtc, info.Exists ? info.Length : -1);
            return _dates.GetOrAdd(key, _ => inner.Read(path));
        }
    }
}

/// <summary>The two choices of a photo sort, as shown in the sort window and in Settings → Photos.</summary>
public static class PhotoOptions
{
    private static readonly PhotoLayout[] Layouts = [PhotoLayout.YearDay, PhotoLayout.YearMonthDay, PhotoLayout.YearMonth, PhotoLayout.Day];
    private static readonly UndatedPolicy[] Policies = [UndatedPolicy.FileDate, UndatedPolicy.NoDateFolder, UndatedPolicy.Leave];

    // An example date shows what each layout makes.
    private static readonly DateTime Example = new(2025, 6, 14);

    public static void Fill(ComboBox layoutBox, ComboBox undatedBox, AppSettings settings)
    {
        layoutBox.Items.Clear();
        foreach (var layout in Layouts)
            layoutBox.Items.Add($"{Strings.Get("PhotosLayout" + layout)}   ({PhotoSorter.FolderFor(Example, layout)})");
        undatedBox.Items.Clear();
        foreach (var policy in Policies)
            undatedBox.Items.Add(Strings.Format("PhotosUndated" + policy, Strings.Get("PhotosNoDateFolder")));
        Select(layoutBox, undatedBox, settings);
    }

    public static void Select(ComboBox layoutBox, ComboBox undatedBox, AppSettings settings)
    {
        layoutBox.SelectedIndex = Math.Max(0, Array.IndexOf(Layouts, settings.PhotoLayout));
        undatedBox.SelectedIndex = Math.Max(0, Array.IndexOf(Policies, settings.PhotoUndated));
    }

    public static (PhotoLayout, UndatedPolicy) Read(ComboBox layoutBox, ComboBox undatedBox) =>
        (Layouts[Math.Max(0, layoutBox.SelectedIndex)], Policies[Math.Max(0, undatedBox.SelectedIndex)]);
}

/// <summary>
/// A line of the photo tree: a date folder, a photo with the files that go with it, the photos left in place, or
/// a file that could not move.
/// </summary>
public sealed class PhotoNode : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isExpanded;

    private PhotoNode(string name, string glyph)
    {
        Name = name;
        Glyph = glyph;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Glyph { get; }

    public string Detail { get; private set; } = "";

    public string? ToolTip { get; private init; }

    public bool IsFolder { get; private init; }

    public bool IsWarning { get; private set; }

    public bool IsError { get; private init; }

    public ObservableCollection<PhotoNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    /// <summary>Opens this node and its children down to <paramref name="depth"/> levels below.</summary>
    public void Expand(int depth)
    {
        IsExpanded = Children.Count > 0;
        if (depth > 0)
            foreach (var child in Children)
                child.Expand(depth - 1);
    }

    private const string FolderGlyph = "";
    private const string WarningGlyph = "";

    private static string GlyphOf(PhotoKind kind) => kind switch
    {
        PhotoKind.Raw => "", // camera
        PhotoKind.Video => "", // video
        _ => "", // photo
    };

    // "IMG_1001.CR3 + .JPG + .xmp"
    private static string GroupName(IReadOnlyList<string> files) =>
        Path.GetFileName(files[0]) + string.Concat(files.Skip(1).Select(f => " + " + Path.GetExtension(f)));

    private static string Time(DateTime date) => date.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The date folders of a preview, each with its photos.</summary>
    public static IEnumerable<PhotoNode> Tree(IEnumerable<PhotoGroup> groups)
    {
        var roots = new List<PhotoNode>();
        var folders = new Dictionary<string, PhotoNode>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<PhotoNode, int>();
        foreach (var group in groups)
        {
            var leaf = new PhotoNode(GroupName(group.Files), GlyphOf(group.Kind))
            {
                Detail = group.Source switch
                {
                    DateSource.Taken => Time(group.Date!.Value),
                    DateSource.FileDate => Strings.Format("PhotosByFileDate", Time(group.Date!.Value)),
                    _ => Strings.Get("PhotosNoDate"),
                },
                IsWarning = group.Source != DateSource.Taken,
                ToolTip = string.Join(Environment.NewLine, group.Files),
            };
            foreach (var folder in Chain(group.Folder!, folders, roots))
                counts[folder] = counts.GetValueOrDefault(folder) + group.Files.Count;
            folders[group.Folder!].Children.Add(leaf);
        }
        return Finish(roots, counts);
    }

    /// <summary>The date folders after a sort, each with the files moved into it.</summary>
    public static IEnumerable<PhotoNode> Tree(PhotoSortResult result, string root)
    {
        var roots = new List<PhotoNode>();
        var folders = new Dictionary<string, PhotoNode>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<PhotoNode, int>();
        foreach (var move in result.Moves.OrderBy(m => m.To, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(root, Path.GetDirectoryName(move.To)!);
            var leaf = new PhotoNode(Path.GetFileName(move.To), GlyphOf(PhotoSorter.KindOf(move.To) ?? PhotoKind.Photo))
            {
                ToolTip = move.To,
                // Renamed because the name was taken in the folder.
                Detail = !string.Equals(Path.GetFileName(move.From), Path.GetFileName(move.To), StringComparison.OrdinalIgnoreCase)
                    ? Strings.Format("PhotosRenamedFrom", Path.GetFileName(move.From)) : "",
            };
            leaf.IsWarning = leaf.Detail.Length > 0;
            foreach (var folder in Chain(relative, folders, roots))
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
            folders[relative].Children.Add(leaf);
        }
        return Finish(roots, counts);
    }

    // The nodes of "2025\2025-06-14": "2025", then "2025-06-14" under it (created when missing).
    private static IEnumerable<PhotoNode> Chain(string relative, Dictionary<string, PhotoNode> folders, List<PhotoNode> roots)
    {
        string path = "";
        PhotoNode? parent = null;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            path = path.Length == 0 ? part : Path.Join(path, part);
            if (!folders.TryGetValue(path, out var node))
            {
                node = new PhotoNode(part, FolderGlyph) { IsFolder = true };
                folders[path] = node;
                (parent?.Children ?? (ICollection<PhotoNode>)roots).Add(node);
            }
            parent = node;
            yield return node;
        }
    }

    // Folder nodes get their file count, now that it is known (before the tree is shown).
    private static List<PhotoNode> Finish(List<PhotoNode> nodes, Dictionary<PhotoNode, int> counts)
    {
        foreach (var node in nodes.Where(n => n.IsFolder))
        {
            int count = counts.GetValueOrDefault(node);
            node.Detail = Strings.Plural("PhotosFiles", count, WindowsShell.FormatCount(count));
            Finish([.. node.Children], counts);
        }
        return nodes;
    }

    /// <summary>The photos without a date, left where they are.</summary>
    public static PhotoNode Staying(IReadOnlyList<PhotoGroup> groups)
    {
        int count = groups.Sum(g => g.Files.Count);
        var node = new PhotoNode(Strings.Get("PhotosLeftInPlace"), WarningGlyph)
        {
            Detail = Strings.Plural("PhotosFiles", count, WindowsShell.FormatCount(count)),
            IsWarning = true,
        };
        foreach (var group in groups)
            node.Children.Add(new PhotoNode(GroupName(group.Files), GlyphOf(group.Kind))
            {
                Detail = Strings.Get("PhotosNoDate"),
                IsWarning = true,
                ToolTip = string.Join(Environment.NewLine, group.Files),
            });
        return node;
    }

    public static PhotoNode Failure(PhotoSortFailure failure, string root) =>
        new(Path.GetRelativePath(root, failure.Path), WarningGlyph)
        {
            Detail = failure.Reason,
            IsError = true,
            ToolTip = failure.Path,
        };
}
