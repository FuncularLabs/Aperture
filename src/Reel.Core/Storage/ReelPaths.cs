namespace Reel.Core.Storage;

/// <summary>
/// Default on-disk location for Reel's data. The app uses <see cref="DefaultDataDir"/>;
/// tests pass their own directory straight into <see cref="ReelDatabase"/>.
/// </summary>
public static class ReelPaths
{
    /// <summary><c>%LOCALAPPDATA%\Reel</c> — one central store, not scattered in user folders.</summary>
    public static string DefaultDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Reel");
}
