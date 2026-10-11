using FolderTools.Core;

namespace FolderTools.Tests;

public class FolderScannerTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Totals_IncludeEverySubfolder()
    {
        _tree.File("root.bin", 100);
        _tree.File(@"A\a1.bin", 1000);
        _tree.File(@"A\a2.bin", 500);
        _tree.File(@"A\Deep\Deeper\d.bin", 2000);
        _tree.File(@"B\b.bin", 10);
        _tree.Dir("Empty");

        var root = new FolderScanner().Scan(_tree.Root);

        Assert.True(root.IsComplete);
        Assert.Equal(3610, root.Size);
        Assert.Equal(5, root.FileCount);
        Assert.Equal(5, root.FolderCount); // A, Deep, Deeper, B, Empty
        Assert.Equal(100, root.OwnFilesSize);
        Assert.Equal(1, root.OwnFileCount);

        var a = root.Children.Single(c => c.Name == "A");
        Assert.Equal(3500, a.Size);
        Assert.Equal(3, a.FileCount);
        Assert.Equal(2, a.FolderCount);
        Assert.Equal(Path.Combine(_tree.Root, "A"), a.FullPath);
        Assert.Equal(3500.0 / 3610, a.ShareOfParent, 6);

        var empty = root.Children.Single(c => c.Name == "Empty");
        Assert.Equal(0, empty.Size);
        Assert.Null(empty.LastModifiedUtc);
        Assert.All(Flatten(root), n => Assert.True(n.IsComplete));
    }

    [Fact]
    public void HiddenAndSystemFiles_AreCounted()
    {
        string hidden = _tree.File(@"A\hidden.bin", 300);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var root = new FolderScanner().Scan(_tree.Root);

        Assert.Equal(300, root.Size);
        Assert.Equal(1, root.FileCount);
    }

    [Fact]
    public void LastModified_IsTheNewestFile()
    {
        string old = _tree.File(@"A\old.bin");
        string recent = _tree.File(@"A\B\recent.bin");
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(recent, new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var root = new FolderScanner().Scan(_tree.Root);

        Assert.Equal(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), root.LastModifiedUtc);
        Assert.Equal(new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), root.Children[0].LastModifiedUtc);
    }

    [Fact]
    public void Junctions_AreListedButNotFollowed()
    {
        string target = _tree.File(@"Real\big.bin", 5000);
        _tree.Junction("Link", Path.GetDirectoryName(target)!);

        var root = new FolderScanner().Scan(_tree.Root);

        Assert.Equal(5000, root.Size); // counted once, where it lives
        var link = root.Children.Single(c => c.Name == "Link");
        Assert.True(link.IsLink);
        Assert.Equal(0, link.Size);
        Assert.True(link.IsComplete);
    }

    [Fact]
    public void Progress_CountsEverything()
    {
        _tree.File(@"A\1.bin", 10);
        _tree.File(@"B\2.bin", 20);

        var scanner = new FolderScanner();
        scanner.Scan(_tree.Root);

        Assert.Equal(2, scanner.FilesScanned);
        Assert.Equal(3, scanner.FoldersScanned);
        Assert.Equal(30, scanner.BytesScanned);
    }

    [Fact]
    public async Task Cancelling_Throws()
    {
        _tree.File(@"A\1.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FolderScanner().ScanAsync(_tree.Root, cts.Token));
    }

    [Fact]
    public void MissingFolder_IsReportedAsInaccessible()
    {
        var root = new FolderScanner().Scan(Path.Combine(_tree.Root, "does-not-exist"));

        Assert.True(root.IsInaccessible);
        Assert.Equal(1, root.InaccessibleCount);
    }

    [Fact]
    public void Detach_SubtractsFromEveryParent()
    {
        _tree.File(@"A\B\b.bin", 400);
        _tree.File(@"A\B\C\c.bin", 100);
        _tree.File(@"A\a.bin", 50);

        var root = new FolderScanner().Scan(_tree.Root);
        var a = root.Children.Single();
        var b = a.Children.Single();
        b.Detach();

        Assert.Empty(a.Children);
        Assert.Equal(50, a.Size);
        Assert.Equal(50, root.Size);
        Assert.Equal(1, root.FileCount);
        Assert.Equal(1, root.FolderCount);
        Assert.Equal(0, a.FolderCount);
        Assert.Null(b.Parent);
    }

    [Fact]
    public void DriveRoot_KeepsItsName()
    {
        Assert.Equal(@"C:\", FolderNode.CreateRoot(@"C:\").Name);
        Assert.Equal("Photos", FolderNode.CreateRoot(@"D:\Photos\").Name);
        Assert.Equal(@"D:\Photos\2025", FolderNode.CreateRoot(@"D:\Photos\").CreateChild("2025").FullPath);
    }

    private static IEnumerable<FolderNode> Flatten(FolderNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
