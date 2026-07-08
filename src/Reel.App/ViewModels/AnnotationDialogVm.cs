using System.Collections.ObjectModel;
using System.Windows.Input;
using Reel.App.Mvvm;
using Reel.Core.Annotations;

namespace Reel.App.ViewModels;

/// <summary>One item's current annotation, as fed into the dialog.</summary>
public sealed record AnnotationTarget(string Path, IReadOnlyList<string> Tags, string Note);

/// <summary>A tag chip in the dialog. Across a multi-selection a tag may be "shared"
/// (on every selected item) or "partial" (on some) — shown with a dashed outline.</summary>
public sealed class TagChipVm(string name, int count, int total)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public int Total { get; } = total;

    public bool Shared => Count >= Total;
    public string ScopeTooltip => Shared
        ? (Total > 1 ? $"On all {Total} selected" : "")
        : $"Pertains to {Count} of {Total} selected";
}

/// <summary>
/// Backs the Tags &amp; Notes dialog for one or many items. Edits are tracked as
/// deltas (tags to add to all / remove from all) so a partial tag that's neither
/// added nor removed stays per-item on save. Notes edit the majority group; items
/// with a different note are shown as excluded and left untouched.
/// </summary>
public sealed class AnnotationDialogVm : ObservableObject
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private readonly int _total;
    private readonly List<string> _allTags;
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _majorityNote;
    private readonly string _originalNote;
    private string _newTag = "";
    private string _note;

    public AnnotationDialogVm(string header, IReadOnlyList<AnnotationTarget> targets, IEnumerable<string> availableTags)
    {
        Header = header;
        _total = targets.Count;
        IsMulti = _total > 1;
        _allTags = availableTags.ToList();

        // --- Tags: union across the selection, with per-tag counts. ---
        var counts = new Dictionary<string, int>(Ci);
        var casing = new Dictionary<string, string>(Ci);
        foreach (var target in targets)
            foreach (var tag in target.Tags.Select(t => t.Trim()).Where(t => t.Length > 0))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
                casing.TryAdd(tag, tag);
            }

        // Order chips: shared (on every item) first, then by recency of use — the
        // rank comes from availableTags, which the caller supplies newest-used-first.
        var rank = new Dictionary<string, int>(Ci);
        for (var i = 0; i < _allTags.Count; i++)
            rank.TryAdd(_allTags[i], i);
        Tags = new ObservableCollection<TagChipVm>(
            counts.OrderByDescending(kv => kv.Value >= _total)
                  .ThenBy(kv => rank.TryGetValue(kv.Key, out var r) ? r : int.MaxValue)
                  .ThenBy(kv => casing[kv.Key], Ci)
                  .Select(kv => new TagChipVm(casing[kv.Key], kv.Value, _total)));

        Suggestions = new ObservableCollection<string>(_allTags.Where(t => !HasChip(t)));

        // --- Notes: majority note; others are excluded from an edit. ---
        var notes = targets.Select(t => (t.Note ?? "").Trim()).ToList();
        var nonEmpty = notes.Where(n => n.Length > 0).ToList();
        _majorityNote = nonEmpty
            .GroupBy(n => n, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";
        _originalNote = _majorityNote;
        _note = _majorityNote;
        NoteExcludedCount = notes.Count(n => n.Length > 0 && !string.Equals(n, _majorityNote, StringComparison.Ordinal));

        AddTagCommand = new RelayCommand(() => { AddTag(_newTag); NewTag = ""; });
        PickTagCommand = new RelayCommand<string>(AddTag);
        RemoveTagCommand = new RelayCommand<TagChipVm>(RemoveTag);
    }

    public string Header { get; }
    public bool IsMulti { get; }
    public ObservableCollection<TagChipVm> Tags { get; }
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

    /// <summary>How many selected items have a note that differs from the majority (left untouched on save).</summary>
    public int NoteExcludedCount { get; }
    public bool NoteHasExclusions => NoteExcludedCount > 0;
    public string NoteExclusionText => NoteExcludedCount == 1
        ? "1 selected item has a different note — it won't be changed."
        : $"{NoteExcludedCount} selected items have different notes — they won't be changed.";

    private bool NoteChanged => !string.Equals(_note.Trim(), _originalNote.Trim(), StringComparison.Ordinal);

    public ICommand AddTagCommand { get; }
    public ICommand PickTagCommand { get; }
    public ICommand RemoveTagCommand { get; }

    /// <summary>Computes one target's resulting (tags, note) from the tracked edits.</summary>
    public (IReadOnlyList<string> Tags, string Note) ResolveFor(AnnotationTarget target) =>
        (AnnotationMerge.MergeTags(target.Tags, _added, _removed),
         AnnotationMerge.MergeNote(target.Note, _note, _majorityNote, NoteChanged));

    private void AddTag(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag))
            return;

        _removed.Remove(tag);
        var existing = Tags.FirstOrDefault(c => Ci.Equals(c.Name, tag));
        if (existing is null)
        {
            _added.Add(tag);
            Tags.Add(new TagChipVm(tag, _total, _total)); // applies to all → shared
        }
        else if (!existing.Shared)
        {
            // Typing/picking a partial tag promotes it to "apply to all".
            _added.Add(existing.Name);
            Tags[Tags.IndexOf(existing)] = new TagChipVm(existing.Name, _total, _total);
        }

        var suggestion = Suggestions.FirstOrDefault(s => Ci.Equals(s, tag));
        if (suggestion is not null)
            Suggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void RemoveTag(TagChipVm? chip)
    {
        if (chip is null)
            return;

        Tags.Remove(chip);
        _added.Remove(chip.Name);
        _removed.Add(chip.Name);

        if (_allTags.Any(t => Ci.Equals(t, chip.Name)) && !Suggestions.Any(s => Ci.Equals(s, chip.Name)))
        {
            Suggestions.Add(chip.Name);
            OnPropertyChanged(nameof(HasSuggestions));
        }
    }

    private bool HasChip(string tag) => Tags.Any(c => Ci.Equals(c.Name, tag));
}
