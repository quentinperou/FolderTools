using FolderTools.Core;

namespace FolderTools.Tests;

public class EmptyFolderCleanerTests : IDisposable
{
    private static readonly string[] DefaultIgnored = ["Thumbs.db", "desktop.ini", ".DS_Store"];

    private readonly TempTree _tree = new();
    private readonly EmptyFolderCleaner _cleaner = new(DefaultIgnored);

    public void Dispose() => _tree.Dispose();

    private string P(string relative) => Path.Combine(_tree.Root, relative);

    [Fact]
    public void Find_ListsTheOutermostEmptyFolders()
    {
        _tree.Dir(@"Chain\Of\Empty\Folders");
        _tree.Dir("Alone");
        _tree.File(@"Kept\file.txt");
        _tree.Dir(@"Kept\EmptyInside");

        var result = _cleaner.Find(_tree.Root);

        Assert.Equal([P("Alone"), P("Chain"), P(@"Kept\EmptyInside")], result.Folders.Select(f => f.Path));
        Assert.Equal(3, result.Folders.Single(f => f.Path == P("Chain")).NestedCount);
        Assert.Equal(6, result.TotalCount);
        Assert.Equal(0, result.InaccessibleCount);
    }

    [Fact]
    public void Find_NeverListsTheStartingFolder()
    {
        _tree.Dir(@"A\B");

        var result = _cleaner.Find(_tree.Root);

        Assert.Equal([P("A")], result.Folders.Select(f => f.Path));
    }

    [Fact]
    public void Find_IgnoresTheIgnoredFiles()
    {
        _tree.File(@"Thumbs\Thumbs.db", 10);
        _tree.File(@"Thumbs\Sub\DESKTOP.INI", 10); // case does not matter
        _tree.File(@"Real\photo.jpg", 10);

        var result = _cleaner.Find(_tree.Root);

        Assert.Equal([P("Thumbs")], result.Folders.Select(f => f.Path));
    }

    [Fact]
    public void Find_SupportsWildcards()
    {
        var cleaner = new EmptyFolderCleaner(["*.tmp", " ", "~$?"]);
        _tree.File(@"A\x.tmp");
        _tree.File(@"B\~$1");
        _tree.File(@"C\~$12");

        var result = cleaner.Find(_tree.Root);

        Assert.Equal([P("A"), P("B")], result.Folders.Select(f => f.Path));
    }

    [Fact]
    public void Find_TreatsJunctionsAsContent()
    {
        string target = _tree.Dir(@"Target\Empty");
        _tree.Junction(@"Holder\Link", target);

        var result = _cleaner.Find(_tree.Root);

        // "Target" only holds an empty folder; "Holder" holds a link, which is never removed.
        Assert.Equal([P("Target")], result.Folders.Select(f => f.Path));
    }

    private static readonly string[] DefaultIgnoredFolders = [".git", ".svn", ".hg"];

    [Fact]
    public void Find_NeverLooksIntoIgnoredFolders()
    {
        var cleaner = new EmptyFolderCleaner(DefaultIgnored, DefaultIgnoredFolders);
        // A repository whose branches are all packed: refs\heads and refs\tags are empty, and must stay.
        _tree.Dir(@"Repo\.git\refs\heads");
        _tree.Dir(@"Repo\.git\refs\tags");
        _tree.Dir(@"Repo\.git\objects\info");
        _tree.Dir(@"Repo\src\empty");
        _tree.Dir(@"OnlyGit\.GIT"); // case does not matter

        var result = cleaner.Find(_tree.Root);

        Assert.Equal([P(@"Repo\src")], result.Folders.Select(f => f.Path));
        Assert.False(result.InsideIgnoredFolder);
    }

    [Fact]
    public void Find_InsideAnIgnoredFolder_FindsNothing()
    {
        var cleaner = new EmptyFolderCleaner(DefaultIgnored, DefaultIgnoredFolders);
        _tree.Dir(@"Repo\.git\refs\heads");

        var result = cleaner.Find(P(@"Repo\.git\refs"));

        Assert.True(result.InsideIgnoredFolder);
        Assert.Empty(result.Folders);
        Assert.True(Directory.Exists(P(@"Repo\.git\refs\heads")));
    }

