using System.Globalization;
using System.IO;
using FolderTools.Core;

namespace FolderTools;

/// <summary>A line of the “Largest files” tab of the size window.</summary>
public sealed class FileRow(FileEntry file, FolderNode root)
{
    private static readonly HashSet<string> Videos = [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg"];
    private static readonly HashSet<string> Images = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".heic", ".webp", ".bmp", ".gif", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".psd"];
    private static readonly HashSet<string> Audio = [".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".aiff"];
    private static readonly HashSet<string> Archives = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".cab"];
    private static readonly HashSet<string> DiskImages = [".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".wim", ".esd"];

    public FileEntry File { get; } = file;

    public string Name => File.Name;

    // Segoe Fluent Icons, by kind of file.
    public string Glyph
    {
        get
        {
            string extension = Path.GetExtension(File.Name).ToLowerInvariant();
            return Videos.Contains(extension) ? ""
                : Images.Contains(extension) ? ""
                : Audio.Contains(extension) ? ""
                : Archives.Contains(extension) ? ""
                : DiskImages.Contains(extension) ? ""
                : "";
        }
    }

    public string SizeText => WindowsShell.FormatSize(File.Length);

    /// <summary>Share of the folder shown at the top of the window (0 to 1).</summary>
    public double Share => root.Size > 0 ? (double)File.Length / root.Size : 0;

    public string ShareText => (Share * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %";

    /// <summary>The file's folder, relative to the folder shown; the name of the folder shown for its own files.</summary>
    public string FolderText => File.Folder == root
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root.FullPath)) is { Length: > 0 } name ? name : root.FullPath
        : Path.GetRelativePath(root.FullPath, File.Folder.FullPath);

    public string ModifiedText => File.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string ToolTip => File.FullPath;
}
