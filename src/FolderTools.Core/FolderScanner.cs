using System.IO.Enumeration;

namespace FolderTools.Core;

/// <summary>
/// Measures a folder and all its subfolders: size, number of files and folders, newest file.
/// Hidden and system files are counted. Junctions and symbolic links are not followed (their target would be
/// counted twice, and a link to a parent would loop); cloud folders such as OneDrive's are read normally.
/// The first levels are scanned in parallel, which is much faster on SSDs.
/// </summary>
public sealed class FolderScanner
{
    // Levels scanned in parallel: below that, a folder's subfolders are scanned one after the other.
    private const int ParallelDepth = 3;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // hidden and system files take room too
        IgnoreInaccessible = false, // reported, not silently skipped
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private long _files;
    private long _folders;
    private long _bytes;
    private volatile string _currentFolder = "";

    /// <summary>Files counted so far (for a progress display, read from any thread).</summary>
    public long FilesScanned => Interlocked.Read(ref _files);

    public long FoldersScanned => Interlocked.Read(ref _folders);

    public long BytesScanned => Interlocked.Read(ref _bytes);

    /// <summary>One of the folders being read, for a progress display.</summary>
    public string CurrentFolder => _currentFolder;

    /// <summary>The root node, available as soon as <see cref="ScanAsync"/> has started.</summary>
    public FolderNode? Root { get; private set; }

    /// <summary>Scans <paramref name="path"/> on worker threads. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public Task<FolderNode> ScanAsync(string path, CancellationToken cancellationToken = default)
    {
        var root = FolderNode.CreateRoot(path);
        Root = root;
        return Task.Run(() =>
        {
            ScanFolder(root, path, 0, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return root;
        }, cancellationToken);
    }

    /// <summary>Scans synchronously, on the calling thread (and worker threads for the first levels).</summary>
    public FolderNode Scan(string path, CancellationToken cancellationToken = default) =>
        ScanAsync(path, cancellationToken).GetAwaiter().GetResult();

    private void ScanFolder(FolderNode node, string path, int depth, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;
        _currentFolder = path;

        var subfolders = new List<(string Name, bool IsReparsePoint)>();
        DateTime? newest = null;
        try
        {
            var entries = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options);
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    subfolders.Add((entry.Name, entry.IsReparsePoint));
                    continue;
                }
                node.OwnFileCount++;
                node.OwnFilesSize += entry.Length;
                if (newest is null || entry.LastWriteUtc > newest)
                    newest = entry.LastWriteUtc;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Access denied, folder deleted during the scan...: what was read so far is kept.
            node.IsInaccessible = true;
        }

        Interlocked.Add(ref _files, node.OwnFileCount);
        Interlocked.Add(ref _bytes, node.OwnFilesSize);
        Interlocked.Increment(ref _folders);

        var children = new FolderNode[subfolders.Count];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = node.CreateChild(subfolders[i].Name);
            if (subfolders[i].IsReparsePoint && IsLink(Path.Join(path, subfolders[i].Name)))
                children[i].IsLink = true;
        }
        node.Children = children;

        void ScanChild(FolderNode child)
        {
            if (child.IsLink)
                child.IsComplete = true;
            else
                ScanFolder(child, Path.Join(path, child.Name), depth + 1, cancellationToken);
        }

        if (depth < ParallelDepth && children.Length > 1)
            Parallel.ForEach(children, ScanChild);
        else
            foreach (var child in children)
                ScanChild(child);

        if (cancellationToken.IsCancellationRequested)
            return;

        long size = node.OwnFilesSize, files = node.OwnFileCount, folders = children.Length;
        long inaccessible = node.IsInaccessible ? 1 : 0;
        foreach (var child in children)
        {
            size += child.Size;
            files += child.FileCount;
            folders += child.FolderCount;
            inaccessible += child.InaccessibleCount;
            if (child.LastModifiedUtc is { } date && (newest is null || date > newest))
                newest = date;
        }
        node.Size = size;
        node.FileCount = files;
        node.FolderCount = folders;
        node.InaccessibleCount = inaccessible;
        node.LastModifiedUtc = newest;
        node.IsComplete = true;
    }

    /// <summary>
    /// True for a junction or a symbolic link. Other reparse points (OneDrive and other cloud folders,
    /// deduplicated files...) are real folders and must be read.
    /// </summary>
    internal static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true; // unknown kind of reparse point that cannot be read: safer not to follow it
        }
    }

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, DateTime LastWriteUtc, bool IsReparsePoint);
}
