using System.Text.Json;
using System.Text.Json.Serialization;
using Reel.Core.Models;
using Reel.Core.Storage;

namespace Reel.Core.Annotations;

/// <summary>
/// Serialises tags + notes to a portable JSON document and merges them back in.
///
/// Each entry carries both the original absolute <see cref="AnnotationEntry.Path"/>
/// and a root-relative descriptor (<see cref="AnnotationEntry.RootAlias"/> +
/// <see cref="AnnotationEntry.RelPath"/>) so annotations survive a move to another
/// machine or user profile where the same folders live at a different absolute
/// location. Import is an <em>upsert</em>: tags are unioned into any existing
/// annotation and a note fills in only when the local note is empty (a differing
/// note is appended, never overwritten). Nothing is ever deleted by an import.
/// </summary>
public sealed class AnnotationTransfer(AnnotationStore annotations, RootStore roots)
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes every non-empty annotation to <paramref name="filePath"/>. Returns the entry count.</summary>
    public int Export(string filePath)
    {
        var allRoots = roots.GetAll();
        var entries = new List<AnnotationEntry>();
        foreach (var (path, annotation) in annotations.GetAll())
        {
            if (annotation.IsEmpty)
                continue;
            var (alias, rel) = ToRootRelative(path, allRoots);
            entries.Add(new AnnotationEntry
            {
                Path = path,
                RootAlias = alias,
                RelPath = rel,
                Tags = [.. annotation.Tags],
                Note = annotation.Note,
            });
        }

        var document = new AnnotationDocument
        {
            Entries = [.. entries.OrderBy(e => e.Path, Ci)],
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(document, JsonOptions));
        return entries.Count;
    }

    /// <summary>Merges a portable document into the store. Returns a summary of what was applied.</summary>
    public ImportSummary Import(string filePath)
    {
        var document = JsonSerializer.Deserialize<AnnotationDocument>(File.ReadAllText(filePath), JsonOptions)
                       ?? new AnnotationDocument();
        var allRoots = roots.GetAll();
        int applied = 0, unresolved = 0, skipped = 0;

        foreach (var entry in document.Entries ?? [])
        {
            var tags = (entry.Tags ?? [])
                .Select(t => t?.Trim() ?? "")
                .Where(t => t.Length > 0)
                .ToList();
            var note = entry.Note?.Trim() ?? "";
            if (tags.Count == 0 && note.Length == 0)
            {
                skipped++;
                continue;
            }

            var target = ResolveLocalPath(entry, allRoots);
            if (target is null)
            {
                unresolved++;
                continue;
            }

            var existing = annotations.Get(target);
            var mergedTags = existing.Tags
                .Concat(tags)
                .Distinct(Ci)
                .ToList();
            var mergedNote = MergeNote(existing.Note, note);
            annotations.Save(target, mergedTags, mergedNote);
            applied++;
        }

        return new ImportSummary(applied, unresolved, skipped);
    }

    /// <summary>
    /// Picks the best local path for an imported entry, preferring one that pairs
    /// with content the user actually has:
    ///   1. the file under a root whose alias matches, if it exists on disk;
    ///   2. the file under <em>any</em> root at the same relative path (survives an alias rename);
    ///   3. the original absolute path, if that file still exists (same-machine restore);
    ///   4. the alias-matched root-relative path even if absent (folder is known, content may re-index);
    ///   5. otherwise unresolved.
    /// </summary>
    private static string? ResolveLocalPath(AnnotationEntry entry, IReadOnlyList<Root> allRoots)
    {
        var rel = NormalizeRel(entry.RelPath);
        Root? aliasRoot = null;
        if (!string.IsNullOrEmpty(entry.RootAlias))
            aliasRoot = allRoots.FirstOrDefault(r => Ci.Equals(r.Alias, entry.RootAlias));

        if (rel.Length > 0)
        {
            if (aliasRoot is not null)
            {
                var underAlias = Path.Combine(aliasRoot.Path, rel);
                if (File.Exists(underAlias))
                    return underAlias;                                   // (1)
            }

            foreach (var root in allRoots)                               // (2)
            {
                var candidate = Path.Combine(root.Path, rel);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        if (!string.IsNullOrEmpty(entry.Path) && File.Exists(entry.Path))
            return entry.Path;                                           // (3)

        if (aliasRoot is not null && rel.Length > 0)
            return Path.Combine(aliasRoot.Path, rel);                    // (4)

        return null;                                                     // (5)
    }

    /// <summary>Root alias + path relative to it, if <paramref name="path"/> lives under a known root.</summary>
    private static (string? Alias, string? Rel) ToRootRelative(string path, IReadOnlyList<Root> allRoots)
    {
        // Longest matching root wins, so a nested root beats its parent.
        Root? best = null;
        foreach (var root in allRoots)
        {
            if (!IsUnder(path, root.Path))
                continue;
            if (best is null || root.Path.Length > best.Path.Length)
                best = root;
        }
        if (best is null)
            return (null, null);

        var rel = path[best.Path.Length..].TrimStart('\\', '/');
        return (best.Alias, rel);
    }

    private static bool IsUnder(string path, string rootPath)
    {
        var root = rootPath.TrimEnd('\\', '/');
        if (path.Length <= root.Length)
            return Ci.Equals(path, root);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && (path[root.Length] == '\\' || path[root.Length] == '/');
    }

    private static string NormalizeRel(string? rel) =>
        string.IsNullOrEmpty(rel)
            ? ""
            : rel.Replace('/', Path.DirectorySeparatorChar)
                 .Replace('\\', Path.DirectorySeparatorChar)
                 .TrimStart(Path.DirectorySeparatorChar);

    private static string MergeNote(string existing, string incoming)
    {
        existing = existing?.Trim() ?? "";
        incoming = incoming?.Trim() ?? "";
        if (incoming.Length == 0)
            return existing;
        if (existing.Length == 0)
            return incoming;
        if (Ci.Equals(existing, incoming) || existing.Contains(incoming, StringComparison.OrdinalIgnoreCase))
            return existing;                       // already present — keep import idempotent
        return existing + "\n" + incoming;
    }
}

/// <summary>Top-level shape of an exported tags-and-notes file.</summary>
public sealed class AnnotationDocument
{
    public int Version { get; init; } = 1;
    public List<AnnotationEntry> Entries { get; init; } = [];
}

/// <summary>One file's tags + note, with both absolute and root-relative locators.</summary>
public sealed class AnnotationEntry
{
    public string Path { get; init; } = "";
    public string? RootAlias { get; init; }
    public string? RelPath { get; init; }
    public List<string> Tags { get; init; } = [];
    public string Note { get; init; } = "";
}

/// <summary>Counts from an import: applied (upserted), unresolved (no local match), skipped (empty).</summary>
public readonly record struct ImportSummary(int Applied, int Unresolved, int Skipped);
