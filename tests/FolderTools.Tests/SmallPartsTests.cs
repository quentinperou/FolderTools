using System.Globalization;
using FolderTools.Core;

namespace FolderTools.Tests;

public class SizeFormatTests
{
    private static readonly string[] English = ["bytes", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] French = ["octets", "Ko", "Mo", "Go", "To", "Po"];

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(15 * 1024, "15.0 KB")]
    [InlineData(150 * 1024, "150 KB")]
    [InlineData(1023 * 1024, "1.00 MB")]
    [InlineData(51_758_998_323, "48.2 GB")]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024, "5.00 TB")]
    public void English_Sizes(long bytes, string expected)
    {
        Assert.Equal(expected, SizeFormat.Format(bytes, English, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void French_UsesCommaAndFrenchUnits()
    {
        Assert.Equal("48,2 Go", SizeFormat.Format(51_758_998_323, French, CultureInfo.GetCultureInfo("fr-FR")));
    }
}

public class LaunchRequestTests
{
    [Fact]
    public void NoArgument_OpensHome()
    {
        Assert.Equal(new LaunchRequest(LaunchAction.Home, null), LaunchRequest.Parse([]));
    }

    [Theory]
    [InlineData("size", LaunchAction.Size)]
    [InlineData("--clean", LaunchAction.Clean)]
    [InlineData("SIZE", LaunchAction.Size)]
    public void Verbs(string verb, LaunchAction action)
    {
        Assert.Equal(new LaunchRequest(action, @"D:\Photos"), LaunchRequest.Parse([verb, @"D:\Photos"]));
    }

    [Fact]
    public void DriveRootWithEscapedQuote_IsRepaired()
    {
        // "C:\" on the command line arrives as C:" (backslash-quote is an escaped quote).
        Assert.Equal(@"C:\", LaunchRequest.Parse(["size", "C:\""]).Path);
        Assert.Equal(@"C:\", LaunchRequest.Parse(["size", "C:"]).Path);
    }

    [Fact]
    public void VerbWithoutFolder_OpensHome()
    {
        Assert.Equal(LaunchAction.Home, LaunchRequest.Parse(["size"]).Action);
    }

    [Fact]
    public void FolderAlone_OpensHomeOnIt()
    {
        Assert.Equal(new LaunchRequest(LaunchAction.Home, @"D:\Photos"), LaunchRequest.Parse([@"D:\Photos"]));
    }

    [Fact]
    public void EnvironmentVariables_AreExpandedAtTheStartOnly()
    {
        Assert.Equal(Environment.GetEnvironmentVariable("TEMP"), LaunchRequest.Parse(["clean", "%TEMP%"]).Path);
        Assert.Equal(@"D:\100%TEMP%", LaunchRequest.Parse(["clean", @"D:\100%TEMP%"]).Path);
    }

    [Fact]
    public void ToArguments_RoundTrips_EvenForADriveRoot()
    {
        var request = new LaunchRequest(LaunchAction.Size, @"C:\");
        Assert.Equal("size \"C:\\\\\"", request.ToArguments());
        Assert.Equal("clean \"D:\\My folder\"", new LaunchRequest(LaunchAction.Clean, @"D:\My folder").ToArguments());
    }
}

public class SensitiveLocationsTests
{
    private static readonly string[] System = [@"C:\Windows", @"C:\Program Files\"];
    private static readonly string[] Exact = [@"C:\Users", @"C:\Users\Quentin"];

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"c:\windows\System32\", true)]
    [InlineData(@"C:\Program Files\App", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\Quentin\", true)]
    [InlineData(@"C:\Users\Quentin\AppData\Local\Temp", false)]
    [InlineData(@"C:\WindowsApps", false)]
    [InlineData(@"D:\Photos", false)]
    public void IsSensitive(string path, bool expected)
    {
        Assert.Equal(expected, SensitiveLocations.IsSensitive(path, System, Exact));
    }
}

public class CsvExportTests : IDisposable
{
    private static readonly string[] Headers = ["Path", "Name", "Level", "Size", "GB", "Files", "Folders", "%", "Modified"];

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void English_CommaSeparated_BiggestFirst()
    {
        _tree.File(@"Small\s.bin", 100);
        _tree.File(@"Big;Name\b.bin", 300);
        var root = new FolderScanner().Scan(_tree.Root);
        var writer = new StringWriter();

        CsvExport.Write(root, writer, Headers, CultureInfo.GetCultureInfo("en-US"));
        string[] lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        Assert.Equal("Path,Name,Level,Size,GB,Files,Folders,%,Modified", lines[0]);
        Assert.StartsWith($"{_tree.Root},{Path.GetFileName(_tree.Root)},0,400,0.000,2,2,100.0,", lines[1]);
        Assert.StartsWith($"{Path.Combine(_tree.Root, "Big;Name")},Big;Name,1,300,0.000,1,0,75.0,", lines[2]);
        Assert.StartsWith($"{Path.Combine(_tree.Root, "Small")},Small,1,100,", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void French_SemicolonSeparated_QuotesWhenNeeded()
    {
        _tree.File(@"Big;Name\b.bin", 300);
        var root = new FolderScanner().Scan(_tree.Root);
        var writer = new StringWriter();

        CsvExport.Write(root, writer, Headers, CultureInfo.GetCultureInfo("fr-FR"));
        string[] lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        Assert.Equal("Path;Name;Level;Size;GB;Files;Folders;%;Modified", lines[0]);
        Assert.StartsWith($"\"{Path.Combine(_tree.Root, "Big;Name")}\";\"Big;Name\";1;300;0,000;1;0;100,0;", lines[2]);
    }
}
