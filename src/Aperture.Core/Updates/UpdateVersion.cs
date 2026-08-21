namespace Aperture.Core.Updates;

/// <summary>
/// App version scheme: <c>major.minor.patch[-prereleaseN]</c> (tags carry a leading
/// <c>v</c>). Ordering follows SemVer's core rule — numeric first, and a prerelease
/// sorts BELOW its own stable (0.8.0-beta1 &lt; 0.8.0) — with the simple prerelease
/// tail this repo uses (beta1 &lt; beta2 &lt; rc1 compared as label + number, labels ordinal).
/// </summary>
public sealed record UpdateVersion(Version Numeric, string? Prerelease) : IComparable<UpdateVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static UpdateVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        // Drop SemVer build metadata: 0.8.0-beta1+abc1234 is the same version as
        // 0.8.0-beta1. An unreadable running version pushes the update check onto
        // its fallback paths, so it's worth not creating one.
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var numericPart = dash < 0 ? s : s[..dash];
        var pre = dash < 0 ? null : s[(dash + 1)..];
        if (string.IsNullOrWhiteSpace(pre)) pre = null;
        if (!Version.TryParse(numericPart, out var v)) return null;
        // Normalize to 3 components so 0.8 == 0.8.0.
        v = new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        return new UpdateVersion(v, pre);
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        var n = Numeric.CompareTo(other.Numeric);
        if (n != 0) return n;
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;
        return ComparePre(Prerelease, other.Prerelease);
    }

    private static int ComparePre(string a, string b)
    {
        var (la, na) = SplitPre(a);
        var (lb, nb) = SplitPre(b);
        var l = string.Compare(la, lb, StringComparison.OrdinalIgnoreCase);
        return l != 0 ? l : na.CompareTo(nb);
    }

    private static (string Label, int Num) SplitPre(string p)
    {
        var digits = new string(p.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var label = p[..(p.Length - digits.Length)];
        return (label, digits.Length > 0 && int.TryParse(digits, out var n) ? n : 0);
    }

    public override string ToString() => Prerelease is null ? Numeric.ToString() : $"{Numeric}-{Prerelease}";
}
