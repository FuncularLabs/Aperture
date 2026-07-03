namespace Reel.Core.Models;

/// <summary>
/// The three cached thumbnail sizes. Values are stable ints because they are
/// persisted as the <c>size</c> column in the thumbnail store.
/// </summary>
public enum ThumbSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

public static class ThumbSizes
{
    /// <summary>All sizes we cache, smallest first.</summary>
    public static readonly ThumbSize[] All = [ThumbSize.Small, ThumbSize.Medium, ThumbSize.Large];

    /// <summary>Longest-edge pixel target for each cached size.</summary>
    public static int LongestEdge(ThumbSize size) => size switch
    {
        ThumbSize.Small => 128,
        ThumbSize.Medium => 256,
        ThumbSize.Large => 512,
        _ => 256,
    };
}
