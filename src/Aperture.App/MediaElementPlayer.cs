using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

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
    private int _asked;   // each play, pause, seek and close: a deferred pause runs only if nothing came after it
    private bool _seeked; // moved since the last play or pause

    public MediaElementPlayer(Decorator host)
    {
        _host = host;
        _element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, // played and paused by quick view, not by being shown
            UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true,            // a seek while paused shows the frame there (0.9.2 V10)
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

    public TimeSpan Position
    {
        get => _element.Position;
        set
        {
            _asked++;
            _seeked = true;
            _element.Position = value;
        }
    }

    public TimeSpan? Duration => _element.NaturalDuration.HasTimeSpan ? _element.NaturalDuration.TimeSpan : null;

    public void Open(Uri source) => _element.Source = source;

    public void Play()
    {
        _asked++;
        _seeked = false;
        _element.Play();
    }

    /// <summary>
    /// Pauses. Straight after a seek the pause waits for the seek to go through (posted, at Background priority): a
    /// MediaElement that has reached its end and is paused straight after a seek goes back to 0:00, while one paused
    /// once the seek has gone through stays there (0.9.2 review round 1, F1; RealPlayerTests). A play, another seek or
    /// a close meanwhile cancels it.
    /// </summary>
    public void Pause()
    {
        var asked = ++_asked;
        if (!_seeked)
        {
            _element.Pause();
            return;
        }
        _seeked = false;
        _element.Dispatcher.BeginInvoke(() =>
        {
            if (asked == _asked)
                _element.Pause();
        }, DispatcherPriority.Background);
    }

    public void Rewind() => _element.Position = TimeSpan.Zero;

    public void Close()
    {
        _asked++;
        _element.Stop();
        _element.Close();      // lets go of the file
        _element.Source = null;
        if (ReferenceEquals(_host.Child, _element))
            _host.Child = null;
    }
}
