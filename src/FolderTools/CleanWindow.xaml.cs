using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using FolderTools.Core;

namespace FolderTools;

/// <summary>
/// "Delete empty folders": looks for them, lists them (all checked), deletes the checked ones on request, then
/// says what was done. Nothing is deleted before the user clicks Delete.
/// </summary>
public partial class CleanWindow : Window, IRebuildable
{
    private readonly string _path;
    private CancellationTokenSource? _search;
    private bool _deleting;
    private EmptyFolderCleaner _cleaner = null!;
    private AppSettings _settings = AppSettings.Load();

    public CleanWindow(string path)
    {
        _path = path;
        InitializeComponent();
        Title = Strings.Format("CleanWindowTitle", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path);
        PathText.Text = path;
        SensitiveBar.Visibility = IsSensitive(path) ? Visibility.Visible : Visibility.Collapsed;
        ShowMode();
        Loaded += (_, _) => _ = SearchAsync();
        Closing += (_, e) => e.Cancel = _deleting; // the deletion stops at the next batch: wait for it
        Closed += (_, _) => _search?.Cancel();
    }

    public ObservableCollection<CleanItem> Items { get; } = [];

    // A deletion in progress cannot be moved to a new window.
    public Window? Rebuild() => _deleting ? null : new CleanWindow(_path);

