using Aperture.App;

namespace Aperture.Core.Tests.Support;

/// <summary>A video player that plays nothing: it records what it's told, and a test raises its events.</summary>
public sealed class FakeVideoPlayer : IVideoPlayer
{
    public List<string> Calls { get; } = [];
    public Uri? Source { get; private set; }
    public bool IsClosed { get; private set; }

    public bool IsMuted { get; set; }
    public double Volume { get; set; }
    public (int Width, int Height)? NaturalSize { get; set; }

    private TimeSpan _position;

    /// <summary>Where it is; a set is a seek, recorded as <c>seek:&lt;seconds&gt;</c>. <see cref="PlayTo"/> moves it as playing does.</summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            _position = value;
            Calls.Add($"seek:{value.TotalSeconds:0.###}");
        }
    }

    /// <summary>The video's length, once it has opened (null: one it can't tell).</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>As if it had played on to <paramref name="seconds"/>: no seek recorded.</summary>
    public void PlayTo(double seconds) => _position = TimeSpan.FromSeconds(seconds);

    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler? Failed;

    public void Open(Uri source)
    {
        Source = source;
        Calls.Add("open");
    }

    public void Play() => Calls.Add("play");
    public void Pause() => Calls.Add("pause");
    public void Rewind() => Calls.Add("rewind");

    public void Close()
    {
        IsClosed = true;
        Calls.Add("close");
    }

    public void RaiseOpened(int width = 1920, int height = 1080, double? seconds = 83)
    {
        NaturalSize = (width, height);
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
    public void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);

    /// <summary>The last of play or pause it was told: what it's doing.</summary>
    public string? LastPlayOrPause => Calls.LastOrDefault(c => c is "play" or "pause");
}
