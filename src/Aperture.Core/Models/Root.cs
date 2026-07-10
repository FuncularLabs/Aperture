namespace Aperture.Core.Models;

/// <summary>
/// A folder the user has added to Aperture. Roots are the unit of inclusion in the
/// unioned browse view. The <see cref="Alias"/> disambiguates leaf folders that
/// share a name (e.g. several folders called "Pics").
/// </summary>
public sealed class Root
{
    public long Id { get; set; }

    /// <summary>Absolute, canonical path to the folder.</summary>
    public required string Path { get; set; }

    /// <summary>User-facing name, shown in the nav pane and available to captions as {alias}.</summary>
    public required string Alias { get; set; }

    /// <summary>Whether this root participates in the unioned view.</summary>
    public bool Included { get; set; } = true;

    /// <summary>Optional color tag (hex or named) for a visual cue. Null = none.</summary>
    public string? ColorTag { get; set; }

    public DateTime AddedUtc { get; set; }
}
