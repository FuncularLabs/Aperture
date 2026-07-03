namespace Reel.Core.Models;

/// <summary>
/// One indexed image. Timestamps are stored as ticks in the database; here they
/// are surfaced as <see cref="DateTime"/>.
/// </summary>
/// <remarks>
/// <see cref="MTimeUtc"/> is the file's last-write time in UTC.
/// <see cref="TakenUtc"/> is the EXIF DateTimeOriginal as wall-clock time (EXIF
/// carries no timezone), or null when absent. For sorting/grouping prefer
/// <see cref="BestDate"/>, which falls back to mtime.
/// </remarks>
public sealed class MediaItem
{
    public long Id { get; init; }
    public long RootId { get; init; }

    /// <summary>Path relative to the owning root, using the OS separator.</summary>
    public required string RelPath { get; init; }
    public required string FileName { get; init; }

    /// <summary>Lower-case extension including the leading dot (e.g. ".jpg").</summary>
    public required string Ext { get; init; }

    public long SizeBytes { get; init; }
    public DateTime MTimeUtc { get; init; }
    public DateTime? TakenUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Camera { get; init; }

    /// <summary>Raw EXIF orientation value (1-8), or null when unknown.</summary>
    public int? Orientation { get; init; }

    public DateTime IndexedUtc { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Image;

    public bool IsVideo => Kind == MediaKind.Video;

    /// <summary>The date to group and sort by: EXIF taken date if present, else file mtime.</summary>
    public DateTime BestDate => TakenUtc ?? MTimeUtc;
}
