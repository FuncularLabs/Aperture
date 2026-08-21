using System.Text.Json;

namespace Aperture.Core.Updates;

/// <summary>One published GitHub release relevant to updating.</summary>
public sealed record ReleaseInfo(
    string Tag,
    UpdateVersion Version,
    bool Prerelease,
    string HtmlUrl,
    string? AssetName,
    string? AssetUrl,
    long AssetSize);

/// <summary>What an update check found: the newest stable and the newest
/// prerelease (either may be null when none exists or parsing failed).</summary>
public sealed record UpdateCheck(ReleaseInfo? Stable, ReleaseInfo? PrereleaseRelease);

/// <summary>
/// Pure parsing/selection over the GitHub releases JSON — separated from HTTP
/// so it's unit-testable. Drafts are ignored; the newest stable and newest
/// prerelease are chosen by VERSION order (not list order), and only releases
/// carrying the single-file win-x64 asset the release workflow publishes count.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>
    /// Aperture publish asset names look like <c>Aperture-v0.8.0-beta1-win-x64.exe</c>.
    /// Not the Markdown Midget <c>-win-x64-net10.exe</c> suffix.
    /// </summary>
    public const string AssetSuffix = "-win-x64.exe";

    public static UpdateCheck Select(string releasesJson)
    {
        var releases = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(releasesJson);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var version = UpdateVersion.Parse(tag);
            if (tag is null || version is null) continue;
            // Either signal is enough. The GitHub flag is set by hand at publish
            // time and has been wrong (Aperture's beta tags have shipped with
            // prerelease:false). Mistaking a beta for a stable release is the one
            // direction that actually hurts — it would be offered to everyone.
            var pre = (el.TryGetProperty("prerelease", out var p) && p.GetBoolean())
                      || version.IsPrerelease;
            var html = el.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            string? assetName = null, assetUrl = null;
            long assetSize = 0;
            if (el.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    assetName = name;
                    assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    break;
                }
            }
            if (assetUrl is null) continue;
            releases.Add(new ReleaseInfo(tag, version, pre, html, assetName, assetUrl, assetSize));
        }

        var stable = releases.Where(r => !r.Prerelease).OrderByDescending(r => r.Version).FirstOrDefault();
        var prerelease = releases.Where(r => r.Prerelease).OrderByDescending(r => r.Version).FirstOrDefault();
        return new UpdateCheck(stable, prerelease);
    }
}
