using Reel.Core.Models;

namespace Reel.Core.Media;

/// <summary>
/// Extensions Reel indexes. Images are decoded with SkiaSharp; videos get a
/// thumbnail from the Windows shell (the same frame Explorer shows) and are only
/// included when the user has videos enabled.
/// </summary>
public static class SupportedFormats
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".mts", ".m2ts",
    };

    public static bool IsImage(string extensionWithDot) => ImageExtensions.Contains(extensionWithDot);

    public static bool IsVideo(string extensionWithDot) => VideoExtensions.Contains(extensionWithDot);

    /// <param name="includeVideos">When false, video files are treated as unsupported.</param>
    public static bool IsSupported(string extensionWithDot, bool includeVideos) =>
        IsImage(extensionWithDot) || (includeVideos && IsVideo(extensionWithDot));

    public static MediaKind KindOf(string extensionWithDot) =>
        IsVideo(extensionWithDot) ? MediaKind.Video : MediaKind.Image;

    public static IReadOnlyCollection<string> Images => ImageExtensions;
    public static IReadOnlyCollection<string> Videos => VideoExtensions;
}
