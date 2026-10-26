namespace Aperture.Core.Models;

/// <summary>
/// A subfolder the user hid with its checkbox: it and every folder inside it are left out of the library's
/// union. <see cref="ItemCount"/> is how many items it holds (in included folders), for the folder tree.
/// </summary>
public sealed record HiddenFolder(long RootId, string RelDir, int ItemCount);

/// <summary>What the library shows (<see cref="Rows"/>), and the subfolders hidden from it.</summary>
public sealed record LibraryContents(List<LibraryRow> Rows, IReadOnlyList<HiddenFolder> Hidden);
