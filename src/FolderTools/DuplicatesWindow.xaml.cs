using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FolderTools.Core;

namespace FolderTools;

/// <summary>
/// "Delete duplicates": finds the files of the folder (and its subfolders) with the same content, checks every copy
/// but the one kept by default, and sends the checked ones to the recycle bin on request. Nothing is deleted before
/// the user clicks the button.
/// </summary>
public partial class DuplicatesWindow : Window, IRebuildable
{
    // Groups shown open; the others are listed closed, so that a big search stays readable.
    private const int OpenGroups = 20;

    private readonly string _path;
    private CancellationTokenSource? _search;
    private bool _recycling;
    private AppSettings _settings = AppSettings.Load();
    private DuplicateSearch? _result;

    public DuplicatesWindow(string path)
    {
        _path = path;
        InitializeComponent();
        Title = Strings.Format("DuplicatesWindowTitle", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path);
        PathText.Text = path;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateRow.Group)));
        List.ItemsSource = view;
        Loaded += (_, _) => _ = SearchAsync();
        Closing += (_, e) => e.Cancel = _recycling; // files are going to the recycle bin: wait for the end
        Closed += (_, _) => _search?.Cancel();
    }

    public ObservableCollection<DuplicateRow> Rows { get; } = [];

    public Window? Rebuild() => _recycling ? null : new DuplicatesWindow(_path);

    private void ShowKeep() =>
        KeepRun.Text = Strings.Format("DuplicatesKeepNow", Strings.Get("DuplicatesKeep" + _settings.DuplicateKeep).ToLower(CultureInfo.CurrentCulture)) + " · ";

    // ---------------------------------------------------------------- Search

    private async Task SearchAsync()
    {
        _search?.Cancel();
        using var cts = new CancellationTokenSource();
        _search = cts;
        _settings = AppSettings.Load();
        var finder = new DuplicateFinder(new EmptyFolderCleaner(_settings.IgnoredFiles, _settings.IgnoredFolders));

        _result = null;
        Rows.Clear();
        SummaryText.Text = Strings.Get("DuplicatesSearching");
        DetailText.Text = "";
        KeepText.Visibility = Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        SetButtons(searching: true);

        // Shown at most every 100 ms (Progress<T> would post every report to the UI thread).
        DuplicateProgress? last = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref last) is not { } p)
                return;
            if (!p.Comparing)
                DetailText.Text = Strings.Plural("DuplicatesListing", p.Count, WindowsShell.FormatCount(p.Count));
            else
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
                DetailText.Text = Strings.Format("DuplicatesComparing", WindowsShell.FormatSize(p.BytesDone), WindowsShell.FormatSize(p.BytesTotal));
            }
        };
        timer.Start();
        var progress = new InlineProgress<DuplicateProgress>(p => Volatile.Write(ref last, p));

        DuplicateSearch? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => finder.Find(_path, progress, cts.Token), cts.Token);
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
            if (_search == cts)
                _search = null;
        }
        if (_search is not null || !IsLoaded)
            return; // replaced by a new search, or the window is closed

        Progress.Visibility = Visibility.Collapsed;
        if (result is null)
        {
            SummaryText.Text = error is null ? Strings.Get("CleanSearchCancelled") : Strings.Format("ErrorSearch", error);
            DetailText.Text = "";
            SetButtons(searching: false);
            return;
        }
        _result = result;
        ShowGroups(result.Groups);
        ShowSummary();
        SetButtons(searching: false);
    }

    private void ShowGroups(IReadOnlyList<DuplicateGroup> groups)
    {
        Rows.Clear();
        int index = 0;
        foreach (var group in groups)
        {
            var view = new DuplicateGroupView(group, index++ < OpenGroups);
            foreach (var file in group.Files)
                view.Rows.Add(new DuplicateRow(file, group.Length, view, _path));
            ApplyKeep(view);
            foreach (var row in view.Rows)
            {
                row.PropertyChanged += (_, _) => UpdateButton();
                Rows.Add(row);
            }
        }
    }

    // Every copy checked but the one the rule keeps (among those still listed).
    private void ApplyKeep(DuplicateGroupView view)
    {
        var keeper = DuplicateFinder.Keeper(view.Source with { Files = view.Rows.Select(r => r.File).ToList() }, _settings.DuplicateKeep);
        foreach (var row in view.Rows)
            row.IsChecked = false;
        foreach (var row in view.Rows.Where(r => r.File != keeper))
            row.IsChecked = true;
    }

    private void ShowSummary()
    {
        if (_result is not { } result)
            return;
        int groups = Rows.Select(r => r.Group).Distinct().Count();
        SummaryText.Text = groups == 0 ? Strings.Get("DuplicatesNone")
            : Strings.Format("DuplicatesFound",
                Strings.Plural("DuplicatesGroups", groups, WindowsShell.FormatCount(groups)),
                Strings.Plural("DuplicatesCopies", result.ExtraCopies, WindowsShell.FormatCount(result.ExtraCopies)),
                WindowsShell.FormatSize(result.Wasted));
        DetailText.Text = Strings.Plural("DuplicatesCompared", result.FileCount, WindowsShell.FormatCount(result.FileCount))
            + (result.InaccessibleCount > 0 ? " · " + Strings.Plural("DuplicatesInaccessible", result.InaccessibleCount, WindowsShell.FormatCount(result.InaccessibleCount)) : "");
        ShowKeep();
        KeepText.Visibility = groups > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetButtons(bool searching)
    {
        CancelButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.Visibility = searching || _recycling ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = _recycling ? Visibility.Collapsed : Visibility.Visible;
        List.Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Note.Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton();
        if (searching || _recycling)
            RecycleButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateButton()
    {
        var going = Rows.Where(r => r.IsChecked).ToList();
        RecycleButton.Content = Strings.Format("DuplicatesRecycle", WindowsShell.FormatCount(going.Count), WindowsShell.FormatSize(going.Sum(r => r.Length)));
        RecycleButton.Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleButton.IsEnabled = going.Count > 0;
    }

    // ---------------------------------------------------------------- Recycle

    private async void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } result)
            return;
        var going = Rows.Where(r => r.IsChecked).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (going.Count == 0)
            return;

        _recycling = true;
        SetButtons(searching: false);
        List.IsEnabled = false;
        SummaryText.Text = Strings.Get("DuplicatesRecycling");
        DetailText.Text = "";
        KeepText.Visibility = Visibility.Collapsed;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<int>(done => Progress.Value = (double)done / going.Count);
        DuplicateDeleteReport report;
        try
        {
            report = await Task.Run(() => DuplicateFinder.Recycle(result.Groups, going, WindowsShell.RecycleSilently, progress));
        }
        finally
        {
            _recycling = false;
            List.IsEnabled = true;
        }
        ShowReport(report);
    }

    // The copies gone leave the list; a group down to one file goes too. What could not go stays, checked.
    private void ShowReport(DuplicateDeleteReport report)
    {
        Progress.Visibility = Visibility.Collapsed;
        var failed = report.Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.ToList())
        {
            if (row.IsChecked && !failed.Contains(row.Path))
            {
                Rows.Remove(row);
                row.Group.Rows.Remove(row);
            }
            else if (failed.Contains(row.Path))
                row.Failed = report.Failures.First(f => string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase)).Reason
                    ?? Strings.Get("FailureInUse");
        }
        foreach (var row in Rows.Where(r => r.Group.Rows.Count < 2).ToList())
            Rows.Remove(row);

        SummaryText.Text = Strings.Plural("DuplicatesDone", report.RecycledCount, WindowsShell.FormatCount(report.RecycledCount),
            WindowsShell.FormatSize(report.Freed));
        DetailText.Text = report.Failures.Count == 0 ? ""
            : Strings.Plural("DuplicatesFailures", report.Failures.Count, WindowsShell.FormatCount(report.Failures.Count));
        SetButtons(searching: false);
    }

    // ---------------------------------------------------------------- Other

    private DuplicateRow? Selected => List.SelectedItem as DuplicateRow;

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } row && e.OriginalSource is FrameworkElement { DataContext: DuplicateRow })
            WindowsShell.ShowInExplorer(row.Path);
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Selected is null)
            e.Handled = true;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            WindowsShell.Open(row.Path);
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            WindowsShell.ShowInExplorer(row.Path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
            Clipboard.SetText(row.Path);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _search?.Cancel();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_recycling)
            return;
        App.OpenSettings(this, SettingsPage.Duplicates);
        // A new rule checks the copies again; new ignored files or folders need a new search (this window may be rebuilt).
        if (IsLoaded)
        {
            var settings = AppSettings.Load();
            bool ignoredChanged = !settings.IgnoredFiles.SequenceEqual(_settings.IgnoredFiles)
                || !settings.IgnoredFolders.SequenceEqual(_settings.IgnoredFolders);
            bool keepChanged = settings.DuplicateKeep != _settings.DuplicateKeep;
            _settings = settings;
            if (ignoredChanged && _search is null)
                _ = SearchAsync();
            else if (keepChanged && _search is null)
            {
                foreach (var view in Rows.Select(r => r.Group).Distinct().ToList())
                    ApplyKeep(view);
                ShowKeep();
                UpdateButton();
            }
        }
    }

    /// <summary>Calls back on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>The header of a group of duplicates in the list.</summary>
