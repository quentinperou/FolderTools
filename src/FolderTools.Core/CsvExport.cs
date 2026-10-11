using System.Globalization;
using System.Text;

namespace FolderTools.Core;

/// <summary>
/// Writes a size analysis as CSV, one line per folder (biggest first among siblings), for Excel: the list
/// separator and decimal mark of the culture ("," and "." in English, ";" and "," in French), UTF-8 with BOM.
/// </summary>
public static class CsvExport
{
    /// <param name="headers">Column titles, localized by the caller: path, name, level, size (bytes), size (GB),
    /// files, folders, % of parent, newest file.</param>
    public static void Write(FolderNode root, TextWriter writer, IReadOnlyList<string> headers, CultureInfo culture)
    {
        if (headers.Count != 9)
            throw new ArgumentException("Nine column titles are expected.", nameof(headers));

        string separator = culture.TextInfo.ListSeparator;
        writer.WriteLine(string.Join(separator, headers.Select(h => Quote(h, separator))));

        var stack = new Stack<FolderNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            string[] fields =
            [
                node.FullPath,
                node.Name,
                (node.Depth - root.Depth).ToString(culture),
                node.Size.ToString(culture),
                (node.Size / (1024.0 * 1024 * 1024)).ToString("0.000", culture),
                node.FileCount.ToString(culture),
                node.FolderCount.ToString(culture),
                (node == root ? 100 : node.ShareOfParent * 100).ToString("0.0", culture),
                node.LastModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
            ];
            writer.WriteLine(string.Join(separator, fields.Select(f => Quote(f, separator))));

            // Pushed smallest first, so that the biggest comes out first.
            foreach (var child in node.Children.OrderBy(c => c.Size))
                stack.Push(child);
        }
    }

    public static void WriteFile(FolderNode root, string path, IReadOnlyList<string> headers, CultureInfo culture)
    {
        // The BOM tells Excel the file is UTF-8 (accents in folder names).
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(root, writer, headers, culture);
    }

    private static string Quote(string field, string separator) =>
        field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
}
