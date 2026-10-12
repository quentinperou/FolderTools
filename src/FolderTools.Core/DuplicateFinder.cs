using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace FolderTools.Core;

/// <summary>Which copy of a group is kept by default (the user can change it file by file).</summary>
public enum DuplicateKeep
{
    /// <summary>The one modified first: usually the original (default).</summary>
    Oldest,
    Newest,
    /// <summary>The one least deep in the subfolders.</summary>
    ShortestPath,
}

public sealed record DuplicateFile(string Path, DateTime LastWriteUtc);

/// <summary>Files with the same content (byte for byte).</summary>
public sealed record DuplicateGroup(long Length, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>The space freed by keeping a single copy.</summary>
    public long Wasted => Length * (Files.Count - 1);
}

public sealed record DuplicateSearch(IReadOnlyList<DuplicateGroup> Groups, int FileCount, int InaccessibleCount)
{
    public int ExtraCopies => Groups.Sum(g => g.Files.Count - 1);

    public long Wasted => Groups.Sum(g => g.Wasted);
}

/// <summary>What the search is doing: listing the files, then comparing those of the same size.</summary>
public sealed record DuplicateProgress(bool Comparing, int Count, long BytesDone, long BytesTotal);

/// <param name="Reason">The system's message, or null when the file is still there after the recycle bin (in use),
/// or when its group lost the copy it was keeping.</param>
public sealed record DuplicateFailure(string Path, string? Reason);

public sealed record DuplicateDeleteReport(int RecycledCount, long Freed, IReadOnlyList<DuplicateFailure> Failures);

/// <summary>
/// Finds the files of a folder and its subfolders that have the same content. Only files of the same size are
/// read: first their beginning, then, when it matches, the whole file (SHA-256). Empty files, hidden and system
/// files, the ignored files and folders of the settings, links and junctions, and cloud files not on the disk
/// (reading them would download them) are left out; so are the extra names of a file with hard links, which take
/// no space.
/// </summary>
public sealed class DuplicateFinder(EmptyFolderCleaner cleaner)
{
    private const int HeadLength = 64 * 1024;
    private const int RecycleBatchSize = 64;

    // A OneDrive / cloud file that is not on the disk: reading it would download it.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly record struct Entry(string Path, long Length, DateTime LastWriteUtc);

