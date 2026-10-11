using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FolderTools.Core;
using Microsoft.Win32;

namespace FolderTools;

/// <summary>"Folder sizes": the folder's tree, every folder with its size, files, subfolders and newest file.</summary>
public partial class SizeWindow : Window, IRebuildable
{
    private enum SortKey { Name, Size, Files, Folders, Modified }

    private readonly string _path;
    private readonly Dictionary<FolderNode, SizeRow> _rows = [];
    private readonly Dictionary<FolderNode, SizeRow> _filesRows = [];
    private readonly HashSet<FolderNode> _expanded = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private FolderScanner _scanner = new();
    private CancellationTokenSource? _scan;
    private FolderNode? _scanRoot; // the analyzed folder
    private FolderNode? _viewRoot; // the folder at the top of the list ("Show only this folder" moves it down)
    private Stopwatch _clock = new();
    private SortKey _sortKey = SortKey.Size;
    private bool _descending = true;

    public SizeWindow(string path)
    {
        _path = path;
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshRows();
        Loaded += (_, _) => _ = ScanAsync();
        Closed += (_, _) => _scan?.Cancel();
        UpdateHeaders();
    }

    public ObservableCollection<SizeRow> Rows { get; } = [];

    public Window? Rebuild() => new SizeWindow(_path);

    private bool IsScanning => _scan is not null;

    // ---------------------------------------------------------------- Scan

    private async Task ScanAsync()
    {
        _scan?.Cancel();
        using var cts = new CancellationTokenSource();
        _scan = cts;
        _scanner = new FolderScanner();
        _rows.Clear();
        _filesRows.Clear();
        _expanded.Clear();
        Rows.Clear();
        InaccessibleBar.Visibility = Visibility.Collapsed;
        SetBusy(true);
        _clock = Stopwatch.StartNew();

        var task = _scanner.ScanAsync(_path, cts.Token);
        _scanRoot = _viewRoot = _scanner.Root!;
        _expanded.Add(_scanRoot);
        ShowViewRoot();
        _timer.Start();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // A new scan (Analyze again) may have replaced this one: its timer keeps running.
            if (_scan == cts)
            {
                _timer.Stop();
                _scan = null;
            }
        }
        if (cts.IsCancellationRequested)
        {
            if (IsLoaded && _scan is null)
            {
                SetBusy(false);
                StatusText.Text = Strings.Get("ScanCancelled");
            }
            return;
        }

