namespace Aperture.App;

/// <summary>
/// What quick view needs of a video player (0.9.1 batch, items 1–2). One per video shown: the window's makes a
/// <c>MediaElement</c> (<see cref="MediaElementPlayer"/>); tests use one that plays nothing.
/// </summary>
public interface IVideoPlayer
{
    /// <summary>Opens the video; <see cref="Opened"/> or <see cref="Failed"/> follows.</summary>
    void Open(Uri source);

    void Play();

    void Pause();

    /// <summary>Back to the start (after it ended).</summary>
    void Rewind();

    /// <summary>Stops it and lets go of its file.</summary>
    void Close();

    bool IsMuted { get; set; }

    /// <summary>0 (silent) to 1.</summary>
    double Volume { get; set; }

    /// <summary>The video's own size once it has opened; <c>null</c> until then.</summary>
    (int Width, int Height)? NaturalSize { get; }

    event EventHandler? Opened;

    event EventHandler? Ended;

    event EventHandler? Failed;
}
