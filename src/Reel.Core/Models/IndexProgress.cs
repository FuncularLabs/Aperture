namespace Reel.Core.Models;

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
    public int Processed { get; init; }
    public int Total { get; init; }
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
