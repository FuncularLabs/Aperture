using System.Collections.ObjectModel;
using Aperture.App.Mvvm;

namespace Aperture.App.ViewModels;

/// <summary>
/// A node in the left folder tree — a root or one of its subfolders. Children
/// load lazily when the node is first expanded (a placeholder keeps the expander
/// arrow visible until then).
/// </summary>
public sealed class FolderNodeVm : ObservableObject
{
    private static readonly FolderNodeVm Placeholder = new();

    private readonly Func<long, string, List<FolderNodeVm>>? _loadChildren;
    private readonly Action<FolderNodeVm, bool>? _onExpandedChanged;
    private readonly Action<FolderNodeVm, bool>? _onShownChanged;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isShown = true;

    private FolderNodeVm() { } // placeholder only

    public FolderNodeVm(
        long rootId, string relDir, string name, bool isRoot, bool hasChildren,
        RootVm? root, int count, Func<long, string, List<FolderNodeVm>> loadChildren,
        Action<FolderNodeVm, bool>? onExpandedChanged = null,
        bool isShown = true, Action<FolderNodeVm, bool>? onShownChanged = null)
    {
        _onExpandedChanged = onExpandedChanged;
        _onShownChanged = onShownChanged;
        RootId = rootId;
        RelDir = relDir;
        Name = name;
        IsRoot = isRoot;
        HasChildren = hasChildren;
        Root = root;
        Count = count;
        _isShown = isShown;
        _loadChildren = loadChildren;

        if (hasChildren)
            Children.Add(Placeholder);
    }

    public long RootId { get; }
    public string RelDir { get; } = "";

    /// <summary>Stable identity for this node across rebuilds — used to persist expansion state.</summary>
    public string Key => $"{RootId}|{RelDir}";
    public string Name { get; } = "";
    public bool IsRoot { get; }
    public bool HasChildren { get; }
    public int Count { get; }

    /// <summary>Non-null on root nodes — carries the include checkbox / rename / remove.</summary>
    public RootVm? Root { get; }

    /// <summary>
    /// A subfolder's show/hide checkbox (a root's is <see cref="RootVm.IsIncluded"/>): unticking hides the folder and
    /// everything inside it. A change is reported once, to the callback the tree gave this node.
    /// </summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (SetProperty(ref _isShown, value))
                _onShownChanged?.Invoke(this, value);
        }
    }

    public ObservableCollection<FolderNodeVm> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
                return;
            if (value)
                EnsureChildren();
            _onExpandedChanged?.Invoke(this, value);
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private void EnsureChildren()
    {
        if (_loaded || _loadChildren is null)
            return;
        _loaded = true;
        Children.Clear();
        foreach (var child in _loadChildren(RootId, RelDir))
            Children.Add(child);
    }
}
