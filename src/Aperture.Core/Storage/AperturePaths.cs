namespace Aperture.Core.Storage;

/// <summary>
/// Default on-disk location for Aperture's data. The app uses <see cref="DefaultDataDir"/>;
/// tests pass their own directory straight into <see cref="ApertureDatabase"/>.
/// </summary>
public static class AperturePaths
{
    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%LOCALAPPDATA%\Aperture</c> — one central store, not scattered in user folders.</summary>
    public static string DefaultDataDir { get; } = Path.Combine(LocalAppData, "Aperture");

    /// <summary>
    /// <c>%LOCALAPPDATA%\Aperture Libraries</c> — holds the library list (<c>catalogs.json</c>) and is the
    /// suggested home for new libraries. A sibling of <see cref="DefaultDataDir"/>, not inside it, so the
    /// Default library's files and the list of every library never share a folder.
    /// </summary>
    public static string LibrariesDir { get; } = Path.Combine(LocalAppData, "Aperture Libraries");

    /// <summary>
    /// The pre-rename location (<c>%LOCALAPPDATA%\Reel</c>). A one-time migration moves any
    /// data found here into <see cref="DefaultDataDir"/> on first launch after the rename.
    /// </summary>
    public static string LegacyReelDataDir { get; } = Path.Combine(LocalAppData, "Reel");
}
