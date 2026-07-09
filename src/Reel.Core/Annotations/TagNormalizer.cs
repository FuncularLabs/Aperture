using System.Text.RegularExpressions;

namespace Reel.Core.Annotations;

/// <summary>
/// Normalizes tags to a single hyphenated token — multi-word tags use hyphens, never
/// spaces (e.g. "date night" → "date-night"). Applied on entry, on save, and to
/// <c>tag:</c> search values so everything matches.
/// </summary>
public static partial class TagNormalizer
{
    public static string Normalize(string tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0)
            return "";
        t = WhitespaceRuns().Replace(t, "-"); // spaces/tabs → hyphen
        t = HyphenRuns().Replace(t, "-");     // collapse repeats
        return t.Trim('-');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    [GeneratedRegex("-{2,}")]
    private static partial Regex HyphenRuns();
}
