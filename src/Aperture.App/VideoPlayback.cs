using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.Core.Formatting;
using Aperture.Core.Settings;

namespace Aperture.App;

/// <summary>
/// Quick view's video playback (0.9.1 batch, items 1–2; 0.9.2 V9–V10), as a model over <see cref="IVideoPlayer"/>: no
/// WPF types, so it's tested without a window. Shown a video, it makes a player for it and opens it paused, with the
/// sound the user last left (muted the first time ever): only <see cref="Toggle"/> (the play button, Space, a click)
/// plays it (from the start once it has ended), and pauses it again. <see cref="Seek"/> moves it (the seek bar);
/// <see cref="Tick"/> follows it as it plays (the window's timer), except while the bar is dragged. Leaving it stops it
/// and lets go of its file. Events from a player it has left change nothing. A mute is saved at once; a volume set
/// while a video plays is saved as the video is left (the slider sets many as it's dragged).
/// </summary>
public sealed class VideoPlayback : ObservableObject
{
    private readonly PlaybackPrefsStore _prefs;
    private readonly Func<IVideoPlayer> _playerFactory;
    private IVideoPlayer? _player;
    private bool _isVideoShown;
    private bool _isReady;
    private bool _isPlaying;
    private bool _hasFailed;
    private bool _atEnd;
    // How far before its end a player may be when it says it has ended (its last frame, rounding).
    private static readonly TimeSpan EndSlack = TimeSpan.FromSeconds(0.5);
    private bool _isMuted;
    private double _volume;
    private bool _soundUnsaved; // a volume set while a video plays: saved once, as it's left (a slider drag sets many)
    private bool _started;      // played or moved along once: its own frame shows (until then, its still)
    private TimeSpan? _duration;
    private double _position;   // seconds; what the seek bar and the time show
    private bool _scrubbing;    // the seek bar is being dragged: it follows the drag, not the player

    /// <param name="prefs">Where the sound is kept between videos and launches.</param>
    /// <param name="playerFactory">Makes the player for each video shown; one that throws means "can't play here".</param>
    public VideoPlayback(PlaybackPrefsStore prefs, Func<IVideoPlayer> playerFactory)
    {
        _prefs = prefs;
        _playerFactory = playerFactory;
        (_isMuted, _volume) = prefs.Load();
        ToggleCommand = new RelayCommand(Toggle);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
    }

    /// <summary>A video is the item quick view shows (playable or not).</summary>
    public bool IsVideoShown
    {
        get => _isVideoShown;
        private set => Set(ref _isVideoShown, value);
    }

    /// <summary>The video has opened (its length is known; its own frame shows once <see cref="ShowsFrame"/>).</summary>
    public bool IsReady
    {
        get => _isReady;
        private set => Set(ref _isReady, value);
    }

