namespace Aperture.Core.Library;

/// <summary>
/// Paths compared as the library's columns compare them (SQLite's NOCASE): only ASCII letters' case is folded (0.9.2
/// review round 22). .NET's OrdinalIgnoreCase folds hundreds of letters that NTFS keeps apart (σ/ς, µ/μ), so two folders,
/// or two files, were taken for one: files went into a folder that wasn't picked, a selection lost one of two, a move
/// was "already in that folder". A case only Windows folds (É/é) is taken here for two names: the safe side (a question,
/// or a move made as the folder is named), never one file taken for another.
/// </summary>
public sealed class AsciiCase : StringComparer
{
    public static readonly AsciiCase Comparer = new();

    private AsciiCase()
    {
    }

    /// <summary>Whether two paths are one, ASCII letters' case aside.</summary>
    public static bool Same(string? a, string? b) => Comparer.Equals(a, b);

    /// <summary>Whether <paramref name="path"/> begins with <paramref name="prefix"/>, ASCII letters' case aside.</summary>
    public static bool StartsWith(string path, string prefix) =>
        path.Length >= prefix.Length && SameChars(path.AsSpan(0, prefix.Length), prefix);

    public override bool Equals(string? x, string? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && SameChars(x, y));

    public override int GetHashCode(string obj)
    {
        var hash = new HashCode();
        foreach (var c in obj)
            hash.Add(Fold(c));
        return hash.ToHashCode();
    }

    public override int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (Fold(x[i]) != Fold(y[i]))
                return Fold(x[i]).CompareTo(Fold(y[i]));
        return x.Length.CompareTo(y.Length);
    }

    private static bool SameChars(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
            if (Fold(a[i]) != Fold(b[i]))
                return false;
        return true;
    }

    private static char Fold(char c) => c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
}
