namespace Aperture.Core.Models;

public enum IndexPhase
{
    Scanning,
    Indexing,
    Pruning,
    Done,
}

/// <summary>Progress snapshot reported by the indexer during a run.</summary>
public sealed class IndexProgress
{
    public required string RootAlias { get; init; }
    public IndexPhase Phase { get; init; }
    /// <summary>Files done so far; while <see cref="IndexPhase.Scanning"/>, the files found so far.</summary>
    public int Processed { get; init; }

    /// <summary>The files to process, once the scan has counted them (0 while scanning).</summary>
    public int Total { get; init; }

    /// <summary>
    /// How many of the files found are online only (cloud placeholders), so are downloaded as they're indexed.
    /// </summary>
    public int OnlineOnly { get; init; }

    public string? CurrentFile { get; init; }
}

/// <summary>Outcome of a single <c>IndexRoot</c> run.</summary>
public sealed class IndexResult
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    public int Removed { get; init; }
    public int ThumbnailsGenerated { get; init; }
    public int Failed { get; init; }

    public int TotalOnDisk => Added + Updated + Skipped;
}
