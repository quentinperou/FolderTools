using System.Globalization;

namespace FolderTools.Core;

/// <summary>How the date folders are laid out.</summary>
public enum PhotoLayout
{
    /// <summary><c>2025\2025-06-14</c> (default).</summary>
    YearDay,
    /// <summary><c>2025\06</c>.</summary>
    YearMonth,
    /// <summary><c>2025\06\14</c>.</summary>
    YearMonthDay,
    /// <summary><c>2025-06-14</c>.</summary>
    Day,
}

/// <summary>What happens to files whose metadata has no date taken (screenshots, downloaded pictures…).</summary>
public enum UndatedPolicy
{
    /// <summary>Sorted with the others, by the date the file was last modified.</summary>
    FileDate,
    /// <summary>Moved to a folder of their own, to sort by hand.</summary>
    NoDateFolder,
    /// <summary>Left where they are.</summary>
    Leave,
}

public enum DateSource
{
    Taken,
    FileDate,
    None,
}

public enum PhotoKind
{
    Photo,
    Raw,
    Video,
}

/// <summary>
/// Files that move together: a photo (or RAW, or video) and the files of the same name that go with it — the JPEG
/// of a RAW, its <c>.xmp</c> sidecar…
/// </summary>
/// <param name="Folder">The destination, relative to the sorted folder; null when the files stay where they are.</param>
public sealed record PhotoGroup(string Name, IReadOnlyList<string> Files, PhotoKind Kind, DateTime? Date, DateSource Source, string? Folder);

public sealed record PhotoSortPlan(string Root, IReadOnlyList<PhotoGroup> Groups)
{
    /// <summary>The groups that move.</summary>
    public IEnumerable<PhotoGroup> Moving => Groups.Where(g => g.Folder is not null);

    public int FileCount => Moving.Sum(g => g.Files.Count);