    private static bool IsSensitive(string path)
    {
        static string F(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        string profile = F(Environment.SpecialFolder.UserProfile);
        string[] system =
        [
            F(Environment.SpecialFolder.Windows),
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
        ];
        string[] exact =
        [
            Path.GetDirectoryName(profile) ?? "",
            profile,
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
        ];
        return SensitiveLocations.IsSensitive(path, system.Where(f => f.Length > 0), exact.Where(f => f.Length > 0));
    }

    private void ShowMode() =>
        ModeRun.Text = Strings.Get(_settings.DeleteToRecycleBin ? "CleanModeRecycle" : "CleanModePermanent") + " ";

    // ---------------------------------------------------------------- Search

    private async Task SearchAsync()
    {
        _search?.Cancel();
        using var cts = new CancellationTokenSource();
        _search = cts;
        _settings = AppSettings.Load();
        _cleaner = new EmptyFolderCleaner(_settings.IgnoredFiles);
        ShowMode();

        Items.Clear();
        InaccessibleBar.Visibility = Visibility.Collapsed;
        SummaryText.Text = Strings.Get("CleanSearching");
        DetailText.Text = "";
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        SetButtons(searching: true);

        // The folder being read, shown at most every 100 ms (Progress<T> posts every report to the UI thread).
        string current = "";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => DetailText.Text = Volatile.Read(ref current);
        timer.Start();
        var progress = new InlineProgress<string>(folder => Volatile.Write(ref current, folder));

        EmptyFolderSearch? result = null;
        string? error = null;
        try
        {
            result = await Task.Run(() => _cleaner.Find(_path, progress, cts.Token), cts.Token);
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
        DetailText.Text = "";
        if (result is null)
        {
            SummaryText.Text = error is null ? Strings.Get("CleanSearchCancelled") : Strings.Format("ErrorSearch", error);
            SetButtons(searching: false);
            return;
        }

        foreach (var folder in result.Folders)
            Items.Add(Track(new CleanItem(folder, _path)));
        SummaryText.Text = result.Folders.Count == 0
            ? Strings.Get("CleanNoneFound")
            : Strings.Plural("CleanFound", result.TotalCount, WindowsShell.FormatCount(result.TotalCount));
        DetailText.Text = result.Folders.Count == 0 ? "" : Strings.Get("CleanFoundDetail");
        ShowInaccessible(result.InaccessibleCount);
        SetButtons(searching: false);
    }

    // The count on the Delete button and the "Select all" box follow every check box, however it is changed
    // (mouse, keyboard, UI Automation).
    private CleanItem Track(CleanItem item)
    {
        item.PropertyChanged += (_, _) => UpdateDeleteButton();
        return item;
    }

    private void ShowInaccessible(int count)
    {
        InaccessibleBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InaccessibleText.Text = Strings.Plural(WindowsShell.IsElevated ? "CleanInaccessibleAdmin" : "CleanInaccessible",
            count, WindowsShell.FormatCount(count));
        AdminButton.Visibility = WindowsShell.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetButtons(bool searching)
    {
        bool canDelete = !searching && Items.Any(i => i.Folder is not null);
        CancelButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        AllBox.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        ModeText.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
        // Nothing found, or everything deleted: no empty list box.
        FolderList.Visibility = searching || Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        var folders = Items.Where(i => i.Folder is not null).ToList();
        int count = folders.Where(i => i.IsChecked).Sum(i => i.Folder!.TotalCount);
        DeleteButton.Content = Strings.Format("CleanDelete", WindowsShell.FormatCount(count));
        DeleteButton.IsEnabled = count > 0;
        AllBox.IsChecked = folders.All(i => i.IsChecked) ? true : folders.Any(i => i.IsChecked) ? null : false;
    }

    // All checked: uncheck them all; otherwise (none or some): check them all.
    private void AllBox_Click(object sender, RoutedEventArgs e)
    {
        bool check = !Items.All(i => i.IsChecked);
        foreach (var item in Items)
            item.IsChecked = check;
        UpdateDeleteButton();
    }

    // ---------------------------------------------------------------- Delete

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var folders = Items.Where(i => i.IsChecked && i.Folder is not null).Select(i => i.Folder!).ToList();
        if (folders.Count == 0)
            return;
        // Deleted for good: one more confirmation, there is no way back.
        if (!_settings.DeleteToRecycleBin
            && MessageBox.Show(this, Strings.Plural("PermanentDeleteQuestion", folders.Sum(f => f.TotalCount), WindowsShell.FormatCount(folders.Sum(f => f.TotalCount))),
                Strings.Get("ActionClean"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _deleting = true;
        DeleteButton.Visibility = RescanButton.Visibility = CloseButton.Visibility = Visibility.Collapsed;
        AllBox.Visibility = ModeText.Visibility = Visibility.Collapsed;
        FolderList.IsEnabled = false;
        SummaryText.Text = Strings.Get("CleanDeleting");
        DetailText.Text = "";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<int>(done => Progress.Value = (double)done / folders.Count);
        Action<IReadOnlyList<string>>? recycle = _settings.DeleteToRecycleBin ? WindowsShell.RecycleSilently : null;
        DeleteReport report;
        try
        {
            report = await Task.Run(() => _cleaner.Delete(folders, recycle, progress));
        }
        finally
        {
            _deleting = false;
            FolderList.IsEnabled = true;
            CloseButton.Visibility = Visibility.Visible;
        }
        ShowReport(report);
    }

    private void ShowReport(DeleteReport report)
    {
        Progress.Visibility = Visibility.Collapsed;
        Items.Clear();
        foreach (var failure in report.Failures)
            Items.Add(new CleanItem(failure, _path));

        SummaryText.Text = Strings.Plural(_settings.DeleteToRecycleBin ? "CleanDoneRecycled" : "CleanDoneDeleted",
            report.DeletedCount, WindowsShell.FormatCount(report.DeletedCount));
        DetailText.Text = report.Failures.Count == 0 ? "" : Strings.Plural("CleanFailures", report.Failures.Count, WindowsShell.FormatCount(report.Failures.Count));
        SetButtons(searching: false);
    }

    // ---------------------------------------------------------------- Other

    private void Cancel_Click(object sender, RoutedEventArgs e) => _search?.Cancel();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsShell.RestartAsAdministrator(new LaunchRequest(LaunchAction.Clean, _path)))
            Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_deleting)
            return;
        App.OpenSettings(this, SettingsPage.EmptyFolders);
        // The mode or the ignored files may have changed: a new search uses them (this window may be rebuilt).
        if (IsLoaded)
        {
            var settings = AppSettings.Load();
            bool ignoredChanged = !settings.IgnoredFiles.SequenceEqual(_settings.IgnoredFiles);
            _settings = settings;
            ShowMode();
            if (ignoredChanged && _search is null)
                _ = SearchAsync();
        }
    }

    /// <summary>Calls back on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

/// <summary>A line of the cleaning window: a folder found (checkable), or one that could not be deleted.</summary>
public sealed class CleanItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public CleanItem(EmptyFolder folder, string root)
    {
        Folder = folder;
        Path = folder.Path;
        RelativePath = System.IO.Path.GetRelativePath(root, folder.Path);
        Detail = folder.NestedCount == 0 ? "" : Strings.Plural("CleanNested", folder.NestedCount, WindowsShell.FormatCount(folder.NestedCount));
    }

    public CleanItem(DeleteFailure failure, string root)
    {
        Path = failure.Path;
        RelativePath = System.IO.Path.GetRelativePath(root, failure.Path);
        Detail = Strings.Get(failure.Reason switch
        {
            DeleteFailureReason.NoLongerEmpty => "FailureNoLongerEmpty",
            DeleteFailureReason.AccessDenied => "FailureAccessDenied",
            _ => "FailureInUse",
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The folder to delete; null for a line of the report.</summary>
    public EmptyFolder? Folder { get; }

    public string Path { get; }

    public string RelativePath { get; }

    public string Detail { get; }

    public Visibility CheckVisibility => Folder is null ? Visibility.Collapsed : Visibility.Visible;

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
