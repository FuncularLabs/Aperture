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

    public void RaiseOpened(int width = 1920, int height = 1080)
    {
        NaturalSize = (width, height);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseEnded() => Ended?.Invoke(this, EventArgs.Empty);
    public void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);

    /// <summary>The last of play or pause it was told: what it's doing.</summary>
    public string? LastPlayOrPause => Calls.LastOrDefault(c => c is "play" or "pause");
}
