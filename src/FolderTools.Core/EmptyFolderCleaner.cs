using System.IO.Enumeration;

namespace FolderTools.Core;

/// <summary>An empty folder found by <see cref="EmptyFolderCleaner.Find"/>, deleted with its empty subfolders.</summary>
/// <param name="NestedCount">Empty subfolders inside it, at every level, that go with it.</param>
public sealed record EmptyFolder(string Path, int NestedCount)
{
    /// <summary>Folders removed when this one is deleted: itself and its empty subfolders.</summary>
    public int TotalCount => NestedCount + 1;
}

public sealed record EmptyFolderSearch(IReadOnlyList<EmptyFolder> Folders, int InaccessibleCount)
{
    public int TotalCount => Folders.Sum(f => f.TotalCount);
}

public enum DeleteFailureReason
{
    /// <summary>Something was added to the folder since it was found.</summary>
    NoLongerEmpty,
    AccessDenied,
    /// <summary>Open in a program, locked...</summary>
    InUse,
}

public sealed record DeleteFailure(string Path, DeleteFailureReason Reason);

/// <param name="DeletedCount">Folders removed, empty subfolders included.</param>
public sealed record DeleteReport(int DeletedCount, IReadOnlyList<DeleteFailure> Failures);

/// <summary>
/// Finds and deletes empty folders. A folder is empty when it holds no file other than the ignored ones
/// (<c>Thumbs.db</c>, <c>desktop.ini</c>...) and its subfolders are empty too. The folder the search starts from
/// is never deleted, and junctions and symbolic links count as content: they are never followed nor removed.
/// </summary>
public sealed class EmptyFolderCleaner
{
    // Recycle bin batches: one shell call per folder would be slow on thousands of folders.
    private const int RecycleBatchSize = 64;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string[] _ignoredPatterns;

    /// <param name="ignoredFiles">File names that do not count as content; <c>*</c> and <c>?</c> are allowed.</param>
    public EmptyFolderCleaner(IEnumerable<string> ignoredFiles)
    {
        _ignoredPatterns = ignoredFiles.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }

    public bool IsIgnored(string fileName) =>
        _ignoredPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));

    /// <summary>
    /// The empty folders under <paramref name="root"/>, as the outermost ones only: a folder that only holds
    /// empty folders is listed once, with its <see cref="EmptyFolder.NestedCount"/>.
    /// </summary>
    public EmptyFolderSearch Find(string root, IProgress<string>? currentFolder = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        var found = new List<EmptyFolder>();
        int inaccessible = 0;
        Visit(root, isRoot: true, found, ref inaccessible, currentFolder, cancellationToken);
        return new EmptyFolderSearch(found.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), inaccessible);
    }

    /// <summary>
    /// Looks at <paramref name="path"/>: returns its number of empty subfolders when it is empty, -1 otherwise.
    /// The empty subfolders of a folder that is not empty are the outermost ones: they go to <paramref name="found"/>.
    /// The root is handled as a folder that is not empty: it is never deleted.
    /// </summary>
    private int Visit(string path, bool isRoot, List<EmptyFolder> found, ref int inaccessible, IProgress<string>? currentFolder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        currentFolder?.Report(path);

        bool hasContent = false;
        var subfolders = new List<string>();
        try
        {
            foreach (var (name, isDirectory, isReparsePoint) in Enumerate(path))
            {
                if (!isDirectory)
                {
                    if (!IsIgnored(name))
                        hasContent = true;
                }
                else if (isReparsePoint && FolderScanner.IsLink(Path.Join(path, name)))
                    hasContent = true;
                else
                    subfolders.Add(Path.Join(path, name));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            inaccessible++;
            return -1; // unknown content: never deleted
        }

        var emptyChildren = new List<EmptyFolder>();
        int nested = 0;
        foreach (string subfolder in subfolders)
        {
            int childNested = Visit(subfolder, isRoot: false, found, ref inaccessible, currentFolder, cancellationToken);
            if (childNested < 0)
                hasContent = true;
            else
            {
                emptyChildren.Add(new EmptyFolder(subfolder, childNested));
                nested += childNested + 1;
            }
        }

        if (hasContent || isRoot)
        {
            found.AddRange(emptyChildren);
            return -1;
        }
        return nested;
    }

    /// <summary>Looks again just before deleting: the folder may have been filled since it was found.</summary>
    public bool IsEmpty(string path)
    {
        try
        {
            foreach (var (name, isDirectory, isReparsePoint) in Enumerate(path))
            {
                if (!isDirectory)
                {
                    if (!IsIgnored(name))
                        return false;
                }
                else if (isReparsePoint && FolderScanner.IsLink(Path.Join(path, name)))
                    return false;
                else if (!IsEmpty(Path.Join(path, name)))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes <paramref name="folders"/>, each one checked again first. Without <paramref name="recycle"/>, they
    /// are removed for good, and a folder that receives a file in the meantime is left alone (only empty folders
    /// and ignored files are removed). With it, they go to the recycle bin through that function, which gets a
    /// batch of paths. <paramref name="progress"/> receives the number of folders handled so far.
    /// </summary>
    public DeleteReport Delete(IReadOnlyList<EmptyFolder> folders, Action<IReadOnlyList<string>>? recycle = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        int deleted = 0, handled = 0;
        var failures = new List<DeleteFailure>();

        foreach (var batch in folders.Chunk(recycle is null ? 1 : RecycleBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = new List<EmptyFolder>();
            foreach (var folder in batch)
            {
                if (!Directory.Exists(folder.Path))
                    continue; // already gone (a temp folder removed by its program): nothing to do
                if (IsEmpty(folder.Path))
                    ready.Add(folder);
                else
                    failures.Add(new DeleteFailure(folder.Path, Directory.Exists(folder.Path) && CanList(folder.Path)
                        ? DeleteFailureReason.NoLongerEmpty
                        : DeleteFailureReason.AccessDenied));
            }

            if (recycle is not null && ready.Count > 0)
            {
                recycle(ready.Select(f => f.Path).ToList());
                foreach (var folder in ready)
                {
                    if (Directory.Exists(folder.Path))
                        failures.Add(new DeleteFailure(folder.Path, DeleteFailureReason.InUse));
                    else
                        deleted += folder.TotalCount;
                }
            }
            else
            {
                foreach (var folder in ready)
                {
                    try
                    {
                        deleted += DeleteTree(folder.Path);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        failures.Add(new DeleteFailure(folder.Path, DeleteFailureReason.AccessDenied));
                    }
                    catch (IOException)
                    {
                        failures.Add(new DeleteFailure(folder.Path, Directory.Exists(folder.Path) && !IsEmpty(folder.Path)
                            ? DeleteFailureReason.NoLongerEmpty
                            : DeleteFailureReason.InUse));
                    }
                }
            }

            handled += batch.Length;
            progress?.Report(handled);
        }
        return new DeleteReport(deleted, failures);
    }

    // Removes ignored files and empty folders only: RemoveDirectory refuses a folder that is not empty, so a
    // file created in the meantime stops the deletion instead of being lost. Returns the folders removed.
    private int DeleteTree(string path)
    {
        int count = 0;
        foreach (var (name, isDirectory, _) in Enumerate(path).ToList())
        {
            string child = Path.Join(path, name);
            if (isDirectory)
                count += DeleteTree(child);
            else if (IsIgnored(name))
            {
                File.SetAttributes(child, FileAttributes.Normal); // desktop.ini is hidden and system, sometimes read-only
                File.Delete(child);
            }
        }
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReadOnly) != 0)
            info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete(recursive: false);
        return count + 1;
    }

    private static bool CanList(string path)
    {
        try
        {
            using var e = Enumerate(path).GetEnumerator();
            e.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static IEnumerable<(string Name, bool IsDirectory, bool IsReparsePoint)> Enumerate(string path) =>
        new FileSystemEnumerable<(string, bool, bool)>(path,
            (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
            Options);
}
