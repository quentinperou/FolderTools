namespace FolderTools.Core;

/// <summary>A file kept by <see cref="LargestFiles"/>: its folder in the size tree, its name, size and date.</summary>
public sealed record FileEntry(FolderNode Folder, string Name, long Length, DateTime LastWriteUtc)
{
    public string FullPath => Path.Join(Folder.FullPath, Name);
}

/// <summary>
/// The largest files met during a scan, up to a capacity (the smaller ones are forgotten). Filled from the scan's
/// worker threads: a file under the current smallest one is turned down without taking the lock, which is the case
/// of almost every file once the list is full.
/// </summary>
public sealed class LargestFiles(int capacity = LargestFiles.DefaultCapacity)
{
    public const int DefaultCapacity = 1000;

    private readonly PriorityQueue<FileEntry, long> _heap = new(); // smallest first
    private readonly Lock _lock = new();
    private long _threshold = -1; // size of the smallest kept file once full, -1 before
    private int _version;

    public int Capacity { get; } = capacity;

    /// <summary>Changes on every add or removal: lets a display skip a refresh when nothing moved.</summary>
    public int Version => Volatile.Read(ref _version);

    public int Count
    {
        get
        {
            lock (_lock)
                return _heap.Count;
        }
    }

    public void Offer(FolderNode folder, string name, long length, DateTime lastWriteUtc)
    {
        if (length <= Volatile.Read(ref _threshold))
            return;
        lock (_lock)
        {
            if (_heap.Count >= Capacity && length <= _heap.Peek().Length)
                return;
            _heap.Enqueue(new FileEntry(folder, name, length, lastWriteUtc), length);
            if (_heap.Count > Capacity)
                _heap.Dequeue();
            if (_heap.Count >= Capacity)
                Volatile.Write(ref _threshold, _heap.Peek().Length);
            _version++;
        }
    }

    /// <summary>The kept files, largest first; only those inside <paramref name="folder"/> when given.</summary>
    public IReadOnlyList<FileEntry> Snapshot(FolderNode? folder = null)
    {
        FileEntry[] all;
        lock (_lock)
            all = _heap.UnorderedItems.Select(i => i.Element).ToArray();
        return all
            .Where(f => folder is null || f.Folder.IsSameOrInside(folder))
            .OrderByDescending(f => f.Length)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Forgets a file (deleted by the user).</summary>
    public void Remove(FileEntry file)
    {
        lock (_lock)
        {
            var kept = _heap.UnorderedItems.Where(i => i.Element != file).ToList();
            if (kept.Count == _heap.Count)
                return;
            _heap.Clear();
            foreach (var (element, priority) in kept)
                _heap.Enqueue(element, priority);
            _version++;
        }
    }
}
