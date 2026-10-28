using Aperture.App.Mvvm;

namespace Aperture.App.ViewModels;

/// <summary>
/// A collapsible date section header. The same instance is shared by every tile
/// in the section (the grid groups by it), so <see cref="IsExpanded"/> toggled
/// from the header drives all its tiles. A rebuild starts the section as the user
/// left it, if they toggled it, and otherwise by the default.
/// </summary>
public sealed class SectionVm : ObservableObject
{
    public required long Key { get; init; }
    public required string Label { get; init; }

    /// <summary>
    /// The tiles that belong to this section, in grid order. Populated by the (off-UI-thread)
    /// build; the view model assigns each tile's <see cref="TileVm.Section"/> from this list on
    /// the UI thread, so a reused tile shared with a concurrent build is never mutated off-thread.
    /// </summary>
    public List<TileVm> Members { get; } = [];

    private int _count;
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    private bool _isExpanded = true;

    /// <summary>Whether the section's tiles show. Setting it — the header's toggle, the keyboard — is the user's toggle.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                Toggled = true;
        }
    }

    /// <summary>
    /// Whether the user has expanded or collapsed this section since its build made it. The view model adds these to
    /// the toggles it remembers, which later builds start sections from; untoggled sections follow the default.
    /// </summary>
    public bool Toggled { get; private set; }

    /// <summary>
    /// How a build leaves the section, before anything shows it — as the user left it, or by the default. Not a toggle:
    /// the toggles the user made are remembered by the view model.
    /// </summary>
    internal void Start(bool expanded)
    {
        _isExpanded = expanded;
        Toggled = false;
    }

    /// <summary>True when the keyboard cursor is on this section's header.</summary>
    private bool _isCursor;
    public bool IsCursor
    {
        get => _isCursor;
        set => SetProperty(ref _isCursor, value);
    }
}

