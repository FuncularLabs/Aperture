using System.Text.Json;

namespace Reel.Core.Settings;

/// <summary>Loads and persists <see cref="ReelSettings"/> as JSON. Tolerant of a missing or corrupt file.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private ReelSettings _current;

    /// <summary>Raised after settings change and are persisted.</summary>
    public event EventHandler? Changed;

    public SettingsService(string dataDir)
    {
        _path = Path.Combine(dataDir, "settings.json");
        _current = Load();
    }

    public ReelSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Mutates settings under lock, persists, then raises <see cref="Changed"/>.</summary>
    public void Update(Action<ReelSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_current);
            Save(_current);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ReelSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ReelSettings>(File.ReadAllText(_path)) ?? new ReelSettings();
        }
        catch
        {
            // Corrupt or unreadable — fall back to defaults rather than failing startup.
        }
        return new ReelSettings();
    }

    private void Save(ReelSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Persisting settings must never crash the app.
        }
    }
}
