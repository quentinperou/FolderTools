using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace FolderTools.Core;

/// <summary>Reads when a photo or a video was taken, from its own metadata.</summary>
public interface IDateTakenReader
{
    /// <summary>The date taken, in the camera's local time; null when the file does not say.</summary>
    DateTime? Read(string path);
}

/// <summary>
/// EXIF date taken of photos and RAW files (JPEG, HEIC, CR2/CR3, NEF, ARW, DNG…) and creation date of videos
/// (MP4, MOV…), through MetadataExtractor (Apache-2.0): no Windows codec needed.
/// </summary>
public sealed class DateTakenReader : IDateTakenReader
{
    public DateTime? Read(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null; // unknown or damaged format: no date
        }

        // Photos: when the shutter was pressed, in the camera's local time (EXIF has no time zone).
        foreach (var exif in directories.OfType<ExifSubIfdDirectory>())
            if ((Valid(exif, ExifDirectoryBase.TagDateTimeOriginal) ?? Valid(exif, ExifDirectoryBase.TagDateTimeDigitized)) is { } taken)
                return taken;
        foreach (var ifd0 in directories.OfType<ExifIfd0Directory>())
            if (Valid(ifd0, ExifDirectoryBase.TagDateTime) is { } date)
                return date;

        // Videos: the creation date of the movie header, stored in UTC.
        foreach (var movie in directories.OfType<QuickTimeMovieHeaderDirectory>())
            if (Valid(movie, QuickTimeMovieHeaderDirectory.TagCreated) is { } created)
                return DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
        return null;
    }

    // Unset dates come as 0000:00:00 (not parsed) or 1904-01-01 (QuickTime's zero): not dates.
    private static DateTime? Valid(MetadataExtractor.Directory directory, int tag) =>
        directory.TryGetDateTime(tag, out var date) && date.Year > 1970 ? date : null;
}
