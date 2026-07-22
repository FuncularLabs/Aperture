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

    /// <summary>
    /// Index this folder's subfolders too. All-or-nothing by design — a user who wants only some
    /// subtrees adds those folders as their own roots. Turning it off keeps Aperture off a large or
    /// expensive hierarchy (e.g. a cloud folder whose files are online-only and would be hydrated
    /// one by one). Turning it off on an existing root prunes the now-out-of-scope items on re-index.
    /// </summary>
    public bool Recursive { get; set; } = true;

    /// <summary>Optional color tag (hex or named) for a visual cue. Null = none.</summary>
    public string? ColorTag { get; set; }

    public DateTime AddedUtc { get; set; }
}
