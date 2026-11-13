namespace Aperture.Core.Settings;

/// <summary>Quick view's sound for videos: muted or not, and the volume (0–1).</summary>
public sealed record PlaybackPrefs(bool Muted = true, double Volume = 0.5)
{
    /// <summary>"Never played": muted, at half volume (0.9.1 batch, item 2: the first video plays muted).</summary>
    public static PlaybackPrefs Default { get; } = new();
}

/// <summary>
/// Keeps <see cref="PlaybackPrefs"/> across videos and launches, app-wide (every library's videos share it). Read
/// leniently: a missing, unreadable or corrupt file is <see cref="PlaybackPrefs.Default"/>.
/// </summary>
public sealed class PlaybackPrefsStore
{
    /// <summary>The file's name.</summary>
    public const string FileName = "playback.json";

    /// <summary>A store kept in <paramref name="path"/>.</summary>
    public PlaybackPrefsStore(string path) => FilePath = path;

    private PlaybackPrefsStore() { } // in memory

    /// <summary>The file it's kept in; <c>null</c> for one kept in memory.</summary>
    public string? FilePath { get; }

    /// <summary>A store that keeps what it's given in memory only (a window without the app's: tests).</summary>
    public static PlaybackPrefsStore InMemory() => new();

    /// <summary>
    /// Where the app keeps it: beside the library list (<paramref name="librariesDir"/>), or in the one library
    /// <c>APERTURE_DATA_DIR</c> pins (<paramref name="pinnedDataDir"/>).
    /// </summary>
    public static string PathFor(string? pinnedDataDir, string librariesDir) =>
        Path.Combine(pinnedDataDir ?? librariesDir, FileName);

    private PlaybackPrefs _memory = PlaybackPrefs.Default;

    /// <summary>What was last saved, or the default.</summary>
    public PlaybackPrefs Load()
    {
        if (FilePath is null)
            return _memory;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return PlaybackPrefs.Default;
            var muted = PlaybackPrefs.Default.Muted;
            var volume = PlaybackPrefs.Default.Volume;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // One value at a time, any case: what's there and of the right kind is kept, the rest is the default.
                if (string.Equals(property.Name, "muted", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                        muted = property.Value.GetBoolean();
                }
                else if (string.Equals(property.Name, "volume", StringComparison.OrdinalIgnoreCase)
                         && property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                         && property.Value.TryGetDouble(out var v) && double.IsFinite(v))
                {
                    volume = Math.Clamp(v, 0, 1);
                }
            }
            return new PlaybackPrefs(muted, volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                   or ArgumentException or NotSupportedException)
        {
            return PlaybackPrefs.Default; // missing, unreadable or not JSON: never played
        }
    }

    /// <summary>Saves, replacing the file whole (written beside it, then moved); a save that fails changes nothing.</summary>
    public void Save(PlaybackPrefs prefs)
    {
        prefs = prefs with { Volume = double.IsFinite(prefs.Volume) ? Math.Clamp(prefs.Volume, 0, 1) : PlaybackPrefs.Default.Volume };
        if (FilePath is null)
        {
            _memory = prefs;
            return;
        }
        var tmp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new { muted = prefs.Muted, volume = prefs.Volume }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or locked file, a folder that can't be made: the session keeps its sound; the file stays.
            try { File.Delete(tmp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
