using System.Windows.Media.Imaging;
using Aperture.App.Mvvm;
using Aperture.App.Services;
using Aperture.Core.Formatting;
using Aperture.Core.Models;

namespace Aperture.App.ViewModels;

/// <summary>
/// One tile in the grid. The thumbnail loads lazily the first time the binding
/// reads <see cref="Thumbnail"/> — i.e. when the tile is realized — so only
/// on-screen tiles ever trigger a decode. No reliance on container lifecycle
/// events, which virtualization fires unreliably.
/// </summary>
public sealed class TileVm(LibraryRow row, ThumbnailService thumbnails, string captionFormat, Annotation annotation)
    : ObservableObject, IGridItem
{
    private readonly ThumbnailService _thumbnails = thumbnails;
    private readonly string _captionFormat = captionFormat;
    private BitmapSource? _thumbnail;
    private bool _loadStarted;

    private Annotation _annotation = annotation;

    public LibraryRow Row { get; } = row;
    public MediaItem Item => Row.Item;
    public Annotation Annotation => _annotation;

    /// <summary>
    /// Replaces this tile's tags/notes and refreshes its badge + tooltip in place —
    /// so editing annotations never rebuilds the grid (which would reset scroll,
    /// selection and re-trigger virtualization).
    /// </summary>
    public void UpdateAnnotation(Annotation annotation)
    {
        _annotation = annotation;
        OnPropertyChanged(nameof(Annotation));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(HasAnnotation));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>The date section this tile belongs to (set by the view model when grouping).</summary>
    public SectionVm? Section { get; set; }

    public long ItemId => Item.Id;
    public string FileName => Item.FileName;
    public string FullPath => Row.FullPath;
    public bool IsVideo => Item.IsVideo;

    public bool HasTags => Annotation.Tags.Count > 0;
    public bool HasNote => !string.IsNullOrWhiteSpace(Annotation.Note);
    public bool HasAnnotation => HasTags || HasNote;

    /// <summary>Folder location relative to the root, e.g. "Camera Sample/2014".</summary>
    public string Location
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Item.RelPath);
            return string.IsNullOrEmpty(dir) ? Row.RootAlias : $"{Row.RootAlias}/{dir.Replace('\\', '/')}";
        }
    }

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

    public string Tooltip
    {
        get
        {
            var lines = $"{FileName}\n{DisplayDate:yyyy-MM-dd HH:mm}\n{Dimensions}\n{SizeText}\nin [{Location}]";
            if (HasTags)
                lines += $"\n🏷 {string.Join(", ", Annotation.Tags)}";
            if (HasNote)
                lines += $"\n📝 {Annotation.Note}";
            return lines;
        }
    }

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
