using Aperture.App.Mvvm;

namespace Aperture.App.ViewModels;

/// <summary>
/// A collapsible date section header. The same instance is shared by every tile
/// in the section (the grid groups by it), so <see cref="IsExpanded"/> toggled
/// from the header drives all its tiles, and state survives incidental rebuilds.
/// </summary>
public sealed class SectionVm : ObservableObject
{
    public required long Key { get; init; }
    public required string Label { get; init; }

    private int _count;
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>True when the keyboard cursor is on this section's header.</summary>
    private bool _isCursor;
    public bool IsCursor
    {
        get => _isCursor;
        set => SetProperty(ref _isCursor, value);
    }
}

