using System.Collections.ObjectModel;
using Reel.App.Mvvm;

namespace Reel.App.ViewModels;

/// <summary>
/// A node in the left folder tree — a root or one of its subfolders. Children
/// load lazily when the node is first expanded (a placeholder keeps the expander
/// arrow visible until then).
/// </summary>
public sealed class FolderNodeVm : ObservableObject
{
    private static readonly FolderNodeVm Placeholder = new();

    private readonly Func<long, string, List<FolderNodeVm>>? _loadChildren;
    private bool _loaded;
    private bool _isExpanded;
    private bool _isSelected;

    private FolderNodeVm() { } // placeholder only

    public FolderNodeVm(
        long rootId, string relDir, string name, bool isRoot, bool hasChildren,
        RootVm? root, int count, Func<long, string, List<FolderNodeVm>> loadChildren)
    {
        RootId = rootId;
        RelDir = relDir;
        Name = name;
        IsRoot = isRoot;
        HasChildren = hasChildren;
        Root = root;
        Count = count;
        _loadChildren = loadChildren;

        if (hasChildren)
            Children.Add(Placeholder);
    }

    public long RootId { get; }
    public string RelDir { get; } = "";
    public string Name { get; } = "";
    public bool IsRoot { get; }
    public bool HasChildren { get; }
    public int Count { get; }

    /// <summary>Non-null on root nodes — carries the include checkbox / rename / remove.</summary>
    public RootVm? Root { get; }

    public ObservableCollection<FolderNodeVm> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
                EnsureChildren();
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
