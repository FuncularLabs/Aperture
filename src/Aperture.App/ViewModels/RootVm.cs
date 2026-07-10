using Aperture.App.Mvvm;
using Aperture.Core.Models;

namespace Aperture.App.ViewModels;

/// <summary>A root in the left nav: alias, path, item count, include checkbox, inline rename.</summary>
public sealed class RootVm : ObservableObject
{
    private readonly Action<RootVm, bool> _onIncludedChanged;
    private readonly Action<RootVm, string> _onAliasChanged;
    private bool _included;
    private int _count;
    private string _status = "";
    private bool _isEditing;

    public RootVm(Root root, Action<RootVm, bool> onIncludedChanged, Action<RootVm, string> onAliasChanged)
    {
        Model = root;
        _included = root.Included;
        _onIncludedChanged = onIncludedChanged;
        _onAliasChanged = onAliasChanged;
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
}
