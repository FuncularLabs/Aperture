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
    private bool _playing;     // told to play, and not told to pause since (an ended video still counts: it isn't paused)
    private bool _paused;      // told to pause at least once (the first pause after Open is what opens the video)
    private bool _played;      // told to play at least once
    private TimeSpan? _before; // a seek made before it was ever played, set again once it is (round 2, R2-1)

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
            if (!_played)
                _before = value;
            _element.Position = value;
        }
    }

    public TimeSpan? Duration => _element.NaturalDuration.HasTimeSpan ? _element.NaturalDuration.TimeSpan : null;

    public void Open(Uri source) => _element.Source = source;

    /// <summary>
    /// Plays. The first play after a seek made before the video was ever played sets that place again straight after:
    /// a MediaElement opened paused starts its first play from 0:00 whatever its position said (round 2, R2-1).
    /// </summary>
    public void Play()
    {
        _asked++;
        _seeked = false;
        _playing = true;
        _played = true;
        _element.Play();
        if (_before is { } at)
        {
            _before = null;
            _element.Position = at;
        }
    }

    /// <summary>
    /// Pauses. A video already paused (opened by a pause, or paused since) is left alone: a second pause on a
    /// MediaElement opened by a pause sends it back to 0:00 (round 2, R2-1). Straight after a seek the pause is posted
    /// at Background priority, so the seek is taken before it: a MediaElement that has reached its end and is paused
    /// straight after a seek goes back to 0:00, while one paused a moment later stays there (round 1, A-F1). A play,
    /// another seek or a close meanwhile cancels it.
    /// </summary>
    public void Pause()
    {
        var asked = ++_asked;
        if (_paused && !_playing)
        {
            _seeked = false;
            return; // already paused: left where it is
        }
        _paused = true;
        _playing = false;
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
        _playing = false;
        _element.Stop();
        _element.Close();      // lets go of the file
        _element.Source = null;
        if (ReferenceEquals(_host.Child, _element))
            _host.Child = null;
    }
}