        _clock.Stop();
        SetBusy(false);
        RefreshRows();
        ShowTotals();
    }

    private void SetBusy(bool busy)
    {
        ScanProgress.Visibility = CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !busy;
    }

    private void ShowTotals()
    {
        if (_viewRoot is not { } root)
            return;
        StatusText.Text = Strings.Format("ScanDone", WindowsShell.FormatSize(root.Size), WindowsShell.FormatCount(root.FileCount),
            WindowsShell.FormatCount(root.FolderCount),
            // "0.04 s" rather than "0.0 s" for a small folder.
            _clock.Elapsed.TotalSeconds.ToString(_clock.Elapsed.TotalSeconds < 1 ? "0.00" : "0.0", CultureInfo.CurrentCulture));

        long inaccessible = _scanRoot?.InaccessibleCount ?? 0;
        InaccessibleBar.Visibility = inaccessible > 0 ? Visibility.Visible : Visibility.Collapsed;
        InaccessibleText.Text = Strings.Plural(WindowsShell.IsElevated ? "InaccessibleFoldersAdmin" : "InaccessibleFolders",
            inaccessible, WindowsShell.FormatCount(inaccessible));
        AdminButton.Visibility = WindowsShell.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsShell.RestartAsAdministrator(new LaunchRequest(LaunchAction.Size, _path)))
            Close();
    }

    // ---------------------------------------------------------------- Rows

    private SizeRow RowFor(FolderNode node)
    {
        if (!_rows.TryGetValue(node, out var row))
        {
            // The analyzed folder shows its size growing during the scan.
            row = new SizeRow(node, false, node == _scanRoot ? () => _scanner.BytesScanned : null);
            _rows[node] = row;
        }
        return row;
    }

    private SizeRow FilesRowFor(FolderNode node)
    {
        if (!_filesRows.TryGetValue(node, out var row))
            _filesRows[node] = row = new SizeRow(node, true);
        return row;
    }

    /// <summary>The lines to show: the top folder, then the subfolders of every expanded folder, sorted.</summary>
    private List<SizeRow> BuildRows()
    {
        var list = new List<SizeRow>();
        if (_viewRoot is not null)
            Add(_viewRoot, 0);
        return list;

        void Add(FolderNode node, int level)
        {
            var row = RowFor(node);
            row.Level = level;
            row.IsExpanded = _expanded.Contains(node) && row.IsExpandable;
            list.Add(row);
            if (!row.IsExpanded)
                return;

            var children = node.Children.Select(RowFor).ToList();
            // The files of a folder that also has subfolders get their own line, so that the lines add up.
            if (node.OwnFileCount > 0 && node.IsComplete)
                children.Add(FilesRowFor(node));
            children.Sort(Compare);
            foreach (var child in children)
            {
                if (child.IsFilesRow)
                {
                    child.Level = level + 1;
                    list.Add(child);
                }
                else
                    Add(child.Node, level + 1);
            }
        }
    }

    // Folders still being scanned come last, by name; the "files" line comes last when sorting by name.
    private int Compare(SizeRow a, SizeRow b)
    {
        if (a.IsPending != b.IsPending)
            return a.IsPending ? 1 : -1;
        if (a.IsPending)
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        if (_sortKey == SortKey.Name && a.IsFilesRow != b.IsFilesRow)
            return a.IsFilesRow ? 1 : -1;

        int result = _sortKey switch
        {
            SortKey.Name => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            SortKey.Files => a.SortFiles.CompareTo(b.SortFiles),
            SortKey.Folders => a.SortFolders.CompareTo(b.SortFolders),
            SortKey.Modified => a.SortModified.CompareTo(b.SortModified),
            _ => a.SortSize.CompareTo(b.SortSize),
        };
        if (result == 0)
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        return _descending ? -result : result;
    }

    /// <summary>
    /// Brings the list to <see cref="BuildRows"/>, moving the existing lines rather than recreating them, so that
    /// the selection and the scroll position stay put while the scan fills the tree.
    /// </summary>
    private void Reconcile()
    {
        var desired = BuildRows();
        var present = new HashSet<SizeRow>(Rows);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && Rows[i] == desired[i])
                continue;
            if (present.Contains(desired[i]))
            {
                int from = i + 1;
                while (Rows[from] != desired[i])
                    from++;
                Rows.Move(from, i);
            }
            else
                Rows.Insert(i, desired[i]);
        }
        while (Rows.Count > desired.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private void RefreshRows()
    {
        Reconcile();
        foreach (var row in Rows)
            row.Refresh();
        if (IsScanning)
            StatusText.Text = Strings.Format("ScanProgress", WindowsShell.FormatCount(_scanner.FilesScanned),
                WindowsShell.FormatSize(_scanner.BytesScanned), _scanner.CurrentFolder);
    }

    // Resetting the whole list (new top folder, new sort) is much faster than moving every line.
    private void ResetRows()
    {
        var selected = Tree.SelectedItem;
        Rows.Clear();
        foreach (var row in BuildRows())
        {
            row.Refresh();
            Rows.Add(row);
        }
        if (selected is not null && Rows.Contains(selected))
        {
            Tree.SelectedItem = selected;
            Tree.ScrollIntoView(selected);
        }
    }

    private void ShowViewRoot()
    {
        PathText.Text = _viewRoot?.FullPath ?? _path;
        Title = Strings.Format("SizeWindowTitle", _viewRoot?.Name ?? _path);
        UpButton.IsEnabled = _viewRoot?.Parent is not null;
        ResetRows();
        if (!IsScanning)
            ShowTotals();
    }

    private void Toggle(SizeRow row)
    {
        if (!row.IsExpandable)
            return;
        if (!_expanded.Remove(row.Node))
            _expanded.Add(row.Node);
        Reconcile();
        row.Refresh();
    }

    private void Chevron_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SizeRow row)
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: SizeRow row })
            Toggle(row);
    }

    // Like a tree view: Right opens a folder or goes to its first subfolder, Left closes it or goes to its parent.
    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not SizeRow row)
            return;
        switch (e.Key)
        {
            case Key.Right when row.IsExpandable && !row.IsExpanded:
            case Key.Enter:
                Toggle(row);
                break;
            case Key.Right when row.IsExpanded:
                Select(Rows.IndexOf(row) + 1);
                break;
            case Key.Left when row.IsExpanded:
                Toggle(row);
                break;
            case Key.Left when row.Level > 0:
                Select(Rows.IndexOf(RowFor(row.IsFilesRow ? row.Node : row.Node.Parent!)));
                break;
            case Key.Delete when CanDelete(row):
                Delete(row);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Select(int index)
    {
        if (index < 0 || index >= Rows.Count)
            return;
        Tree.SelectedIndex = index;
        Tree.ScrollIntoView(Rows[index]);
        (Tree.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem)?.Focus();
    }

    // ---------------------------------------------------------------- Sorting

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column })
            return;
        var key = column == NameColumn ? SortKey.Name
            : column == FilesColumn ? SortKey.Files
            : column == FoldersColumn ? SortKey.Folders
            : column == ModifiedColumn ? SortKey.Modified
            : SortKey.Size;
        // A new column starts with what matters most: biggest first, newest first, A to Z.
        _descending = key == _sortKey ? !_descending : key != SortKey.Name;
        _sortKey = key;
        UpdateHeaders();
        ResetRows();
    }

    private void UpdateHeaders()
    {
        string Header(string text, params SortKey[] keys) =>
            keys.Contains(_sortKey) ? $"{text} {(_descending ? "▼" : "▲")}" : text;
        NameColumn.Header = Header(Strings.Get("ColumnName"), SortKey.Name);
        SizeColumn.Header = Header(Strings.Get("ColumnSize"), SortKey.Size);
        ShareColumn.Header = Strings.Get("ColumnShare");
        FilesColumn.Header = Header(Strings.Get("ColumnFiles"), SortKey.Files);
        FoldersColumn.Header = Header(Strings.Get("ColumnFolders"), SortKey.Folders);
        ModifiedColumn.Header = Header(Strings.Get("ColumnModified"), SortKey.Modified);
    }

    // ---------------------------------------------------------------- Row menu

    private SizeRow? SelectedRow => Tree.SelectedItem as SizeRow;

    private bool CanDelete(SizeRow row) => !IsScanning && !row.IsFilesRow && row.Node != _scanRoot && row.Node.IsComplete;

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            e.Handled = true;
            return;
        }
        FocusMenuItem.IsEnabled = !row.IsFilesRow && !row.Node.IsLink && row.Level > 0;
        DeleteMenuItem.IsEnabled = CanDelete(row);
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            WindowsShell.OpenInExplorer(row.Node.FullPath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            Clipboard.SetText(row.Node.FullPath);
    }

    private void ShowOnly_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || row.IsFilesRow)
            return;
        _viewRoot = row.Node;
        _expanded.Add(row.Node);
        ShowViewRoot();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_viewRoot?.Parent is not { } parent)
            return;
        var previous = _viewRoot;
        _viewRoot = parent;
        _expanded.Add(parent);
        ShowViewRoot();
        Tree.SelectedItem = RowFor(previous);
        Tree.ScrollIntoView(Tree.SelectedItem);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row && CanDelete(row))
            Delete(row);
    }

    private void Delete(SizeRow row)
    {
        var node = row.Node;
        string message = Strings.Format("ConfirmRecycleFolder", node.Name, WindowsShell.FormatSize(node.Size),
            WindowsShell.FormatCount(node.FileCount));
        if (MessageBox.Show(this, message, Strings.Get("MenuDelete"), MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        if (!WindowsShell.RecycleWithDialogs(node.FullPath, this))
            return; // cancelled, or an error Windows has already explained
        int index = Rows.IndexOf(row);
        if (node == _viewRoot)
            _viewRoot = node.Parent;
        node.Detach();
        Reconcile();
        foreach (var r in Rows)
            r.Refresh();
        ShowTotals();
        Select(Math.Min(index, Rows.Count - 1));
    }

    // ---------------------------------------------------------------- Export, settings

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_viewRoot is not { } root)
            return;
        var dialog = new SaveFileDialog
        {
            Filter = Strings.Get("CsvFilter"),
            FileName = $"{Strings.Get("ActionSize")} - {string.Concat(root.Name.Split(Path.GetInvalidFileNameChars()))} - {DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            CsvExport.WriteFile(root, dialog.FileName, Strings.Get("CsvHeaders").Split('|'), CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Strings.Format("ErrorExport", ex.Message), Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.OpenSettings(this);
}
