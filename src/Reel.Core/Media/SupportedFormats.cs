namespace Reel.Core.Media;

/// <summary>
/// Image extensions Reel indexes in v1. Limited to what SkiaSharp decodes
/// reliably on Windows without extra native codecs. HEIC/TIFF are deferred to a
/// shell-thumbnail fallback in a later milestone.
/// </summary>
public static class SupportedFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
    };

    /// <param name="extensionWithDot">Extension including the leading dot, any case.</param>
    public static bool IsSupported(string extensionWithDot) => Extensions.Contains(extensionWithDot);

    public static IReadOnlyCollection<string> All => Extensions;
}
