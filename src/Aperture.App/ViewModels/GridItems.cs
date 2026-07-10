using Aperture.App.Mvvm;

namespace Aperture.App.ViewModels;

/// <summary>Common marker for anything that can appear in the grid (media tiles and folder tiles).</summary>
public interface IGridItem
{
    SectionVm? Section { get; }
}

/// <summary>A subfolder shown as a navigable tile in the grid.</summary>
public sealed class FolderTileVm : ObservableObject, IGridItem
{
    public required string Name { get; init; }
    public required long RootId { get; init; }

    /// <summary>This folder's directory path relative to its root ("" would be the root itself).</summary>
    public required string RelDir { get; init; }

    public required string FullPath { get; init; }

    /// <summary>Recursive count of media items under this folder.</summary>
    public int Count { get; init; }

    public SectionVm? Section { get; set; }
}

/// <summary>One clickable segment in the location breadcrumb.</summary>
public sealed class BreadcrumbVm
{
    public required string Label { get; init; }
    public required long? RootId { get; init; }
    public required string RelDir { get; init; }
    public bool IsLast { get; set; }
}
