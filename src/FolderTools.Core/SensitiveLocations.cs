namespace FolderTools.Core;

/// <summary>
/// Places where removing empty folders may break something: some programs (and Windows itself) expect their
/// empty folders to be there. The cleaning window shows a warning for them; it does not refuse.
/// </summary>
public static class SensitiveLocations
{
    /// <param name="systemFolders">Folders whose whole content is sensitive: Windows, Program Files, ProgramData...</param>
    /// <param name="exactFolders">Folders sensitive themselves but not their subfolders: the users folder, a profile.</param>
    public static bool IsSensitive(string path, IEnumerable<string> systemFolders, IEnumerable<string> exactFolders)
    {
        string full = Normalize(path);
        if (Path.GetPathRoot(full) is { } root && string.Equals(Normalize(root), full, StringComparison.OrdinalIgnoreCase))
            return true; // a whole drive
        if (exactFolders.Any(f => string.Equals(Normalize(f), full, StringComparison.OrdinalIgnoreCase)))
            return true;
        return systemFolders.Any(f => IsSameOrInside(full, f));
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        string p = Normalize(path), f = Normalize(folder);
        return f.Length > 0
            && (string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f.EndsWith('\\') ? f : f + "\\", StringComparison.OrdinalIgnoreCase));
    }

    // "C:\Windows\" and "c:\windows" are the same folder; a drive root keeps its backslash.
    private static string Normalize(string path) =>
        path.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
