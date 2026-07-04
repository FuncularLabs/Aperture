using System.Windows.Media.Imaging;
using Reel.App.Mvvm;
using Reel.App.Services;
using Reel.Core.Formatting;
using Reel.Core.Models;

namespace Reel.App.ViewModels;

/// <summary>
/// One tile in the grid. The thumbnail loads lazily the first time the binding
/// reads <see cref="Thumbnail"/> — i.e. when the tile is realized — so only
/// on-screen tiles ever trigger a decode. No reliance on container lifecycle
/// events, which virtualization fires unreliably.
/// </summary>
public sealed class TileVm(LibraryRow row, ThumbnailService thumbnails, string captionFormat) : ObservableObject
{
    private readonly ThumbnailService _thumbnails = thumbnails;
    private readonly string _captionFormat = captionFormat;
    private BitmapSource? _thumbnail;
    private bool _loadStarted;

    public LibraryRow Row { get; } = row;
    public MediaItem Item => Row.Item;

    /// <summary>The date section this tile belongs to (set by the view model when grouping).</summary>
    public SectionVm? Section { get; set; }

    public long ItemId => Item.Id;
    public string FileName => Item.FileName;
    public string FullPath => Row.FullPath;
    public bool IsVideo => Item.IsVideo;

    /// <summary>
    /// The tile image. Reading it (which the binding does on realization) kicks a
    /// one-time async load; the property then raises change notification when the
    /// decoded bitmap is ready.
    /// </summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            if (!_loadStarted)
            {
                _loadStarted = true;
                BeginLoad();
            }
            return _thumbnail;
        }
        private set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>Caption rendered from the user's tokenized format string.</summary>
    public string Caption => CaptionFormatter.Format(_captionFormat, Item, Row.RootAlias);

    public string Tooltip =>
        $"{FileName}\n{DisplayDate:yyyy-MM-dd HH:mm}\n{Dimensions}\n{SizeText}\n{Row.RootAlias}";

    public string Dimensions => Item is { Width: { } w, Height: { } h } ? $"{w} × {h}" : "";

    public string SizeText => FormatBytes(Item.SizeBytes);

    // EXIF taken date is wall-clock (unspecified); mtime is UTC → show local.
    private DateTime DisplayDate => Item.TakenUtc ?? Item.MTimeUtc.ToLocalTime();

    private async void BeginLoad()
    {
        var bitmap = await _thumbnails.LoadAsync(ItemId, ThumbSize.Large);
        if (bitmap is not null)
            Thumbnail = bitmap;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
