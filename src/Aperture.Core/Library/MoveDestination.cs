using Aperture.Core.Models;

namespace Aperture.Core.Library;

/// <summary>A folder of another library, as Move to… needs it: its path, and whether it indexes its subfolders.</summary>
public sealed record LibraryFolder(string Path, bool Recursive);

/// <summary>Another library (not the one open) and its folders, read from its own files.</summary>
public sealed record OtherLibrary(string Name, IReadOnlyList<LibraryFolder> Folders);

public enum MoveDestinationKind
{
    /// <summary>A folder this library indexes: no question.</summary>
    ThisLibrary,

    /// <summary>Under one of this library's folders, which doesn't include its subfolders: not indexed.</summary>
    NotIndexedHere,

    /// <summary>A folder another library indexes.</summary>
    OtherLibrary,

    /// <summary>In no library.</summary>
    NoLibrary,
}

/// <summary>
/// Where a Move to… folder is (0.9.2 M2). A folder this library indexes is one of its folders, or inside one that
/// includes its subfolders — hidden subfolders and unticked folders count — by the deepest such folder;
/// <see cref="FolderPath"/> is then built from that folder's own path, as a row's full path is. Folders are compared by
/// their full paths, in any case; a folder reached by another name (a junction, a mapped drive) isn't recognised.
/// </summary>
public sealed record MoveDestination(MoveDestinationKind Kind, string FolderPath)
{
    public long? RootId { get; init; }

    /// <summary>The folder's path inside <see cref="RootId"/>'s folder ("" for that folder itself).</summary>
    public string RelDir { get; init; } = "";

    /// <summary>For <see cref="MoveDestinationKind.NotIndexedHere"/>: the folder of this library it's under.</summary>
    public Root? UnderRoot { get; init; }

    /// <summary>For <see cref="MoveDestinationKind.OtherLibrary"/>: that library's name.</summary>
    public string? LibraryName { get; init; }

    public static MoveDestination Classify(string folder, IEnumerable<Root> roots, IEnumerable<OtherLibrary>? others)
    {
        var path = Normalize(folder);
        Root? indexedBy = null;
        var indexedByLength = -1;
        var rel = "";
        Root? under = null;
        foreach (var root in roots)
        {
            var rootPath = Normalize(root.Path);
            if (!Contains(rootPath, path, out var inside))
                continue;
            if (inside.Length == 0 || root.Recursive)
            {
                if (rootPath.Length > indexedByLength)
                    (indexedBy, indexedByLength, rel) = (root, rootPath.Length, inside);
            }
            else
                under ??= root;
        }
        if (indexedBy is not null)
        {
            return new MoveDestination(MoveDestinationKind.ThisLibrary,
                rel.Length == 0 ? indexedBy.Path : Path.Combine(indexedBy.Path, rel)) { RootId = indexedBy.Id, RelDir = rel };
        }

        foreach (var library in others ?? [])
        foreach (var libraryFolder in library.Folders)
        {
            if (Contains(Normalize(libraryFolder.Path), path, out var inside) && (inside.Length == 0 || libraryFolder.Recursive))
                return new MoveDestination(MoveDestinationKind.OtherLibrary, path) { LibraryName = library.Name };
        }

        return under is not null
            ? new MoveDestination(MoveDestinationKind.NotIndexedHere, path) { UnderRoot = under }
            : new MoveDestination(MoveDestinationKind.NoLibrary, path);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Whether child is parent or inside it, and if so its path inside it.
    private static bool Contains(string parent, string child, out string inside)
    {
        inside = "";
        if (AsciiCase.Same(parent, child)) // as the library's columns compare, not .NET's wider fold (round 22)
            return true;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        if (!AsciiCase.StartsWith(child, prefix))
            return false;
        inside = child[prefix.Length..];
        return true;
    }
}
