namespace Aperture.Core.Models;

/// <summary>User-authored tags and a free-text note for a file, keyed by its path.</summary>
public sealed class Annotation
{
    public static readonly Annotation Empty = new();

    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Note { get; init; } = "";

    public bool IsEmpty => Tags.Count == 0 && string.IsNullOrWhiteSpace(Note);
}
