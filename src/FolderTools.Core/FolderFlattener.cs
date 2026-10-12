using System.IO.Enumeration;
using System.Security.Cryptography;

namespace FolderTools.Core;

/// <summary>How deep a flatten goes.</summary>
public enum FlattenDepth
{
    /// <summary>Every file of every subfolder comes up to the folder (default).</summary>
    AllLevels,
    /// <summary>The content of the direct subfolders comes up one level: their files, and their own subfolders whole.</summary>
    OneLevel,
}

/// <summary>What a name already taken in the folder becomes.</summary>
public enum FlattenNaming
{
    /// <summary><c>IMG_001 (2).jpg</c> (default).</summary>
    Suffix,
    /// <summary><c>Noël - IMG_001.jpg</c>: the folders it came from, then the name (only when the name is taken).</summary>
    Prefix,
}

/// <summary>A file (or, one level only, a folder) that comes up to the flattened folder.</summary>
/// <param name="Relative">Its path relative to the flattened folder, such as <c>Photos\Noël\IMG_001.jpg</c>.</param>
/// <param name="IdenticalTo">The file of the same name and the same content it would be renamed against, if any.</param>
public sealed record FlattenItem(string Path, string Relative, bool IsFolder, string? IdenticalTo);

public sealed record FlattenPlan(string Root, FlattenDepth Depth, FlattenNaming Naming, IReadOnlyList<FlattenItem> Items,
    IReadOnlyList<string> EmptiedFolders, IReadOnlySet<string> TakenNames, int InaccessibleCount)
{
    public IEnumerable<FlattenItem> Identical => Items.Where(i => i.IdenticalTo is not null);
}

/// <summary>Where an item goes: its new name in the flattened folder, or null when it goes to the recycle bin.</summary>
public sealed record FlattenTarget(FlattenItem Item, string? Name)
{
    public bool IsRenamed => Name is not null && !string.Equals(Name, System.IO.Path.GetFileName(Item.Path), StringComparison.OrdinalIgnoreCase);
}

public sealed record FlattenMove(string From, string To, bool IsFolder);

/// <param name="Reason">The system's message, or null for a file the recycle bin did not take (in use).</param>
public sealed record FlattenFailure(string Path, string? Reason);

/// <param name="RemovedFolders">The subfolders deleted once emptied (an undo creates them again).</param>
public sealed record FlattenResult(IReadOnlyList<FlattenMove> Moves, IReadOnlyList<string> Recycled,
    IReadOnlyList<string> RemovedFolders, IReadOnlyList<FlattenFailure> Failures);

