using System.ComponentModel;
using System.Globalization;
using System.Windows;
using FolderTools.Core;

namespace FolderTools;

/// <summary>
/// One line of the size tree: a folder, or the files sitting directly in a folder that also has subfolders
/// (so that the lines under a folder add up to it). The tree is shown as a flat list with indented names.
/// </summary>
public sealed class SizeRow(FolderNode node, bool isFilesRow, Func<long>? liveBytes = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public FolderNode Node { get; } = node;

    /// <summary>The "files in this folder" line; <see cref="Node"/> is then the folder that holds them.</summary>
    public bool IsFilesRow { get; } = isFilesRow;

    public int Level { get; set; }

    public bool IsExpanded { get; set; }

    public Thickness Indent => new(Level * 18, 0, 0, 0);

    public bool IsPending => !IsFilesRow && !Node.IsComplete;

    public bool IsExpandable => !IsFilesRow && Node.Children.Length > 0;

    public long SortSize => IsFilesRow ? Node.OwnFilesSize : Node.Size;

    public long SortFiles => IsFilesRow ? Node.OwnFileCount : Node.FileCount;

    public long SortFolders => IsFilesRow ? -1 : Node.FolderCount;

    public DateTime SortModified => IsFilesRow ? DateTime.MinValue : Node.LastModifiedUtc ?? DateTime.MinValue;

    public string Name => IsFilesRow ? Strings.Plural("FilesRow", Node.OwnFileCount, WindowsShell.FormatCount(Node.OwnFileCount)) : Node.Name;

    public string Glyph =>
        IsFilesRow ? "" // page
        : Node.IsLink ? "" // link
        : Node.IsInaccessible ? "" // lock
        : ""; // folder

    public string ToolTip =>
        IsFilesRow ? Node.FullPath
        : Node.IsLink ? Strings.Format("LinkToolTip", Node.FullPath)
        : Node.IsInaccessible ? Strings.Format("InaccessibleToolTip", Node.FullPath)
        : Node.FullPath;

    public string Chevron => IsExpanded ? "" : "";

    public Visibility ChevronVisibility => IsExpandable ? Visibility.Visible : Visibility.Hidden;

    public FontStyle FontStyle => IsFilesRow || Node.IsLink ? FontStyles.Italic : FontStyles.Normal;

    public string SizeText =>
        IsPending ? liveBytes is not null ? WindowsShell.FormatSize(liveBytes()) + "…" : "…"
        : WindowsShell.FormatSize(SortSize);

    // A share needs the total of the folder above, known once that folder is complete.
    private bool IsShareKnown => !IsPending && (Level == 0 || IsFilesRow || Node.Parent is not { IsComplete: false });

    /// <summary>Share of the folder above (0 to 1), for the bar.</summary>
    public double Share =>
        !IsShareKnown ? 0
        : IsFilesRow ? Node.Size > 0 ? (double)Node.OwnFilesSize / Node.Size : 0
        : Level == 0 ? 1
        : Node.ShareOfParent;

    public string ShareText => !IsShareKnown ? "" : (Share * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %";

    public string FilesText => IsPending ? "" : WindowsShell.FormatCount(SortFiles);

    public string FoldersText => IsPending || IsFilesRow ? "" : WindowsShell.FormatCount(Node.FolderCount);

    public string ModifiedText =>
        IsPending || IsFilesRow || Node.LastModifiedUtc is not { } date ? ""
        : date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Shows the current values (the scan fills the nodes on other threads).</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