public sealed class DuplicateGroupView(DuplicateGroup source, bool isExpanded) : INotifyPropertyChanged
{
    private bool _isExpanded = isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DuplicateGroup Source { get; } = source;

    public List<DuplicateRow> Rows { get; } = [];

    public string Title => Path.GetFileName(Source.Files[0].Path);

    public string Detail => Strings.Format("DuplicatesGroupDetail", WindowsShell.FormatCount(Source.Files.Count),
        WindowsShell.FormatSize(Source.Length), WindowsShell.FormatSize(Source.Wasted));

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
}

/// <summary>A copy in a group of duplicates: checked, it goes to the recycle bin.</summary>
public sealed class DuplicateRow(DuplicateFile file, long length, DuplicateGroupView group, string root) : INotifyPropertyChanged
{
    private bool _isChecked;
    private string? _failed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DuplicateGroupView Group { get; } = group;

    public DuplicateFile File { get; } = file;

    public string Path { get; } = file.Path;

    public long Length { get; } = length;

    public string Name => System.IO.Path.GetFileName(Path);

    // The folder relative to the searched one; a file right in it shows that folder's name.
    public string Folder => System.IO.Path.GetRelativePath(root, System.IO.Path.GetDirectoryName(Path)!) is var relative && relative == "."
        ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root))
        : relative;

    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string SizeText => WindowsShell.FormatSize(Length);

    public string Tag => _failed ?? (IsChecked ? "" : Strings.Get("DuplicatesKept"));

    public bool IsError => _failed is not null;

    /// <summary>Why the copy could not go to the recycle bin.</summary>
    public string? Failed
    {
        get => _failed;
        set
        {
            _failed = value;
            Changed(nameof(Tag));
            Changed(nameof(IsError));
        }
    }

    /// <summary>To the recycle bin. Every copy of a group cannot be checked: one at least is kept.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            if (value && Group.Rows.Count > 0 && Group.Rows.All(r => r == this || r.IsChecked))
            {
                Changed(nameof(IsChecked)); // refused: the check box goes back
                return;
            }
            _isChecked = value;
            Changed(nameof(IsChecked));
            Changed(nameof(Tag));
        }
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
