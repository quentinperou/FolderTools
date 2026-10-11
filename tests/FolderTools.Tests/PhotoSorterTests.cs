using FolderTools.Core;

namespace FolderTools.Tests;

public class PhotoSorterTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeReader _dates = new();

    public void Dispose() => _tree.Dispose();

    private string P(string relative) => Path.Combine(_tree.Root, relative);

    private PhotoSorter Sorter => new(_dates);

    [Theory]
    [InlineData(PhotoLayout.YearDay, @"2025\2025-06-14")]
    [InlineData(PhotoLayout.YearMonth, @"2025\06")]
    [InlineData(PhotoLayout.YearMonthDay, @"2025\06\14")]
    [InlineData(PhotoLayout.Day, "2025-06-14")]
    public void Layouts(PhotoLayout layout, string expected)
    {
        Assert.Equal(expected, PhotoSorter.FolderFor(new DateTime(2025, 6, 14, 14, 2, 0), layout));
    }

    [Fact]
    public void Plan_GroupsARawWithItsJpegAndSidecar_AndLeavesOtherFiles()
    {
        _tree.File("IMG_1001.CR3");
        _tree.File("IMG_1001.JPG");
        _tree.File("IMG_1001.xmp");
        _tree.File("VID_1010.MP4");
        _tree.File("notes.txt");
        _tree.File("lonely.xmp");
        _tree.File(@"Sub\IMG_2000.jpg");
        _dates["IMG_1001.CR3"] = new DateTime(2025, 6, 14, 14, 2, 0);
        _dates["VID_1010.MP4"] = new DateTime(2025, 6, 15, 16, 47, 0);

        var plan = Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.Leave, "No date");

        Assert.Equal(2, plan.Groups.Count);
        var raw = plan.Groups.Single(g => g.Name == "IMG_1001");
        Assert.Equal(PhotoKind.Raw, raw.Kind);
        Assert.Equal(3, raw.Files.Count);
        Assert.Equal(@"2025\2025-06-14", raw.Folder);
        Assert.Equal(@"2025\2025-06-15", plan.Groups.Single(g => g.Name == "VID_1010").Folder);
        Assert.Equal(4, plan.FileCount);
        Assert.Equal(2, plan.FolderCount);
    }

    [Fact]
    public void Plan_GroupDate_ComesFromAnyFileThatHasOne()
    {
        _tree.File("IMG_1.CR3");
        _tree.File("IMG_1.JPG");
        _dates["IMG_1.JPG"] = new DateTime(2024, 12, 24);

        var group = Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.Leave, "No date").Groups.Single();

        Assert.Equal(DateSource.Taken, group.Source);
        Assert.Equal(@"2024\2024-12-24", group.Folder);
    }

    [Theory]
    [InlineData(UndatedPolicy.FileDate, @"2023\2023-03-05")]
    [InlineData(UndatedPolicy.NoDateFolder, "No date")]
    [InlineData(UndatedPolicy.Leave, null)]
    public void Plan_Undated(UndatedPolicy policy, string? expected)
    {
        string shot = _tree.File("Screenshot.png");
        File.SetLastWriteTime(shot, new DateTime(2023, 3, 5, 10, 0, 0));

        var group = Sorter.Plan(_tree.Root, PhotoLayout.YearDay, policy, "No date").Groups.Single();

        Assert.Equal(expected, group.Folder);
        Assert.Equal(policy == UndatedPolicy.FileDate ? DateSource.FileDate : DateSource.None, group.Source);
    }

    [Fact]
    public void Execute_MovesAndUndoPutsBack()
    {
        _tree.File("IMG_1001.CR3", 10);
        _tree.File("IMG_1001.JPG", 5);
        _tree.File("notes.txt");
        _dates["IMG_1001.CR3"] = new DateTime(2025, 6, 14);
        var plan = Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.Leave, "No date");

        var result = Sorter.Execute(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Moves.Count);
        Assert.True(File.Exists(P(@"2025\2025-06-14\IMG_1001.CR3")));
        Assert.True(File.Exists(P(@"2025\2025-06-14\IMG_1001.JPG")));
        Assert.False(File.Exists(P("IMG_1001.CR3")));
        Assert.True(File.Exists(P("notes.txt")));
        Assert.Equal([P("2025"), P(@"2025\2025-06-14")], result.CreatedFolders);
        Assert.Equal(1, result.FolderCount);

        var failures = PhotoSorter.Undo(result);

        Assert.Empty(failures);
        Assert.True(File.Exists(P("IMG_1001.CR3")));
        Assert.True(File.Exists(P("IMG_1001.JPG")));
        Assert.False(Directory.Exists(P("2025")));
    }

    [Fact]
    public void Execute_NeverOverwrites_AndKeepsAGroupTogether()
    {
        _tree.File(@"2025\2025-06-14\IMG_1001.JPG", 99);
        _tree.File("IMG_1001.CR3", 10);
        _tree.File("IMG_1001.JPG", 5);
        _dates["IMG_1001.CR3"] = new DateTime(2025, 6, 14);

        var result = Sorter.Execute(Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.Leave, "No date"));

        Assert.Empty(result.Failures);
        Assert.Equal(99, new FileInfo(P(@"2025\2025-06-14\IMG_1001.JPG")).Length);
        Assert.True(File.Exists(P(@"2025\2025-06-14\IMG_1001 (2).CR3")));
        Assert.True(File.Exists(P(@"2025\2025-06-14\IMG_1001 (2).JPG")));
        Assert.Empty(result.CreatedFolders);
    }

    [Fact]
    public void Undo_KeepsFoldersThatReceivedOtherFiles()
    {
        _tree.File("A.jpg");
        _dates["A.jpg"] = new DateTime(2025, 1, 2);
        var result = Sorter.Execute(Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.Leave, "No date"));
        _tree.File(@"2025\later.txt");

        PhotoSorter.Undo(result);

        Assert.True(File.Exists(P("A.jpg")));
        Assert.False(Directory.Exists(P(@"2025\2025-01-02")));
        Assert.True(File.Exists(P(@"2025\later.txt")));
    }

    [Fact]
    public void HiddenFilesAreLeftAlone()
    {
        string hidden = _tree.File("hidden.jpg");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _dates["hidden.jpg"] = new DateTime(2025, 1, 2);

        Assert.Empty(Sorter.Plan(_tree.Root, PhotoLayout.YearDay, UndatedPolicy.FileDate, "No date").Groups);
    }

    [Fact]
    public void RealReader_ReadsTheExifDateTaken()
    {
        string photo = _tree.File("IMG_0001.JPG");
        File.WriteAllBytes(photo, JpegWithDateTaken("2025:06:14 14:02:00"));

        Assert.Equal(new DateTime(2025, 6, 14, 14, 2, 0), new DateTakenReader().Read(photo));
    }

    // The smallest JPEG MetadataExtractor reads: SOI, an APP1 "Exif" segment (TIFF header, IFD0 pointing at the Exif
    // IFD, DateTimeOriginal), EOI. No image data: only the metadata is read.
    private static byte[] JpegWithDateTaken(string date)
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            tiff.AddRange(BitConverter.GetBytes(tag));
            tiff.AddRange(BitConverter.GetBytes(type));
            tiff.AddRange(BitConverter.GetBytes(count));
            tiff.AddRange(BitConverter.GetBytes(value));
        }
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        Entry(0x8769, 4, 1, 26); // Exif IFD at 26
        tiff.AddRange(BitConverter.GetBytes(0u));
        tiff.AddRange(BitConverter.GetBytes((ushort)1));
        Entry(0x9003, 2, 20, 44); // DateTimeOriginal, 20 ASCII bytes at 44
        tiff.AddRange(BitConverter.GetBytes(0u));
        tiff.AddRange(System.Text.Encoding.ASCII.GetBytes(date + "\0"));

        var exif = System.Text.Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        int length = exif.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif, 0xFF, 0xD9];
    }

    [Fact]
    public void RealReader_GivesNoDateForAFileWithoutMetadata()
    {
        string text = _tree.File("fake.jpg", 10);
        Assert.Null(new DateTakenReader().Read(text));
    }

    private sealed class FakeReader : Dictionary<string, DateTime>, IDateTakenReader
    {
        public FakeReader() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public DateTime? Read(string path) => TryGetValue(Path.GetFileName(path), out var date) ? date : null;
    }
}
