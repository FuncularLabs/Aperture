using System.Windows.Input;
using Aperture.App.Mvvm;
using Aperture.Core.Settings;

namespace Aperture.App;

/// <summary>
/// Quick view's video playback (0.9.1 batch, items 1–2), as a model over <see cref="IVideoPlayer"/>: no WPF types, so
/// it's tested without a window. Shown a video, it makes a player for it and plays it with the sound the user last left
/// (muted the first time ever); <see cref="Toggle"/> plays and pauses it (from the start once it has ended); leaving it
/// stops it and lets go of its file. Events from a player it has left change nothing. A mute is saved at once; a
/// volume set while a video plays is saved as the video is left (the slider sets many as it's dragged).
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
    private bool _isMuted;
    private double _volume;
    private bool _soundUnsaved; // a volume set while a video plays: saved once, as it's left (a slider drag sets many)

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

    /// <summary>The video has opened: its own frame shows (until then, its still).</summary>
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

    public ICommand ToggleCommand { get; }

    public ICommand ToggleMuteCommand { get; }

    /// <summary>
    /// Whether a press is on the video: on its picture (the still, or its own frame once open), or anywhere while it
    /// has neither — so a click there plays or pauses rather than closing quick view.
    /// </summary>
    public static bool IsOnVideo(bool hasPicture, bool onPicture) => !hasPicture || onPicture;

    /// <summary>Shows the video at <paramref name="path"/>: a player of its own, with the saved sound, playing.</summary>
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
            player.Play();
            IsPlaying = true;
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
        IsPlaying = false;
        IsReady = false;
        HasFailed = false;
        IsVideoShown = false;
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
        }
        player.Play();
        IsPlaying = true;
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
        if (!ReferenceEquals(sender, _player))
            return; // a video already left
        IsReady = true;
        OnPropertyChanged(nameof(NaturalSize));
    }

    private void OnEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _player))
            return;
        _atEnd = true;
        IsPlaying = false;
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

    // The state the status line and Space follow: say they changed too.
    private void Set(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetProperty(ref field, value, name))
            return;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(SpaceToggles));
    }
}