    public int FolderCount => Moving.Select(g => g.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed record PhotoMove(string From, string To);

public sealed record PhotoSortFailure(string Path, string Reason);

/// <param name="CreatedFolders">The folders the sort created, outermost first (an undo removes those left empty).</param>
public sealed record PhotoSortResult(IReadOnlyList<PhotoMove> Moves, IReadOnlyList<PhotoSortFailure> Failures,
    IReadOnlyList<string> CreatedFolders)
{
    public int FolderCount => Moves.Select(m => Path.GetDirectoryName(m.To)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>
/// Sorts the photos, RAW files and videos sitting directly in a folder into date folders inside it, from their date
/// taken. Other files and the existing subfolders never move; a file is never overwritten (a name already taken in
/// the destination gets “ (2)”); the whole sort can be undone.
/// </summary>
public sealed class PhotoSorter(IDateTakenReader reader)
{
    public static readonly IReadOnlySet<string> PhotoExtensions = Set(".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".png", ".tif", ".tiff", ".webp", ".gif", ".bmp", ".avif");
    public static readonly IReadOnlySet<string> RawExtensions = Set(".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl", ".raw");
    public static readonly IReadOnlySet<string> VideoExtensions = Set(".mp4", ".mov", ".m4v", ".avi", ".mts", ".m2ts", ".3gp", ".mkv", ".wmv", ".mpg", ".mpeg");

    // Files that only go with a photo of the same name: edits, thumbnails, Apple's edit lists.
    public static readonly IReadOnlySet<string> SidecarExtensions = Set(".xmp", ".aae", ".thm", ".pp3", ".dop");

    private static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Looks at the files of <paramref name="root"/> (not its subfolders) and decides where each group goes.
    /// <paramref name="noDateFolder"/> is the name of the folder of files without a date (localized by the caller).
    /// </summary>
    public PhotoSortPlan Plan(string root, PhotoLayout layout, UndatedPolicy undated, string noDateFolder,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = System.IO.Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, // desktop.ini, Thumbs.db…
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
        }).ToList();

        var groups = new List<PhotoGroup>();
        int done = 0;
        foreach (var byName in files.GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var media = byName.Where(f => KindOf(f) is not null).ToList();
            if (media.Count == 0)
                continue; // sidecars without their photo, and every other file, stay

            // The date of the group: the first of its files whose metadata has one (RAW, then photo, then video).
            var ordered = media.OrderBy(f => KindOf(f) switch { PhotoKind.Raw => 0, PhotoKind.Photo => 1, _ => 2 }).ToList();
            DateTime? date = null;
            foreach (string file in ordered)
                if ((date = reader.Read(file)) is not null)
                    break;
            var source = date is not null ? DateSource.Taken : DateSource.None;
            if (date is null && undated == UndatedPolicy.FileDate)
            {
                date = ordered.Min(File.GetLastWriteTime);
                source = DateSource.FileDate;
            }

            string? folder = date is { } d ? FolderFor(d, layout)
                : undated == UndatedPolicy.NoDateFolder ? noDateFolder
                : null;
            var members = byName.Where(f => KindOf(f) is not null || SidecarExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            groups.Add(new PhotoGroup(byName.Key, members, KindOf(ordered[0])!.Value, date, source, folder));

            done += members.Count;
            progress?.Report(done);
        }
        return new PhotoSortPlan(root, groups.OrderBy(g => g.Folder ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Date).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public static PhotoKind? KindOf(string path)
    {
        string extension = Path.GetExtension(path);
        return RawExtensions.Contains(extension) ? PhotoKind.Raw
            : PhotoExtensions.Contains(extension) ? PhotoKind.Photo
            : VideoExtensions.Contains(extension) ? PhotoKind.Video
            : null;
    }

    // Gregorian digits whatever the user's calendar.
    public static string FolderFor(DateTime date, PhotoLayout layout) => layout switch
    {
        PhotoLayout.YearMonth => Path.Join(date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("MM", CultureInfo.InvariantCulture)),
        PhotoLayout.YearMonthDay => Path.Join(date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("MM", CultureInfo.InvariantCulture), date.ToString("dd", CultureInfo.InvariantCulture)),
        PhotoLayout.Day => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Path.Join(date.ToString("yyyy", CultureInfo.InvariantCulture), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
    };

    /// <summary>Moves the groups of the plan. A group whose files changed meanwhile is moved as far as possible.</summary>
    public PhotoSortResult Execute(PhotoSortPlan plan, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var moves = new List<PhotoMove>();
        var failures = new List<PhotoSortFailure>();
        var created = new List<string>();
        int done = 0;

        foreach (var group in plan.Moving)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = Path.Join(plan.Root, group.Folder);
            try
            {
                CreateFolder(folder, plan.Root, created);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.AddRange(group.Files.Select(f => new PhotoSortFailure(f, ex.Message)));
                continue;
            }

            // The files of a group keep the same name: with a clash, they all get the same " (n)".
            string suffix = FreeSuffix(folder, group.Files);
            foreach (string file in group.Files)
            {
                string target = Path.Join(folder, Path.GetFileNameWithoutExtension(file) + suffix + Path.GetExtension(file));
                try
                {
                    File.Move(file, target, overwrite: false);
                    moves.Add(new PhotoMove(file, target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new PhotoSortFailure(file, ex.Message));
                }
                progress?.Report(++done);
            }
        }
        return new PhotoSortResult(moves, failures, created);
    }

    /// <summary>
    /// Puts every moved file back (with “ (2)” if its old name was taken meanwhile), then removes the folders the
    /// sort created, when they are empty again.
    /// </summary>
    public static IReadOnlyList<PhotoSortFailure> Undo(PhotoSortResult result)
    {
        var failures = new List<PhotoSortFailure>();
        foreach (var move in result.Moves.Reverse())
        {
            try
            {
                string back = move.From;
                if (File.Exists(back))
                {
                    string folder = Path.GetDirectoryName(back)!;
                    back = Path.Join(folder, Path.GetFileNameWithoutExtension(back) + FreeSuffix(folder, [back]) + Path.GetExtension(back));
                }
                File.Move(move.To, back, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new PhotoSortFailure(move.To, ex.Message));
            }
        }
        // Innermost first: a year folder empties once its day folders are gone.
        foreach (string folder in result.CreatedFolders.Reverse())
        {
            try
            {
                if (System.IO.Directory.Exists(folder) && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
                    System.IO.Directory.Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return failures;
    }

    // Creates the folder and its missing parents under the root, noting those created (outermost first).
    private static void CreateFolder(string folder, string root, List<string> created)
    {
        var missing = new Stack<string>();
        for (string? f = folder; f is not null && !System.IO.Directory.Exists(f) && f.Length > root.Length; f = Path.GetDirectoryName(f))
            missing.Push(f);
        while (missing.Count > 0)
        {
            string f = missing.Pop();
            System.IO.Directory.CreateDirectory(f);
            created.Add(f);
        }
    }

    // "", or " (2)", " (3)"… so that none of the files' names is taken in the folder.
    private static string FreeSuffix(string folder, IEnumerable<string> files)
    {
        var names = files.Select(f => (Path.GetFileNameWithoutExtension(f), Path.GetExtension(f))).ToList();
        for (int n = 1; ; n++)
        {
            string suffix = n == 1 ? "" : $" ({n})";
            if (names.All(x => !File.Exists(Path.Join(folder, x.Item1 + suffix + x.Item2))))
                return suffix;
        }
    }
}
