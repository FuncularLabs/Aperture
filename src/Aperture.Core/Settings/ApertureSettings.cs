namespace Aperture.Core.Settings;

/// <summary>One level of a multi-level sort: a token to sort by and a direction.</summary>
public sealed class SortLevel
{
    public string Token { get; set; } = "date";
    public bool Descending { get; set; }

    public SortLevel() { }

    public SortLevel(string token, bool descending)
    {
        Token = token;
        Descending = descending;
    }
}

/// <summary>
/// User-tunable settings, persisted as JSON. Defaults are chosen so the app is
/// usable out of the box with no configuration.
/// </summary>
public sealed class ApertureSettings
{
    /// <summary>Index and show video files (thumbnail from the OS shell). On by default.</summary>
    public bool IncludeVideos { get; set; } = true;

    /// <summary>Zoom stop index (0..4) the grid opens at.</summary>
    public int DefaultZoom { get; set; } = 2;

    /// <summary>Tile caption format string. See <c>CaptionFormatter</c> for tokens.</summary>
    public string CaptionFormat { get; set; } = "{date:yyyy-MM-dd HH.mm} · {alias}";

    /// <summary>Active sort, by preset label (see MainViewModel.SortOptions).</summary>
    public string SortPreset { get; set; } = "Newest first";

    /// <summary>Group the grid into collapsible date sections (only applies to date sorts). On by default.</summary>
    public bool GroupByDate { get; set; } = true;

    /// <summary>Soft cap on the on-disk thumbnail cache, in megabytes.</summary>
    public long ThumbnailCacheCapMb { get; set; } = 2048;

    // --- Window / layout persistence ---

    /// <summary>
    /// Native WINDOWPLACEMENT as [normalLeft, normalTop, normalRight, normalBottom, showCmd].
    /// Restored with Set/GetWindowPlacement so size, position, monitor and maximized state
    /// survive across launches even on a multi-monitor / mixed-DPI setup.
    /// </summary>
    public int[]? WindowPlacement { get; set; }

    /// <summary>Width, in DIPs, of the left folder pane.</summary>
    public double? NavWidth { get; set; }

    /// <summary>Set once the first-run folder detection has been offered.</summary>
    public bool FirstRunDone { get; set; }
}
