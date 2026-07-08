using System.Collections.ObjectModel;
using System.Windows.Input;
using Reel.App.Mvvm;
using Reel.Core.Library;

namespace Reel.App.ViewModels;

/// <summary>One row in the tag manager: an editable name + how many files use it.</summary>
public sealed class TagItemVm(string name, int count) : ObservableObject
{
    private string _name = name;

    public string OriginalName { get; } = name;
    public int Count { get; } = count;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

/// <summary>Rename (=merge) and delete tags across the whole library.</summary>
public sealed class TagManagerVm : ObservableObject
{
    private readonly LibraryService _library;

    public TagManagerVm(LibraryService library)
    {
        _library = library;
        RenameCommand = new RelayCommand<TagItemVm>(Rename);
        DeleteCommand = new RelayCommand<TagItemVm>(Delete);
        Reload();
    }

    public ObservableCollection<TagItemVm> Tags { get; } = [];
    public bool IsEmpty => Tags.Count == 0;

    /// <summary>Set once any tag was renamed or deleted, so the caller can refresh.</summary>
    public bool Changed { get; private set; }

    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    private void Reload()
    {
        Tags.Clear();
        foreach (var kv in _library.GetTagCounts().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Tags.Add(new TagItemVm(kv.Key, kv.Value));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Rename(TagItemVm? item)
    {
        if (item is null)
            return;
        var newName = item.Name.Trim();
        if (newName.Length == 0 || string.Equals(newName, item.OriginalName, StringComparison.Ordinal))
            return;
        _library.RenameTag(item.OriginalName, newName);
        Changed = true;
        Reload();
    }

    private void Delete(TagItemVm? item)
    {
        if (item is null)
            return;
        _library.DeleteTag(item.OriginalName);
        Changed = true;
        Reload();
    }
}
