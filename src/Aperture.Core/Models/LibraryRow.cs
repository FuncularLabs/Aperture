namespace Aperture.Core.Models;

/// <summary>An item as it appears in the unioned browse view: the item plus its root's display info.</summary>
public sealed class LibraryRow
{
    public required MediaItem Item { get; init; }
    public required string RootAlias { get; init; }
    public required string RootPath { get; init; }
    public string? RootColor { get; init; }

    /// <summary>Absolute path to the underlying file (root path + relative path).</summary>
    public string FullPath => System.IO.Path.Combine(RootPath, Item.RelPath);
}
