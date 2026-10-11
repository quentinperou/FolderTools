using System.Globalization;

namespace FolderTools.Core;

/// <summary>Sizes as File Explorer shows them: powers of 1024, three significant digits ("48.2 GB", "48,2 Go").</summary>
public static class SizeFormat
{
    /// <param name="units">Unit names from bytes up: B, KB, MB, GB, TB, PB (localized by the caller).</param>
    public static string Format(long bytes, IReadOnlyList<string> units, CultureInfo culture)
    {
        if (bytes < 1024)
            return $"{bytes.ToString("N0", culture)} {units[0]}";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Count - 1)
        {
            value /= 1024;
            unit++;
        }
        // 999.6 MB would round to "1000 MB": show "0.98 GB" instead, like Explorer.
        if (value >= 999.5 && unit < units.Count - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        return $"{value.ToString(format, culture)} {units[unit]}";
    }
}
