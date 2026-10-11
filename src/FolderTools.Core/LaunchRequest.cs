namespace FolderTools.Core;

public enum LaunchAction
{
    /// <summary>No folder: the home window.</summary>
    Home,
    Size,
    Clean,
}

/// <summary>
/// What the command line asks for: <c>FolderTools.exe size "D:\Photos"</c>, <c>clean "%TEMP%"</c>, or nothing.
/// The context menu (classic entries and the Windows 11 handler) and the "run as administrator" relaunch use it.
/// </summary>
public sealed record LaunchRequest(LaunchAction Action, string? Path)
{
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new LaunchRequest(LaunchAction.Home, null);

        var action = args[0].TrimStart('-', '/').ToLowerInvariant() switch
        {
            "size" => LaunchAction.Size,
            "clean" => LaunchAction.Clean,
            _ => LaunchAction.Home,
        };
        // Without a known verb, the first argument is a folder to open in the home window.
        string? path = action == LaunchAction.Home ? args[0] : args.Count > 1 ? args[1] : null;
        path = NormalizePath(path);
        return new LaunchRequest(path is null ? LaunchAction.Home : action, path);
    }

    /// <summary>Arguments that ask for the same thing again (to restart as administrator).</summary>
    public string ToArguments() => Action switch
    {
        LaunchAction.Size => $"size {QuotePath(Path!)}",
        LaunchAction.Clean => $"clean {QuotePath(Path!)}",
        _ => Path is null ? "" : QuotePath(Path),
    };

    /// <summary>
    /// A drive root reaches us as <c>C:"</c>: in <c>"C:\"</c> the command line rules read <c>\"</c> as an
    /// escaped quote. The classic menu commands cannot avoid it, so it is repaired here.
    /// </summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':')
            path += '\\';
        // "%TEMP%" typed or pasted in the home window; a real folder name may contain a "%" elsewhere.
        return path.StartsWith('%') ? Environment.ExpandEnvironmentVariables(path) : path;
    }

    // A path ending with a backslash gets a second one, so that the closing quote is not escaped.
    public static string QuotePath(string path) => path.EndsWith('\\') ? $"\"{path}\\\"" : $"\"{path}\"";
}