    public DuplicateSearch Find(string root, IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new List<Entry>();
        int inaccessible = 0;
        Collect(root, files, ref inaccessible, progress, cancellationToken);

        var sameSize = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).ToList();
        long total = sameSize.Sum(g => g.Key * g.Count());
        long done = 0;
        int compared = 0;
        var groups = new List<DuplicateGroup>();
        foreach (var bySize in sameSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = WithoutHardLinks(bySize);
            // The beginning first: most files of the same size differ there, and are not read further.
            foreach (var byHead in GroupByHash(candidates, head: true))
            {
                foreach (var byContent in byHead.Count > 1 && bySize.Key > HeadLength ? GroupByHash(byHead, head: false) : [byHead])
                    if (byContent.Count > 1)
                        groups.Add(new DuplicateGroup(bySize.Key, byContent.Select(e => new DuplicateFile(e.Path, e.LastWriteUtc))
                            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()));
            }
            done += bySize.Key * bySize.Count();
            compared += bySize.Count();
            progress?.Report(new DuplicateProgress(true, compared, done, total));
        }
        return new DuplicateSearch(groups.OrderByDescending(g => g.Wasted).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList(),
            files.Count, inaccessible);
    }

    private void Collect(string folder, List<Entry> files, ref int inaccessible, IProgress<DuplicateProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var subfolders = new List<string>();
        try
        {
            var entries = new FileSystemEnumerable<(string Name, bool IsDirectory, FileAttributes Attributes, long Length, DateTime LastWriteUtc)>(folder,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Attributes, e.Length, e.LastWriteTimeUtc.UtcDateTime), Options);
            foreach (var e in entries)
            {
                string path = Path.Join(folder, e.Name);
                if (e.IsDirectory)
                {
                    if (!cleaner.IsIgnoredFolder(e.Name) && !((e.Attributes & FileAttributes.ReparsePoint) != 0 && FolderScanner.IsLink(path)))
                        subfolders.Add(path);
                }
                else if (e.Length > 0 && !cleaner.IsIgnored(e.Name)
                    && (e.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) == 0)
                    files.Add(new Entry(path, e.Length, e.LastWriteUtc));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            inaccessible++;
            return;
        }
        progress?.Report(new DuplicateProgress(false, files.Count, 0, 0));
        foreach (string subfolder in subfolders)
            Collect(subfolder, files, ref inaccessible, progress, cancellationToken);
    }

    // Names of the same file (hard links): one is enough, deleting another would free nothing.
    private static List<Entry> WithoutHardLinks(IEnumerable<Entry> entries)
    {
        var seen = new HashSet<(uint, ulong)>();
        var result = new List<Entry>();
        foreach (var entry in entries)
        {
            var id = FileId(entry.Path);
            if (id is null || seen.Add(id.Value))
                result.Add(entry);
        }
        return result;
    }

    private static List<List<Entry>> GroupByHash(List<Entry> entries, bool head)
    {
        var groups = new Dictionary<string, List<Entry>>();
        foreach (var entry in entries)
        {
            string? hash = Hash(entry.Path, head);
            if (hash is null)
                continue; // could not be read: never reported as a duplicate
            if (!groups.TryGetValue(hash, out var list))
                groups[hash] = list = [];
            list.Add(entry);
        }
        return groups.Values.ToList();
    }

    private static string? Hash(string path, bool head)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16, FileOptions.SequentialScan);
            if (!head)
                return Convert.ToHexString(SHA256.HashData(stream));
            byte[] buffer = new byte[HeadLength];
            int read = stream.ReadAtLeast(buffer, HeadLength, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The copy kept by default: by date, the shortest path breaking ties (then the path itself).</summary>
    public static DuplicateFile Keeper(DuplicateGroup group, DuplicateKeep keep)
    {
        var byPath = group.Files.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return keep switch
        {
            DuplicateKeep.Oldest => group.Files.OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
            DuplicateKeep.Newest => group.Files.OrderByDescending(f => f.LastWriteUtc).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First(),
            _ => byPath.First(),
        };
    }

    /// <summary>
    /// Sends <paramref name="toRecycle"/> to the recycle bin through <paramref name="recycle"/>, in batches. A group
    /// is skipped when none of the copies it keeps is still there with the same size: the last copy is never removed.
    /// </summary>
    public static DuplicateDeleteReport Recycle(IReadOnlyList<DuplicateGroup> groups, IReadOnlySet<string> toRecycle,
        Action<IReadOnlyList<string>> recycle, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var failures = new List<DuplicateFailure>();
        var ready = new List<(string Path, long Length)>();
        foreach (var group in groups)
        {
            var going = group.Files.Where(f => toRecycle.Contains(f.Path)).ToList();
            if (going.Count == 0)
                continue;
            bool kept = group.Files.Any(f => !toRecycle.Contains(f.Path) && SameLength(f.Path, group.Length));
            if (!kept)
                failures.AddRange(going.Select(f => new DuplicateFailure(f.Path, null)));
            else
                ready.AddRange(going.Select(f => (f.Path, group.Length)));
        }

        int recycled = 0, handled = 0;
        long freed = 0;
        foreach (var batch in ready.Chunk(RecycleBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recycle(batch.Select(b => b.Path).ToList());
            foreach (var (path, length) in batch)
            {
                if (File.Exists(path))
                    failures.Add(new DuplicateFailure(path, null));
                else
                {
                    recycled++;
                    freed += length;
                }
            }
            handled += batch.Length;
            progress?.Report(handled);
        }
        return new DuplicateDeleteReport(recycled, freed, failures);
    }

    private static bool SameLength(string path, long length)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- File identity (hard links)

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    private static (uint, ulong)? FileId(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks < 2)
                return null; // a single name: nothing to merge
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