    [Fact]
    public void Find_IgnoredFolders_SupportWildcards()
    {
        var cleaner = new EmptyFolderCleaner([], ["keep*"]);
        _tree.Dir(@"A\keep-me");
        _tree.Dir(@"B\other");

        Assert.Equal([P("B")], cleaner.Find(_tree.Root).Folders.Select(f => f.Path));
    }

    [Fact]
    public void Delete_LeavesAFolderThatReceivedAnIgnoredFolder()
    {
        var cleaner = new EmptyFolderCleaner(DefaultIgnored, DefaultIgnoredFolders);
        _tree.Dir(@"A\B");
        var found = cleaner.Find(_tree.Root);
        _tree.Dir(@"A\B\.git");

        var report = cleaner.Delete(found.Folders);

        Assert.Equal(DeleteFailureReason.NoLongerEmpty, Assert.Single(report.Failures).Reason);
        Assert.True(Directory.Exists(P(@"A\B\.git")));
    }

    [Fact]
    public void Find_HiddenFilesAreContent()
    {
        string hidden = _tree.File(@"A\secret.dat");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Empty(_cleaner.Find(_tree.Root).Folders);
    }

    [Fact]
    public void Find_MissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _cleaner.Find(P("nope")));
    }

    [Fact]
    public void Delete_RemovesFoldersAndIgnoredFiles()
    {
        _tree.Dir(@"A\B\C");
        string ini = _tree.File(@"A\B\desktop.ini");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        _tree.File(@"Keep\file.txt");

        var found = _cleaner.Find(_tree.Root);
        var report = _cleaner.Delete(found.Folders);

        Assert.Equal(3, report.DeletedCount);
        Assert.Empty(report.Failures);
        Assert.False(Directory.Exists(P("A")));
        Assert.True(File.Exists(P(@"Keep\file.txt")));
        Assert.True(Directory.Exists(_tree.Root));
    }

    [Fact]
    public void Delete_LeavesAFolderFilledSinceTheSearch()
    {
        _tree.Dir(@"A\B");
        var found = _cleaner.Find(_tree.Root);
        _tree.File(@"A\B\new.txt", 5);

        var report = _cleaner.Delete(found.Folders);

        Assert.Equal(0, report.DeletedCount);
        var failure = Assert.Single(report.Failures);
        Assert.Equal(P("A"), failure.Path);
        Assert.Equal(DeleteFailureReason.NoLongerEmpty, failure.Reason);
        Assert.True(File.Exists(P(@"A\B\new.txt")));
    }

    [Fact]
    public void Delete_SkipsFoldersAlreadyGone()
    {
        _tree.Dir("A");
        var found = _cleaner.Find(_tree.Root);
        Directory.Delete(P("A"));

        var report = _cleaner.Delete(found.Folders);

        Assert.Equal(0, report.DeletedCount);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public void Delete_WithRecycleBin_HandsTheFoldersOver()
    {
        _tree.Dir(@"A\B");
        _tree.Dir("C");
        _tree.Dir("Stuck");
        var found = _cleaner.Find(_tree.Root);
        var recycled = new List<string>();

        var report = _cleaner.Delete(found.Folders, paths =>
        {
            recycled.AddRange(paths);
            foreach (string path in paths.Where(p => !p.EndsWith("Stuck")))
                Directory.Delete(path, recursive: true);
        });

        Assert.Equal([P("A"), P("C"), P("Stuck")], recycled);
        Assert.Equal(3, report.DeletedCount); // A, A\B, C
        Assert.Equal(DeleteFailureReason.InUse, Assert.Single(report.Failures).Reason);
    }

    [Fact]
    public void Delete_WithRecycleBin_ChecksAgainFirst()
    {
        _tree.Dir("A");
        var found = _cleaner.Find(_tree.Root);
        _tree.File(@"A\new.txt");
        var recycled = new List<string>();

        var report = _cleaner.Delete(found.Folders, recycled.AddRange);

        Assert.Empty(recycled);
        Assert.Equal(DeleteFailureReason.NoLongerEmpty, Assert.Single(report.Failures).Reason);
    }

    [Fact]
    public void Delete_ReportsProgress()
    {
        _tree.Dir("A");
        _tree.Dir("B");
        var found = _cleaner.Find(_tree.Root);
        var steps = new List<int>();

        _cleaner.Delete(found.Folders, progress: new SyncProgress<int>(steps.Add));

        Assert.Equal([1, 2], steps);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
