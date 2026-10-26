using System.IO;

namespace Aperture.App.ViewModels;

/// <summary>A folder to add to the library: its path, its name in the tree, and whether to index its subfolders.</summary>
public sealed record FolderToAdd(string Path, string Alias, bool IncludeSubfolders);

/// <summary>Decides which chosen folders to add, and asks — once — whether to include their subfolders.</summary>
public static class FolderAdditions
{
    /// <summary>
    /// The folders among <paramref name="chosen"/> to add: each once, skipping any already added and any that doesn't
    /// exist. If there is at least one, <paramref name="askIncludeSubfolders"/> is asked once, for all of them; its
    /// answer applies to each, and <c>null</c> (cancelled) adds none.
    /// </summary>
    public static IReadOnlyList<FolderToAdd> Plan(
        IEnumerable<string> chosen, IEnumerable<string> alreadyAdded, Func<IReadOnlyList<string>, bool?> askIncludeSubfolders)
    {
        var taken = new HashSet<string>(alreadyAdded.Select(Full), StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var path in chosen)
        {
            var full = Full(path);
            if (Directory.Exists(full) && taken.Add(full))
                folders.Add(full);
        }
        if (folders.Count == 0 || askIncludeSubfolders(folders) is not { } includeSubfolders)
            return [];
        return [.. folders.Select(f => new FolderToAdd(f, AliasFor(f), includeSubfolders))];
    }

    /// <summary>A folder's name in the tree: its own name, or the whole path for a drive's root.</summary>
    public static string AliasFor(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // Full path without a trailing separator (a drive root keeps its own: "C:\").
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
