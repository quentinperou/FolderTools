using System.Diagnostics;

namespace FolderTools.Tests;

/// <summary>A folder tree in the temp folder for one test, deleted afterwards.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("foldertools-tests-").FullName;

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string File(string relative, int size = 0)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// <summary>A directory junction (no administrator rights needed, unlike symbolic links).</summary>
    public string Junction(string relative, string target)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    public void Dispose()
    {
        try
        {
            // Junctions first: deleting the tree must not walk into their target.
            foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories("*", SearchOption.AllDirectories)
                         .Where(d => d.LinkTarget is not null).ToList())
                dir.Delete();
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
