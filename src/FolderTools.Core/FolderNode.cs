namespace FolderTools.Core;

/// <summary>
/// A folder of a size analysis (<see cref="FolderScanner"/>) and its totals, subfolders included.
/// The scanner fills a node on a worker thread; its totals are final once <see cref="IsComplete"/> is true.
/// <see cref="Children"/> is assigned once, in full, before the subfolders are scanned, so a reader may list
/// them while they are still being scanned.
/// </summary>
public sealed class FolderNode
{
    private readonly string _rootPath; // only for the root: the others build their path from their parent's
    private volatile bool _isComplete;
    private volatile FolderNode[] _children = [];

    private FolderNode(string name, FolderNode? parent, string rootPath)
    {
        Name = name;
        Parent = parent;
        _rootPath = rootPath;
    }

    public static FolderNode CreateRoot(string path) => new(DisplayName(path), null, path);

    public FolderNode CreateChild(string name) => new(name, this, "");

    public string Name { get; }

    public FolderNode? Parent { get; private set; }

    // Built on demand: storing every full path would cost a lot of memory on a whole drive.
    public string FullPath => Parent is null ? _rootPath : Path.Join(Parent.FullPath, Name);

    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    public FolderNode[] Children
    {
        get => _children;
        internal set => _children = value;
    }

    /// <summary>Total size of the files, in bytes, subfolders included.</summary>
    public long Size { get; internal set; }

    /// <summary>Number of files, subfolders included.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Number of folders inside this one, at every level.</summary>
    public long FolderCount { get; internal set; }

    /// <summary>Size of the files directly in this folder.</summary>
    public long OwnFilesSize { get; internal set; }

    /// <summary>Number of files directly in this folder.</summary>
    public long OwnFileCount { get; internal set; }

    /// <summary>Last write time of the newest file, subfolders included; null when there is no file.</summary>
    public DateTime? LastModifiedUtc { get; internal set; }

    /// <summary>A junction or symbolic link: listed, but not followed (its target is counted where it lives).</summary>
    public bool IsLink { get; internal set; }

    /// <summary>The folder could not be listed (access denied, deleted during the scan...).</summary>
    public bool IsInaccessible { get; internal set; }

    /// <summary>Number of folders that could not be listed, this one and its subfolders included.</summary>
    public long InaccessibleCount { get; internal set; }

    public bool IsComplete
    {
        get => _isComplete;
        internal set => _isComplete = value;
    }

    /// <summary>Share of the parent's size, from 0 to 1 (1 for the root).</summary>
    public double ShareOfParent => Parent is null ? 1 : Parent.Size > 0 ? (double)Size / Parent.Size : 0;

    /// <summary>
    /// Takes this folder out of the tree once it has been deleted, and subtracts its totals from every parent.
    /// Only for a complete tree.
    /// </summary>
    public void Detach()
    {
        if (Parent is not { } parent)
            return;
        parent.Children = parent.Children.Where(c => c != this).ToArray();
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Size -= Size;
            ancestor.FileCount -= FileCount;
            ancestor.FolderCount -= FolderCount + 1;
            ancestor.InaccessibleCount -= InaccessibleCount;
        }
        Parent = null;
    }

    // "C:\" stays "C:\", "D:\Photos\" becomes "Photos".
    private static string DisplayName(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path;
    }

    public override string ToString() => FullPath;
}
