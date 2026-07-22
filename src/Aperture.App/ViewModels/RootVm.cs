using Aperture.App.Mvvm;
using Aperture.Core.Models;

namespace Aperture.App.ViewModels;

/// <summary>A root in the left nav: alias, path, item count, include checkbox, inline rename.</summary>
public sealed class RootVm : ObservableObject
{
    private readonly Action<RootVm, bool> _onIncludedChanged;
    private readonly Action<RootVm, string> _onAliasChanged;
    private readonly Action<RootVm, bool> _onRecursiveChanged;
    private bool _included;
    private bool _recursive;
    private int _count;
    private string _status = "";
    private bool _isEditing;
    private bool _isIndexing;
    private bool _isPaused;

    public RootVm(
        Root root, Action<RootVm, bool> onIncludedChanged, Action<RootVm, string> onAliasChanged,
        Action<RootVm, bool> onRecursiveChanged)
    {
        Model = root;
        _included = root.Included;
        _recursive = root.Recursive;
        _onIncludedChanged = onIncludedChanged;
        _onAliasChanged = onAliasChanged;
        _onRecursiveChanged = onRecursiveChanged;
        BeginRenameCommand = new RelayCommand(() => IsEditing = true);
    }

    public Root Model { get; }
    public long Id => Model.Id;
    public string Path => Model.Path;

    public RelayCommand BeginRenameCommand { get; }

    public string Alias
    {
        get => Model.Alias;
        set
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || Model.Alias == trimmed)
                return;
            Model.Alias = trimmed;
            OnPropertyChanged();
            _onAliasChanged(this, trimmed);
        }
    }

    /// <summary>True while the alias is being edited inline in the nav.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public bool IsIncluded
    {
        get => _included;
        set
        {
            if (SetProperty(ref _included, value))
            {
                Model.Included = value;
                _onIncludedChanged(this, value);
            }
        }
    }

    /// <summary>
    /// Index this folder's subfolders too. Turning it off re-indexes the root, which prunes the
    /// items that are no longer in scope; turning it back on picks them up again.
    /// </summary>
    public bool IsRecursive
    {
        get => _recursive;
        set
        {
            if (SetProperty(ref _recursive, value))
            {
                Model.Recursive = value;
                _onRecursiveChanged(this, value);
            }
        }
    }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>True while this root is actively being indexed (drives Pause/Cancel in its menu).</summary>
    public bool IsIndexing
    {
        get => _isIndexing;
        set => SetProperty(ref _isIndexing, value);
    }

    /// <summary>
    /// True when the user paused this root's indexing. Whatever was indexed before the pause is kept,
    /// and resuming picks up where it left off (already-indexed files are skipped, not re-read).
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }
}
