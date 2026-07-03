using Reel.App.Mvvm;
using Reel.Core.Models;

namespace Reel.App.ViewModels;

/// <summary>A root in the left nav: alias, path, item count, and its include checkbox.</summary>
public sealed class RootVm : ObservableObject
{
    private readonly Action<RootVm, bool> _onIncludedChanged;
    private bool _included;
    private int _count;
    private string _status = "";

    public RootVm(Root root, Action<RootVm, bool> onIncludedChanged)
    {
        Model = root;
        _included = root.Included;
        _onIncludedChanged = onIncludedChanged;
    }

    public Root Model { get; }
    public long Id => Model.Id;
    public string Path => Model.Path;

    public string Alias
    {
        get => Model.Alias;
        set
        {
            if (Model.Alias == value)
                return;
            Model.Alias = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Bound to the checkbox; toggling re-filters the union.</summary>
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

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    /// <summary>Per-root status line (e.g. "indexing 1,204 / 8,900").</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}
