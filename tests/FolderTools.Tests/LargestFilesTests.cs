using System.Globalization;
using FolderTools.Core;

namespace FolderTools.Tests;

public class LargestFilesTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Scan_KeepsTheLargestFiles_LargestFirst()
    {
        _tree.File("small.bin", 10);
        _tree.File(@"A\big.bin", 5000);
        _tree.File(@"A\B\medium.bin", 300);
        _tree.File(@"C\tiny.bin", 1);

        var scanner = new FolderScanner(largestFiles: 3);
        var root = scanner.Scan(_tree.Root);
        var files = scanner.LargestFiles.Snapshot();

        Assert.Equal(["big.bin", "medium.bin", "small.bin"], files.Select(f => f.Name));
        Assert.Equal(Path.Combine(_tree.Root, @"A\B\medium.bin"), files[1].FullPath);
        Assert.Equal(3, scanner.LargestFiles.Count);
        Assert.Equal(root, files[2].Folder);
    }

    [Fact]
    public void Snapshot_OfAFolder_OnlyHasItsFiles()
    {
        _tree.File(@"A\a.bin", 100);
        _tree.File(@"A\B\b.bin", 50);
        _tree.File(@"C\c.bin", 200);

        var scanner = new FolderScanner();
        var root = scanner.Scan(_tree.Root);
        var a = root.Children.Single(c => c.Name == "A");

        Assert.Equal(["a.bin", "b.bin"], scanner.LargestFiles.Snapshot(a).Select(f => f.Name));
    }

    [Fact]
    public void ManyThreads_KeepExactlyTheLargest()
    {
        var root = FolderNode.CreateRoot(@"X:\");
        var files = new LargestFiles(100);
        Parallel.For(0, 10_000, i => files.Offer(root, $"f{i}", i, DateTime.UtcNow));

        var kept = files.Snapshot();
        Assert.Equal(100, kept.Count);
        Assert.Equal(9_999, kept[0].Length);
        Assert.Equal(9_900, kept[^1].Length);
    }

    [Fact]
    public void Remove_ForgetsTheFile()
    {
        var root = FolderNode.CreateRoot(@"X:\");
        var files = new LargestFiles();
        files.Offer(root, "a", 10, DateTime.UtcNow);
        files.Offer(root, "b", 20, DateTime.UtcNow);
        int version = files.Version;

        files.Remove(files.Snapshot()[0]);

        Assert.Equal(["a"], files.Snapshot().Select(f => f.Name));
        Assert.NotEqual(version, files.Version);
    }

    [Fact]
    public void RemoveFile_SubtractsFromEveryParent()
    {
        _tree.File(@"A\B\b.bin", 400);
        _tree.File(@"A\a.bin", 50);

        var root = new FolderScanner().Scan(_tree.Root);
        var b = root.Children.Single().Children.Single();
        b.RemoveFile(400);

        Assert.Equal(0, b.Size);
        Assert.Equal(0, b.OwnFileCount);
        Assert.Equal(50, root.Size);
        Assert.Equal(1, root.FileCount);
    }

    [Fact]
    public void CsvExport_WritesTheFiles()
    {
        _tree.File(@"Videos\clip.mp4", 300);
        _tree.File("root.iso", 100);
        var scanner = new FolderScanner();
        var root = scanner.Scan(_tree.Root);
        var writer = new StringWriter();

        CsvExport.WriteFiles(scanner.LargestFiles.Snapshot(), root, writer,
            ["Path", "Name", "Folder", "Size", "GB", "%", "Modified"], CultureInfo.GetCultureInfo("en-US"));
        string[] lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        Assert.Equal("Path,Name,Folder,Size,GB,%,Modified", lines[0]);
        Assert.StartsWith($"{Path.Combine(_tree.Root, @"Videos\clip.mp4")},clip.mp4,Videos,300,0.000,75.0,", lines[1]);
        Assert.StartsWith($"{Path.Combine(_tree.Root, "root.iso")},root.iso,.,100,0.000,25.0,", lines[2]);
    }
}
