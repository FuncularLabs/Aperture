using System.Collections.ObjectModel;
using System.Windows.Input;
using Reel.App.Mvvm;

namespace Reel.App.ViewModels;

/// <summary>Backs the Tags &amp; Notes dialog: a chip list of tags with add/pick/remove, plus a note.</summary>
public sealed class AnnotationDialogVm : ObservableObject
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private readonly List<string> _allTags;
    private string _newTag = "";
    private string _note;

    public AnnotationDialogVm(string fileName, IEnumerable<string> tags, string note, IEnumerable<string> availableTags)
    {
        Header = fileName;
        Tags = new ObservableCollection<string>(tags);
        _note = note;
        _allTags = availableTags.ToList();
        Suggestions = new ObservableCollection<string>(_allTags.Where(t => !Contains(Tags, t)));

        AddTagCommand = new RelayCommand(() => { AddTag(_newTag); NewTag = ""; });
        PickTagCommand = new RelayCommand<string>(AddTag);
        RemoveTagCommand = new RelayCommand<string>(RemoveTag);
    }

    public string Header { get; }
    public ObservableCollection<string> Tags { get; }
    public ObservableCollection<string> Suggestions { get; }

    public string NewTag
    {
        get => _newTag;
        set => SetProperty(ref _newTag, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public bool HasSuggestions => Suggestions.Count > 0;

    public ICommand AddTagCommand { get; }
    public ICommand PickTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    private void AddTag(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag))
            return;
        if (!Contains(Tags, tag))
            Tags.Add(tag);
        var suggestion = Suggestions.FirstOrDefault(s => Ci.Equals(s, tag));
        if (suggestion is not null)
            Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void RemoveTag(string? tag)
    {
        if (tag is null)
            return;
        Tags.Remove(tag);
        if (_allTags.Any(t => Ci.Equals(t, tag)) && !Contains(Suggestions, tag))
        {
            Suggestions.Add(tag);
            OnPropertyChanged(nameof(HasSuggestions));
        }
    }

    private static bool Contains(IEnumerable<string> list, string value) => list.Any(x => Ci.Equals(x, value));
}
