using System.Text.RegularExpressions;
using Aperture.Core.Models;

namespace Aperture.Core.Formatting;

/// <summary>
/// A Gmail-style search over the library. Space-separated terms are AND-ed.
/// Supported operators: <c>tag:x</c>, <c>note:x</c>, <c>has:tag</c>, <c>has:note</c>,
/// <c>is:video</c>/<c>is:image</c>/<c>is:tagged</c>, <c>type:mp4</c>, <c>name:x</c>,
/// <c>camera:x</c>, <c>folder:x</c>. Bare words match the file name, folder alias,
/// camera, tags, or note. Values may be quoted: <c>tag:"date night"</c>.
/// </summary>
public sealed partial class SearchQuery
{
    private readonly List<Term> _terms;

    private SearchQuery(List<Term> terms) => _terms = terms;

    public bool IsEmpty => _terms.Count == 0;

    public static SearchQuery Parse(string query)
    {
        var terms = new List<Term>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (Match m in TokenRegex().Matches(query))
            {
                var field = m.Groups["field"].Success ? m.Groups["field"].Value.ToLowerInvariant() : null;
                var value = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;
                if (value.Length == 0 && field is null)
                    continue;
                terms.Add(Classify(field, value));
            }
        }
        return new SearchQuery(terms);
    }

    public bool Matches(LibraryRow row, Annotation annotation)
    {
        // tag: terms are OR-ed with each other (match ANY), then AND-ed with the
        // rest — so `tag:a tag:b note:x` means (has a OR has b) AND note contains x.
        var hasTagTerm = false;
        var anyTagMatched = false;
        foreach (var term in _terms)
        {
            if (term.Kind == TermKind.Tag)
            {
                hasTagTerm = true;
                anyTagMatched |= term.Matches(row, annotation);
                continue;
            }
            if (!term.Matches(row, annotation))
                return false;
        }
        return !hasTagTerm || anyTagMatched;
    }

    private static Term Classify(string? field, string value) => field switch
    {
        "tag" or "tags" => new Term(TermKind.Tag, Annotations.TagNormalizer.Normalize(value)),
        "note" or "notes" => new Term(TermKind.Note, value),
        "has" => new Term(TermKind.Has, value.ToLowerInvariant()),
        "is" => new Term(TermKind.Is, value.ToLowerInvariant()),
        "type" or "ext" => new Term(TermKind.Type, value.TrimStart('.').ToLowerInvariant()),
        "name" or "file" => new Term(TermKind.Name, value),
        "camera" => new Term(TermKind.Camera, value),
        "folder" or "alias" => new Term(TermKind.Alias, value),
        _ => new Term(TermKind.Free, field is null ? value : $"{field}:{value}"),
    };

    private enum TermKind { Free, Tag, Note, Has, Is, Type, Name, Camera, Alias }

    private sealed record Term(TermKind Kind, string Value)
    {
        public bool Matches(LibraryRow row, Annotation annotation)
        {
            var item = row.Item;
            var v = Value;
            return Kind switch
            {
                TermKind.Free =>
                    Has(item.FileName, v) || Has(row.RootAlias, v) || Has(item.Camera, v)
                    || annotation.Tags.Any(t => Has(t, v)) || Has(annotation.Note, v),
                TermKind.Tag => annotation.Tags.Any(t => Has(t, v)),
                TermKind.Note => Has(annotation.Note, v),
                TermKind.Name => Has(item.FileName, v),
                TermKind.Camera => Has(item.Camera, v),
                TermKind.Alias => Has(row.RootAlias, v),
                TermKind.Type => string.Equals(item.Ext, "." + v, StringComparison.OrdinalIgnoreCase),
                TermKind.Has => v switch
                {
                    "tag" or "tags" => annotation.Tags.Count > 0,
                    "note" or "notes" => !string.IsNullOrWhiteSpace(annotation.Note),
                    _ => false,
                },
                TermKind.Is => v switch
                {
                    "video" => item.IsVideo,
                    "image" or "photo" => !item.IsVideo,
                    "tagged" => annotation.Tags.Count > 0,
                    "noted" => !string.IsNullOrWhiteSpace(annotation.Note),
                    _ => false,
                },
                _ => false,
            };
        }

        private static bool Has(string? haystack, string needle) =>
            haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("""(?:(?<field>[A-Za-z]+):)?(?:"(?<q>[^"]*)"|(?<v>\S+))""", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
