using System.Windows.Controls;
using System.Windows.Media;

namespace Aperture.App;

/// <summary>
/// Quick view's player: a WPF <see cref="MediaElement"/> (Windows' own media playback), made in code for each video and
/// put in quick view's video host — never in XAML, so a PC without a media player still opens the window (making one
/// there throws, and the video keeps its still). Closing it stops it, lets go of its file and takes it out of the host.
/// Exercised by the opt-in <c>RealPlayerTests</c> (APERTURE_MEDIA_TESTS=1): CI has no media player.
/// </summary>
public sealed class MediaElementPlayer : IVideoPlayer
{
    private readonly Decorator _host;
    private readonly MediaElement _element;

    public MediaElementPlayer(Decorator host)
    {
        _host = host;
        _element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, // played and paused by quick view, not by being shown
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,           // quick view's area takes the clicks
            Focusable = false,
        };
        _element.MediaOpened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _element.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        _element.MediaFailed += (_, _) => Failed?.Invoke(this, EventArgs.Empty);
        host.Child = _element;
    }

    public event EventHandler? Opened;

    public event EventHandler? Ended;

    public event EventHandler? Failed;

    public bool IsMuted
    {
        get => _element.IsMuted;
        set => _element.IsMuted = value;
    }

    public double Volume
    {
        get => _element.Volume;
        set => _element.Volume = value;
    }

    public (int Width, int Height)? NaturalSize =>
        _element.NaturalVideoWidth > 0 ? (_element.NaturalVideoWidth, _element.NaturalVideoHeight) : null;

    public void Open(Uri source) => _element.Source = source;

    public void Play() => _element.Play();

    public void Pause() => _element.Pause();

    public void Rewind() => _element.Position = TimeSpan.Zero;

    public void Close()
    {
        _element.Stop();
        _element.Close();      // lets go of the file
        _element.Source = null;
        if (ReferenceEquals(_host.Child, _element))
            _host.Child = null;
    }
}
