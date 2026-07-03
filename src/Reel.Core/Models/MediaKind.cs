namespace Reel.Core.Models;

/// <summary>Whether an indexed item is a still image or a video. Persisted as the <c>kind</c> column.</summary>
public enum MediaKind
{
    Image = 0,
    Video = 1,
}
