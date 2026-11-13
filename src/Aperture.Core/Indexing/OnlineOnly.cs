using Aperture.Core.Models;

namespace Aperture.Core.Indexing;

/// <summary>What a run does with the online-only files it would read (0.9.2 C1–C4).</summary>
public enum OnlineOnlyChoice
{
    /// <summary>None of them is read (downloaded); one not yet in the library stays out, one in it stays as it was.</summary>
    Skip,

    /// <summary>Every other file first, then them.</summary>
    Background,

    /// <summary>In order with the rest, as before 0.9.2.</summary>
    Now,
}

/// <summary>The question a run asks before it reads any file: this folder has <paramref name="Count"/> to download.</summary>
public sealed record OnlineOnlyQuestion(Root Root, int Count);
