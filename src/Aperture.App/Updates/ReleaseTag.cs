using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Aperture.App.Updates;

/// <summary>
/// Whether a Version that just landed on <c>main</c> should become a <c>v*</c>
/// git tag. Comparison is <see cref="UpdateVersion"/> — the same SemVer rules
/// as the in-app updater (numeric first, prerelease below its own stable).
/// </summary>
internal static class ReleaseTag
{
    public const string AppCsprojRelativePath = "src/Aperture.App/Aperture.App.csproj";
    public const string DirectoryBuildPropsRelativePath = "Directory.Build.props";

    /// <summary>
    /// Reads <c>&lt;Version&gt;</c> from the app csproj (source of truth for
    /// the shipped assembly). Falls back to <c>Directory.Build.props</c> if
    /// the csproj has no Version — MSBuild would inherit it from there.
    /// </summary>
    public static string? ReadLandedVersion(string repoRoot)
    {
        var csproj = ReadVersionElement(Path.Combine(repoRoot, AppCsprojRelativePath));
        if (!string.IsNullOrWhiteSpace(csproj)) return csproj;
        var props = ReadVersionElement(Path.Combine(repoRoot, DirectoryBuildPropsRelativePath));
        return string.IsNullOrWhiteSpace(props) ? null : props;
    }

    public static string? ReadVersionElement(string path)
    {
        if (!File.Exists(path)) return null;
        var doc = XDocument.Load(path);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Version")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
    }

    /// <summary>
    /// Among existing <c>v*</c> tags, the one that compares greatest under
    /// <see cref="UpdateVersion"/>, or <c>null</c> if none parse.
    /// </summary>
    public static string? LatestTag(IEnumerable<string> existingTags)
    {
        string? bestName = null;
        UpdateVersion? best = null;
        foreach (var raw in existingTags)
        {
            if (!TryVTag(raw, out var name, out var parsed)) continue;
            if (best is null || parsed.CompareTo(best) > 0)
            {
                best = parsed;
                bestName = name;
            }
        }
        return bestName;
    }

    /// <summary>
    /// Returns <c>v{version}</c> when <paramref name="landedVersion"/> is
    /// strictly greater than every existing <c>v*</c> tag and that tag name
    /// is not already present. Equal, lower, unparseable, or already-tagged
    /// versions return <c>null</c>. Never force-updates a tag.
    /// </summary>
    public static string? TagToCreate(string? landedVersion, IEnumerable<string> existingTags)
    {
        var raw = landedVersion?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var landed = UpdateVersion.Parse(raw);
        if (landed is null) return null;

        var wanted = TagNameFor(raw);
        if (wanted is null) return null;

        var tags = existingTags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (tags.Any(t => string.Equals(t, wanted, StringComparison.Ordinal)))
            return null;

        var latestName = LatestTag(tags);
        if (latestName is not null)
        {
            var latest = UpdateVersion.Parse(latestName);
            if (latest is not null && landed.CompareTo(latest) <= 0)
                return null;
        }

        return wanted;
    }

    /// <summary>
    /// Tag name from a Version string: leading <c>v</c> is normalized, build
    /// metadata is dropped so it matches <see cref="UpdateVersion.Parse"/>.
    /// The prerelease suffix is kept as written (e.g. <c>0.8.1-beta1</c> →
    /// <c>v0.8.1-beta1</c>), not rewritten to the 3-component ToString form.
    /// </summary>
    public static string? TagNameFor(string versionText)
    {
        var s = versionText.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        s = s.Trim();
        return s.Length == 0 ? null : $"v{s}";
    }

    private static bool TryVTag(string? raw, out string name, out UpdateVersion parsed)
    {
        name = raw?.Trim() ?? "";
        parsed = null!;
        if (name.Length == 0) return false;
        if (!(name.StartsWith('v') || name.StartsWith('V'))) return false;
        var v = UpdateVersion.Parse(name);
        if (v is null) return false;
        parsed = v;
        return true;
    }
}
