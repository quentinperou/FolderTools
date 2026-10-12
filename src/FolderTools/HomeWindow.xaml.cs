using System.IO;
using System.Windows;
using FolderTools.Core;
using Microsoft.Win32;

namespace FolderTools;

/// <summary>Started from the Start menu: pick a folder, then one of the tools.</summary>
public partial class HomeWindow : Window, IRebuildable
{
    public HomeWindow(string? folder = null)
    {
        InitializeComponent();
        PathBox.Text = folder ?? "";
        UpdateButtons();
    }

    public Window? Rebuild() => new HomeWindow(PathBox.Text);

    private string? Folder
    {
        get
        {
            string? path = LaunchRequest.NormalizePath(PathBox.Text);
            return path is not null && Directory.Exists(path) ? path : null;
        }
    }

    private void UpdateButtons() => SizeButton.IsEnabled = CleanButton.IsEnabled = PhotosButton.IsEnabled = FlattenButton.IsEnabled = Folder is not null;

    private void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateButtons();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (Folder is { } current)
            dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    private void TempFolder_Click(object sender, RoutedEventArgs e) =>
        PathBox.Text = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is { } folder)
            new SizeWindow(folder).Show();
    }

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is { } folder)
            new CleanWindow(folder).Show();
    }

    private void Photos_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is { } folder)
            new PhotoSortWindow(folder).Show();
    }

    private void Flatten_Click(object sender, RoutedEventArgs e)
    {
        if (Folder is { } folder)
            new FlattenWindow(folder).Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.OpenSettings(this);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolder(e) is not null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedFolder(e) is { } folder)
            PathBox.Text = folder;
    }

    private static string? DroppedFolder(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && Directory.Exists(paths[0]) ? paths[0] : null;
}
