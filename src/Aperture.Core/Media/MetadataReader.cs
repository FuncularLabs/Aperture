using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;

namespace Aperture.Core.Media;

/// <summary>EXIF fields Aperture cares about, extracted with MetadataExtractor.</summary>
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

    /// <summary>
    /// Human-readable EXIF label/value pairs for the preview pane (camera, lens,
    /// exposure, ISO, GPS…). Empty for files without EXIF; never throws.
    /// </summary>
    public static List<(string Label, string Value)> ReadExifSummary(string path)
    {
        var result = new List<(string, string)>();
        IReadOnlyList<Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(path); }
        catch { return result; }

        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        static string? Desc(Directory? d, int tag)
        {
            var s = d?.GetDescription(tag);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        void Add(string label, string? v)
        {
            if (!string.IsNullOrWhiteSpace(v))
                result.Add((label, v!));
        }

        var camera = string.Join(' ',
            new[] { Desc(ifd0, ExifDirectoryBase.TagMake), Desc(ifd0, ExifDirectoryBase.TagModel) }
                .Where(s => s is not null));
        Add("Camera", camera.Length > 0 ? camera : null);
        Add("Lens", Desc(sub, ExifDirectoryBase.TagLensModel));
        Add("Focal length", Desc(sub, ExifDirectoryBase.TagFocalLength));
        Add("Aperture", Desc(sub, ExifDirectoryBase.TagFNumber));
        Add("Shutter", Desc(sub, ExifDirectoryBase.TagExposureTime));
        Add("ISO", Desc(sub, ExifDirectoryBase.TagIsoEquivalent));
        Add("Flash", Desc(sub, ExifDirectoryBase.TagFlash));

        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        if (gps?.GetGeoLocation() is { } loc && (loc.Latitude != 0 || loc.Longitude != 0))
            Add("GPS", $"{loc.Latitude:0.00000}, {loc.Longitude:0.00000}");

        return result;
    }
}
