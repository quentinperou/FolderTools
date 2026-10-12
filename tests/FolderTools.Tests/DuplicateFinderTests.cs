using System.Diagnostics;
using FolderTools.Core;

namespace FolderTools.Tests;

public class DuplicateFinderTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string P(string relative) => Path.Combine(_tree.Root, relative);

    private string Write(string relative, byte[] content, DateTime? modified = null)
    {
        string path = _tree.File(relative);
        File.WriteAllBytes(path, content);
        if (modified is { } date)
            File.SetLastWriteTime(path, date);
        return path;
    }

    private string Write(string relative, string content, DateTime? modified = null) =>
        Write(relative, System.Text.Encoding.UTF8.GetBytes(content), modified);

    private static DuplicateFinder Finder => new(new EmptyFolderCleaner(["Thumbs.db"], [".git"]));

    [Fact]
    public void FindsFilesWithTheSameContent_WhateverTheirName()
    {
        Write(@"2024\VID_0412.mp4", "video");
        Write(@"Import\VID_0412.mp4", "video");
        Write(@"To sort\Holidays (copy).mp4", "video");
        Write(@"a.txt", "other");
        Write(@"b.txt", "OTHER"); // same size, other content

        var search = Finder.Find(_tree.Root);

        var group = Assert.Single(search.Groups);
        Assert.Equal(3, group.Files.Count);
        Assert.Equal(5, group.Length);
        Assert.Equal(10, group.Wasted);
        Assert.Equal(2, search.ExtraCopies);
        Assert.Equal(5, search.FileCount);
    }

    [Fact]
    public void LargeFiles_DifferingAfterTheirBeginning_AreNotDuplicates()
    {
        var a = new byte[200_000];
        var b = new byte[200_000];
        b[150_000] = 1;
        Write("a.bin", a);
        Write("b.bin", b);
        Write(@"Copy\a.bin", a);

        var group = Assert.Single(Finder.Find(_tree.Root).Groups);
        Assert.Equal([P("a.bin"), P(@"Copy\a.bin")], group.Files.Select(f => f.Path));
    }

    [Fact]
    public void LeavesOut_EmptyHiddenIgnoredFilesAndFolders()
    {
        _tree.File("empty1.txt");
        _tree.File("empty2.txt");
        string hidden = Write("hidden.txt", "same");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Write("visible.txt", "same");
        Write(@"Repo\.git\objects\x", "same");
        Write("Thumbs.db", "same");

        var search = Finder.Find(_tree.Root);

        Assert.Empty(search.Groups);
        Assert.Equal(1, search.FileCount);
    }

    [Fact]
    public void HardLinks_AreOneFile()
    {
        string original = Write("original.dat", "content");
        string link = P("link.dat");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{original}\"")
               {
                   CreateNoWindow = true,
                   UseShellExecute = false,
                   RedirectStandardOutput = true,
               })!)
            process.WaitForExit();
        Assert.True(File.Exists(link));

        Assert.Empty(Finder.Find(_tree.Root).Groups);

        Write("copy.dat", "content");
        var group = Assert.Single(Finder.Find(_tree.Root).Groups);
        Assert.Equal(2, group.Files.Count); // one name of the linked file, and the real copy
    }

    [Fact]
    public void Keeper_ByDate_ThenShortestPath()
    {
        var old = new DateTime(2024, 7, 12);
        Write(@"2024\Summer\VID.mp4", "v", old);
        Write(@"Import\VID.mp4", "v", new DateTime(2024, 9, 2));
        Write(@"A\VID.mp4", "v", old);
        var group = Finder.Find(_tree.Root).Groups.Single();

        Assert.Equal(P(@"A\VID.mp4"), FolderKeeper(group, DuplicateKeep.Oldest));
        Assert.Equal(P(@"Import\VID.mp4"), FolderKeeper(group, DuplicateKeep.Newest));
        Assert.Equal(P(@"A\VID.mp4"), FolderKeeper(group, DuplicateKeep.ShortestPath));
    }

    private static string FolderKeeper(DuplicateGroup group, DuplicateKeep keep) => DuplicateFinder.Keeper(group, keep).Path;

    [Fact]
    public void Recycle_RemovesTheCheckedCopies()
    {
        Write("a.txt", "same");
        Write(@"Copy\a.txt", "same");
        var search = Finder.Find(_tree.Root);
        var recycled = new List<string>();

        var report = DuplicateFinder.Recycle(search.Groups, new HashSet<string> { P(@"Copy\a.txt") }, paths =>
        {
            recycled.AddRange(paths);
            foreach (string path in paths)
                File.Delete(path);
        });

        Assert.Equal([P(@"Copy\a.txt")], recycled);
        Assert.Equal(1, report.RecycledCount);
        Assert.Equal(4, report.Freed);
        Assert.Empty(report.Failures);
        Assert.True(File.Exists(P("a.txt")));
    }

    [Fact]
    public void Recycle_NeverRemovesTheLastCopy()
    {
        Write("a.txt", "same");
        Write(@"Copy\a.txt", "same");
        var search = Finder.Find(_tree.Root);
        File.Delete(P("a.txt")); // the kept copy disappeared since the search

        var report = DuplicateFinder.Recycle(search.Groups, new HashSet<string> { P(@"Copy\a.txt") },
            _ => throw new InvalidOperationException("nothing may be recycled"));

        Assert.Equal(0, report.RecycledCount);
        Assert.Single(report.Failures);
        Assert.True(File.Exists(P(@"Copy\a.txt")));
    }
}
