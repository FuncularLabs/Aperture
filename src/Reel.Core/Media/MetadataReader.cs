using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;

namespace Reel.Core.Media;

/// <summary>EXIF fields Reel cares about, extracted with MetadataExtractor.</summary>
public readonly record struct ImageMetadata(DateTime? TakenLocal, string? Camera, int? Orientation);

/// <summary>
/// Reads EXIF capture date, camera model and orientation. Never throws for a
/// bad/absent-metadata file — returns empty fields instead so indexing continues.
/// </summary>
public sealed class MetadataReader
{
    public ImageMetadata Read(string path)
    {
        IReadOnlyList<Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(path);
        }
        catch
        {
            // Corrupt, truncated, or a format MetadataExtractor won't parse.
            return default;
        }

        DateTime? taken = null;
        string? camera = null;
        int? orientation = null;

        foreach (var dir in directories)
        {
            if (dir is ExifSubIfdDirectory sub && taken is null)
            {
                if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                    taken = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            }

            if (dir is ExifIfd0Directory ifd0)
            {
                camera ??= ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
                if (orientation is null && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o))
                    orientation = o;
            }
        }

        // Fall back to any directory that happens to carry a capture date.
        if (taken is null)
        {
            foreach (var dir in directories)
            {
                if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                {
                    taken = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                    break;
                }
            }
        }

        return new ImageMetadata(taken, string.IsNullOrWhiteSpace(camera) ? null : camera, orientation);
    }
}