/// <summary>
/// Brings the files of the subfolders of a folder up into it, then deletes the subfolders left empty. Nothing is
/// overwritten: a name already taken gets “ (2)” or the names of the folders it came from. A file whose name is
/// taken by a file with the same content can go to the recycle bin instead. Ignored folders (<c>.git</c>…), links
/// and junctions are never looked into nor moved; ignored files (<c>Thumbs.db</c>…) stay and go with their folder.
/// </summary>
public sealed class FolderFlattener(EmptyFolderCleaner cleaner)
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public FlattenPlan Plan(string root, FlattenDepth depth, FlattenNaming naming, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var subfolders = new List<string>();
        foreach (var entry in Enumerate(root))
        {
            taken.Add(entry.Name);
            string path = System.IO.Path.Join(root, entry.Name);
            if (!entry.IsDirectory)
                rootFiles[entry.Name] = path;
            else if (!cleaner.IsIgnoredFolder(entry.Name) && !(entry.IsReparsePoint && FolderScanner.IsLink(path)))
                subfolders.Add(path);
        }

        subfolders.Sort(StringComparer.OrdinalIgnoreCase);
        var items = new List<(string Path, bool IsFolder)>();
        var emptied = new List<string>();
        int inaccessible = 0;
        foreach (string subfolder in subfolders)
        {
            // A subfolder that keeps something (an ignored folder, a link, a folder that could not be read) stays.
            bool empties = depth == FlattenDepth.AllLevels
                ? Collect(subfolder, items, ref inaccessible, progress, cancellationToken)
                : CollectOneLevel(subfolder, items, ref inaccessible);
            if (empties)
                emptied.Add(subfolder);
        }

        // The first file of a name keeps it (a file already in the folder first); a later one with the same content
        // is offered for the recycle bin.
        var owners = new Dictionary<string, string>(rootFiles, StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FlattenItem>();
        foreach (var (path, isFolder) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = System.IO.Path.GetFileName(path);
            string? identical = null;
            if (!isFolder)
            {
                if (owners.TryGetValue(name, out string? owner))
                    identical = SameContent(owner, path, hashes) ? owner : null;
                else
                    owners[name] = path;
            }
            result.Add(new FlattenItem(path, System.IO.Path.GetRelativePath(root, path), isFolder, identical));
        }
        return new FlattenPlan(root, depth, naming, result, emptied, taken, inaccessible);
    }

    // Returns whether the folder is left empty once its items have moved.
    private bool Collect(string folder, List<(string, bool)> items, ref int inaccessible, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<string>();
        var folders = new List<string>();
        bool empties = true;
        try
        {
            foreach (var entry in Enumerate(folder))
            {
                string path = System.IO.Path.Join(folder, entry.Name);
                if (!entry.IsDirectory)
                {
                    if (!cleaner.IsIgnored(entry.Name))
                        files.Add(path);
                }
                else if (!cleaner.IsIgnoredFolder(entry.Name) && !(entry.IsReparsePoint && FolderScanner.IsLink(path)))
                    folders.Add(path);
                else
                    empties = false;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            inaccessible++;
            return false;
        }
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
            items.Add((file, false));
        progress?.Report(items.Count);
        foreach (string sub in folders.Order(StringComparer.OrdinalIgnoreCase))
            empties &= Collect(sub, items, ref inaccessible, progress, cancellationToken);
        return empties;
    }

    // One level: the files and the folders right inside the subfolder (links and ignored folders stay).
    private bool CollectOneLevel(string folder, List<(string, bool)> items, ref int inaccessible)
    {
        bool empties = true;
        try
        {
            var entries = Enumerate(folder).ToList();
            foreach (var entry in entries.Where(e => !e.IsDirectory && !cleaner.IsIgnored(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                items.Add((System.IO.Path.Join(folder, entry.Name), false));
            foreach (var entry in entries.Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                string path = System.IO.Path.Join(folder, entry.Name);
                if (!cleaner.IsIgnoredFolder(entry.Name) && !(entry.IsReparsePoint && FolderScanner.IsLink(path)))
                    items.Add((path, true));
                else
                    empties = false;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            inaccessible++;
            return false;
        }
        return empties;
    }

    private static bool SameContent(string a, string b, Dictionary<string, byte[]?> hashes)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
                return false;
            byte[]? ha = Hash(a, hashes), hb = Hash(b, hashes);
            return ha is not null && hb is not null && ha.AsSpan().SequenceEqual(hb);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static byte[]? Hash(string path, Dictionary<string, byte[]?> hashes)
    {
        if (!hashes.TryGetValue(path, out byte[]? hash))
        {
            try
            {
                using var stream = File.OpenRead(path);
                hash = SHA256.HashData(stream);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                hash = null;
            }
            hashes[path] = hash;
        }
        return hash;
    }

    /// <summary>
    /// The new name of each item. <paramref name="recycle"/> holds the identical files the user lets go to the
    /// recycle bin: they get no name, and do not take one from the others.
    /// </summary>
    public static IReadOnlyList<FlattenTarget> Resolve(FlattenPlan plan, IReadOnlySet<FlattenItem> recycle)
    {
        // Every name of the folder counts as taken, the subfolders being emptied too: they are still there while
        // the items move.
        var taken = new HashSet<string>(plan.TakenNames, StringComparer.OrdinalIgnoreCase);
        var targets = new List<FlattenTarget>();
        foreach (var item in plan.Items)
        {
            if (recycle.Contains(item))
            {
                targets.Add(new FlattenTarget(item, null));
                continue;
            }
            string name = System.IO.Path.GetFileName(item.Path);
            if (taken.Contains(name))
                name = FreeName(item, plan.Naming, taken);
            taken.Add(name);
            targets.Add(new FlattenTarget(item, name));
        }
        return targets;
    }

    private static string FreeName(FlattenItem item, FlattenNaming naming, HashSet<string> taken)
    {
        string name = System.IO.Path.GetFileName(item.Path);
        if (naming == FlattenNaming.Prefix)
        {
            // "Photos\Noël\IMG_001.jpg" → "Photos - Noël - IMG_001.jpg".
            string folders = System.IO.Path.GetDirectoryName(item.Relative) ?? "";
            name = string.Join(" - ", folders.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Append(name));
            if (!taken.Contains(name))
                return name;
        }
        return WithSuffix(name, item.IsFolder, n => !taken.Contains(n));
    }

    // "name (2).ext", "name (3).ext"… the first one accepted by isFree; a folder has no extension.
    private static string WithSuffix(string name, bool isFolder, Func<string, bool> isFree)
    {
        string stem = isFolder ? name : System.IO.Path.GetFileNameWithoutExtension(name);
        string extension = isFolder ? "" : System.IO.Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){extension}";
            if (isFree(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Moves the items to their targets, sends the identical files let go to the recycle bin through
    /// <paramref name="recycleFiles"/>, then deletes the subfolders left empty (ignored files go with them).
    /// </summary>
    public FlattenResult Execute(FlattenPlan plan, IReadOnlyList<FlattenTarget> targets,
        Action<IReadOnlyList<string>>? recycleFiles, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var moves = new List<FlattenMove>();
        var failures = new List<FlattenFailure>();
        var recycled = new List<string>();
        int done = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = target.Item;
            if (target.Name is null)
            {
                recycled.Add(item.Path);
                continue;
            }
            // Something may have taken the name since the preview: never overwrite.
            string to = System.IO.Path.Join(plan.Root, target.Name);
            if (Exists(to))
                to = System.IO.Path.Join(plan.Root, WithSuffix(target.Name, item.IsFolder, n => !Exists(System.IO.Path.Join(plan.Root, n))));
            try
            {
                if (item.IsFolder)
                    Directory.Move(item.Path, to);
                else
                    File.Move(item.Path, to, overwrite: false);
                moves.Add(new FlattenMove(item.Path, to, item.IsFolder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new FlattenFailure(item.Path, ex.Message));
            }
            progress?.Report(++done);
        }

        if (recycled.Count > 0 && recycleFiles is not null)
        {
            recycleFiles(recycled);
            foreach (string path in recycled.Where(File.Exists).ToList())
            {
                failures.Add(new FlattenFailure(path, null));
                recycled.Remove(path);
            }
        }
        else
            recycled.Clear();

        // The emptied subfolders: deleted for good when nothing but ignored files and empty folders is left (checked
        // again by the cleaner, so a folder that still holds a file that could not move stays).
        var removed = new List<string>();
        var emptied = plan.EmptiedFolders.Where(Directory.Exists).Where(cleaner.IsEmpty).Select(f => new EmptyFolder(f, 0)).ToList();
        cleaner.Delete(emptied, recycle: null, cancellationToken: cancellationToken);
        removed.AddRange(emptied.Select(f => f.Path).Where(f => !Directory.Exists(f)));
        return new FlattenResult(moves, recycled, removed, failures);
    }

    /// <summary>
    /// Creates the deleted subfolders again and moves every item back (with “ (2)” if its old name was taken
    /// meanwhile). The files sent to the recycle bin stay there.
    /// </summary>
    public static IReadOnlyList<FlattenFailure> Undo(FlattenResult result)
    {
        var failures = new List<FlattenFailure>();
        foreach (string folder in result.RemovedFolders)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var move in result.Moves.Reverse())
        {
            try
            {
                string back = move.From;
                string folder = System.IO.Path.GetDirectoryName(back)!;
                Directory.CreateDirectory(folder);
                if (Exists(back))
                    back = System.IO.Path.Join(folder, WithSuffix(System.IO.Path.GetFileName(back), move.IsFolder,
                        n => !Exists(System.IO.Path.Join(folder, n))));
                if (move.IsFolder)
                    Directory.Move(move.To, back);
                else
                    File.Move(move.To, back, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new FlattenFailure(move.To, ex.Message));
            }
        }
        return failures;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static IEnumerable<(string Name, bool IsDirectory, bool IsReparsePoint)> Enumerate(string path) =>
        new FileSystemEnumerable<(string, bool, bool)>(path,
            (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
            Options);
}