    /// <summary>Playing, or (before it has opened) to play once it has.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>The video can't be played here (no codec, no media player, the file unreachable).</summary>
    public bool HasFailed
    {
        get => _hasFailed;
        private set => Set(ref _hasFailed, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    /// <summary>0–1; set by the volume slider (as <see cref="SetVolume"/>).</summary>
    public double Volume
    {
        get => _volume;
        set => SetVolume(value);
    }

    /// <summary>The video's own size once it has opened.</summary>
    public (int Width, int Height)? NaturalSize => _isReady ? _player?.NaturalSize : null;

    /// <summary>The player of the video shown; <c>null</c> when none is (or it couldn't be made).</summary>
    public IVideoPlayer? Player => _player;

    /// <summary>Space plays and pauses (a playable video is shown); otherwise it closes quick view, as for a photo.</summary>
    public bool SpaceToggles => _isVideoShown && !_hasFailed;

    /// <summary>The line under a video that isn't playing yet, or can't; empty otherwise.</summary>
    public string StatusLine =>
        !_isVideoShown ? ""
        : _hasFailed ? "This video can't be played here. Press Enter to open it in its default app."
        : !_isReady ? "Loading video…"
        : "";

    /// <summary>
    /// The video's own frame shows rather than its still: it has opened, and has been played or moved along once (a
    /// video opens paused, and its first frame is often black where the still isn't).
    /// </summary>
    public bool ShowsFrame => _isReady && _started;

    /// <summary>The seek bar works: the video has opened and the player tells its length.</summary>
    public bool CanSeek => _isReady && !_hasFailed && _player is not null && _duration is { } length && length > TimeSpan.Zero;

    /// <summary>The seek bar's end: the video's length in seconds (0 until it's known).</summary>
    public double DurationSeconds => _isReady && _duration is { } length ? length.TotalSeconds : 0;

    /// <summary>Where the video is, in seconds: the seek bar's value. Setting it seeks (<see cref="Seek"/>).</summary>
    public double PositionSeconds
    {
        get => _position;
        set => Seek(value);
    }

    /// <summary>"elapsed / length" (<see cref="MediaTime"/>); <see cref="MediaTime.Pending"/> until it has opened.</summary>
    public string TimeText =>
        !_isVideoShown || _hasFailed ? ""
        : !_isReady ? MediaTime.Pending
        : MediaTime.Format(TimeSpan.FromSeconds(_position), _duration);

    /// <summary>
    /// Moves the video to <paramref name="seconds"/> (clamped to its length), playing or paused; a paused video then
    /// shows the frame there and stays there, and play goes on from there (MediaElementPlayer keeps the MediaElement's
    /// quirks out of it: round 2, R2-1). Moved to its end, the next play starts again; moved back from its end, it plays
    /// from there. Nothing happens before it has opened, or when its length isn't known.
    /// </summary>
    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds) || !CanSeek || _player is not { } player || _duration is not { } length)
            return;
        var at = Math.Clamp(seconds, 0, length.TotalSeconds);
        player.Position = TimeSpan.FromSeconds(at);
        // After the seek, a video not playing is paused there: one ended and not paused would go on from here (V12); one
        // already paused is left as it is (MediaElementPlayer.Pause). Not at its very end: a MediaElement paused at its
        // end sends the next seek back to 0:00 (round 2, P2); left there, it plays nothing.
        if (!_isPlaying && at < length.TotalSeconds)
            player.Pause();
        _position = at;
        _atEnd = at >= length.TotalSeconds;
        _started = true;
        RaiseTimeline();
    }

    /// <summary>Follows the player as it plays (the window calls it a few times a second): its place, and a length told late.</summary>
    public void Tick()
    {
        if (_player is not { } player || !_isReady || _hasFailed)
            return;
        var changed = false;
        if (_duration is null && player.Duration is { } told)
        {
            _duration = told;
            changed = true;
        }
        if (!_scrubbing)
        {
            var at = Math.Max(0, player.Position.TotalSeconds);
            if (_duration is { } length)
                at = Math.Min(at, length.TotalSeconds);
            if (at != _position)
            {
                _position = at;
                changed = true;
            }
        }
        if (changed)
            RaiseTimeline();
    }

    /// <summary>The seek bar's thumb is being dragged: <see cref="Tick"/> leaves the bar under the pointer.</summary>
    public void BeginScrub() => _scrubbing = _player is not null;

    public void EndScrub() => _scrubbing = false;

    public ICommand ToggleCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    /// <summary>
    /// Whether a press is on the video: on its picture (the still, or its own frame once open), or anywhere while it
    /// has neither — so a click there plays or pauses rather than closing quick view.
    /// </summary>
    public static bool IsOnVideo(bool hasPicture, bool onPicture) => !hasPicture || onPicture;

    /// <summary>
    /// Shows the video at <paramref name="path"/>: a player of its own, with the saved sound, opened paused (0.9.2 V9:
    /// it plays only when <see cref="Toggle"/> is asked to; a Manual MediaElement opens on a pause).
    /// </summary>
    public void Show(string path)
    {
        Leave();
        IsVideoShown = true;
        IVideoPlayer? player = null;
        try
        {
            player = _playerFactory();
            player.Opened += OnOpened;
            player.Ended += OnEnded;
            player.Failed += OnFailed;
            _player = player;
            player.IsMuted = _isMuted;
            player.Volume = _volume;
            player.Open(new Uri(path));
            player.Pause();
            IsPlaying = false;
        }
        catch (Exception)
        {
            // No media player on this PC (or one that won't take this file): the still stays, with a line saying so.
            if (player is not null)
                Drop(player);
            _player = null;
            HasFailed = true;
            IsPlaying = false;
        }
        OnPropertyChanged(nameof(Player));
        RaiseTimeline();
    }

    /// <summary>Stops the video shown, if any, and lets go of its file (another item, quick view closed, a switch).</summary>
    public void Leave()
    {
        if (_soundUnsaved)
            Save();
        if (_player is { } player)
        {
            _player = null;
            Drop(player);
            OnPropertyChanged(nameof(Player));
        }
        _atEnd = false;
        _started = false;
        _duration = null;
        _position = 0;
        _scrubbing = false;
        IsPlaying = false;
        IsReady = false;
        HasFailed = false;
        IsVideoShown = false;
        RaiseTimeline();
    }

    /// <summary>Plays or pauses; once it has ended, plays it again from the start.</summary>
    public void Toggle()
    {
        if (_player is not { } player || _hasFailed)
            return;
        if (_isPlaying)
        {
            player.Pause();
            IsPlaying = false;
            return;
        }
        if (_atEnd)
        {
            player.Rewind();
            _atEnd = false;
            _position = 0;
        }
        player.Play();
        IsPlaying = true;
        _started = true;
        RaiseTimeline();
    }

    /// <summary>Pauses (before the video is opened in its default app).</summary>
    public void Pause()
    {
        if (_player is not { } player || !_isPlaying)
            return;
        player.Pause();
        IsPlaying = false;
    }

    public void ToggleMute() => SetMuted(!_isMuted);

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        if (_player is { } player)
            player.IsMuted = muted;
        Save();
    }

    /// <summary>Sets the volume (clamped to 0–1); raising it while muted unmutes, as Windows' own volume does.</summary>
    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume))
            return;
        volume = Math.Clamp(volume, 0, 1);
        var raised = volume > _volume;
        SetProperty(ref _volume, volume, nameof(Volume));
        if (_player is { } player)
            player.Volume = volume;
        if (raised && _isMuted)
            SetMuted(false); // saves both
        else if (_player is null)
            Save();
        else
            _soundUnsaved = true; // saved as the video is left, not once for every step of the slider (review R1)
    }

    private void Save()
    {
        _prefs.Save(new PlaybackPrefs(_isMuted, _volume));
        _soundUnsaved = false;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player) || _player is not { } player)
            return; // a video already left
        _duration = player.Duration;
        IsReady = true;
        OnPropertyChanged(nameof(NaturalSize));
        RaiseTimeline();
    }

    private void OnEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player) || _player is not { } player)
            return;
        // The end comes as an event, after the fact: moved back since (the bar dragged off its end, two seeks in a turn),
        // it plays or holds where it was moved, and this end is old news (round 4's layer: seed 36 of
        // RealPlayerSequenceTests).
        if (_duration is { } length && player.Position < length - EndSlack)
            return;
        _atEnd = true;
        IsPlaying = false; // the player itself isn't paused: paused at its end, a seek back lands at 0:00 (round 1, F1)
        if (!_scrubbing)
            _position = _duration?.TotalSeconds ?? Math.Max(0, player.Position.TotalSeconds); // the bar at the end
        RaiseTimeline();
    }

    private void OnFailed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player) || _player is not { } player)
            return;
        _player = null;
        Drop(player);
        OnPropertyChanged(nameof(Player));
        IsPlaying = false;
        IsReady = false;
        HasFailed = true;
        _started = false;
        _duration = null;
        _position = 0;
        _scrubbing = false;
        RaiseTimeline();
    }

    private void Drop(IVideoPlayer player)
    {
        player.Opened -= OnOpened;
        player.Ended -= OnEnded;
        player.Failed -= OnFailed;
        try
        {
            player.Close();
        }
        catch (Exception)
        {
            // Letting go must never throw out of moving on or closing quick view.
        }
    }

    // What the seek bar, the time and the frame-or-still follow: said together whenever any of it may have moved.
    private void RaiseTimeline()
    {
        OnPropertyChanged(nameof(ShowsFrame));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(TimeText));
    }

    // The state the status line and Space follow: say they changed too.
    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
            return;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(SpaceToggles));
    }
}
