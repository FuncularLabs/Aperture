using Aperture.Core.Models;

namespace Aperture.Core.Formatting;

/// <summary>
/// What the window says while folders are indexed: a short status beside a folder in the tree, a headline for the
/// status bar and the indexing card, and a friendly line under it (0.9.1 batch, item 4). Numbers are written in the
/// user's own format (the current culture), as everywhere else in the window.
/// </summary>
public static class IndexingMessage
{
    /// <summary>The short status beside a folder in the tree while it's indexed; empty once it's done.</summary>
    public static string RootStatus(IndexProgress p) => p.Phase switch
    {
        IndexPhase.Scanning => p.Processed > 0 ? $"counting… {p.Processed:n0}" : "counting…",
        IndexPhase.Indexing => $"indexing {p.Processed:n0} / {p.Total:n0}",
        IndexPhase.Pruning => "cleaning up…",
        _ => "",
    };

    /// <summary>
    /// The headline: for one folder, what its run is doing (<paramref name="progress"/> is its latest report, or null
    /// if it hasn't reported yet); for several, how many are being indexed; empty for none.
    /// </summary>
    public static string Headline(string? alias, IndexProgress? progress, int foldersIndexing)
    {
        if (foldersIndexing <= 0)
            return "";
        if (foldersIndexing > 1)
            return $"Indexing {foldersIndexing:n0} folders…";
        return progress?.Phase switch
        {
            IndexPhase.Scanning when progress.Processed > 0 =>
                $"Counting files in {alias}… {progress.Processed:n0} so far",
            IndexPhase.Scanning => $"Counting files in {alias}…",
            IndexPhase.Indexing =>
                $"Indexing {alias}: {progress.Processed:n0} of {progress.Total:n0} file{(progress.Total == 1 ? "" : "s")}",
            IndexPhase.Pruning => $"{alias}: cleaning up…",
            _ => $"Indexing {alias}…",
        };
    }

    /// <summary>
    /// The headline and the friendly line for the runs in progress (each folder's alias and its latest report, or null
    /// if it hasn't reported yet): one folder's progress, or how many; how many online-only files they'll read in all.
    /// </summary>
    public static (string Headline, string Note) For(IReadOnlyList<(string Alias, IndexProgress? Progress)> runs)
    {
        var downloading = runs.Where(r => r.Progress?.OnlineOnly > 0).ToList();
        return (runs.Count == 1 ? Headline(runs[0].Alias, runs[0].Progress, 1) : Headline(null, null, runs.Count),
                Note(downloading.Sum(r => r.Progress!.OnlineOnly), runs.Count > 0,
                     afterTheRest: downloading.Count > 0 && downloading.All(r => r.Progress!.OnlineOnlyLast)));
    }

    /// <summary>
    /// The friendly line while anything is indexed, saying too how many of the files are online-only (downloaded as
    /// they're indexed, which is slower — or, <paramref name="afterTheRest"/>, after every other file, as the user
    /// asked: 0.9.2 C3); empty otherwise.
    /// </summary>
    public static string Note(int onlineOnly, bool indexing, bool afterTheRest = false)
    {
        if (!indexing)
            return "";
        const string patience = "This might take a while. You can keep browsing.";
        return (onlineOnly, afterTheRest) switch
        {
            (<= 0, _) => patience,
            (1, true) => patience + " 1 of the files is online-only; it's downloaded after the others.",
            (_, true) => patience + $" {onlineOnly:n0} of the files are online-only; they're downloaded after the others.",
            (1, false) => patience + " 1 of the files is online-only, so it's downloaded as it's indexed.",
            _ => patience + $" {onlineOnly:n0} of the files are online-only, so each one is downloaded as it's indexed.",
        };
    }
}
