using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Funcular.Data.Orm.Attributes;

namespace Aperture.Core.Storage;

// FunkyORM entity classes — one per table — that back the roots/items data access.
// They mirror the SQLite schema (which stores dates as INTEGER ticks, so these use `long`
// ticks rather than DateTime; the stores convert to/from the DateTime-based domain models).
// Suffixed "Row" so they don't collide with the domain models (Root, MediaItem, LibraryRow) —
// FunkyORM keys inference off class names, so duplicate names must be avoided.

/// <summary>The <c>roots</c> table.</summary>
[Table("roots")]
public class RootRow
{
    public long Id { get; set; }                                   // 'Id' is the auto-detected PK
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";
    public bool Included { get; set; } = true;                     // INTEGER 0/1 in SQLite
    [Column("color_tag")] public string? ColorTag { get; set; }
    [Column("added_ticks")] public long AddedTicks { get; set; }   // DateTime.Ticks (UTC)
}

/// <summary>The <c>items</c> table.</summary>
[Table("items")]
public class ItemRow
{
    public long Id { get; set; }
    [Column("root_id")] public long RootId { get; set; }
    [Column("rel_path")] public string RelPath { get; set; } = "";
    [Column("file_name")] public string FileName { get; set; } = "";
    public string Ext { get; set; } = "";
    [Column("size_bytes")] public long SizeBytes { get; set; }
    [Column("mtime_ticks")] public long MtimeTicks { get; set; }
    [Column("taken_ticks")] public long? TakenTicks { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Camera { get; set; }
    public int? Orientation { get; set; }
    [Column("indexed_ticks")] public long IndexedTicks { get; set; }
    public int Kind { get; set; }
}

/// <summary>The <c>annotations</c> table (tags + note per file, keyed by path).</summary>
[Table("annotations")]
public class AnnotationRow
{
    [Key] public string Path { get; set; } = "";     // string PK — [Key] since it's not the Id convention
    public string Tags { get; set; } = "[]";          // JSON array of tag strings
    public string Note { get; set; } = "";
    [Column("updated_ticks")] public long UpdatedTicks { get; set; }
}

/// <summary>The <c>tag_stats</c> table (per-tag recency/use, keyed by name).</summary>
[Table("tag_stats")]
public class TagStatRow
{
    [Key] public string Name { get; set; } = "";      // string PK
    [Column("last_used_ticks")] public long LastUsedTicks { get; set; }
    [Column("use_count")] public int UseCount { get; set; }
}

/// <summary>
/// Detail entity for the grid's union query: items plus their root's alias/color/path, filtered to
/// included roots. The <c>[RemoteProperty]</c> attributes let FunkyORM pull the joined columns
/// without hand-written JOIN SQL — the ORM's headline feature, dogfooded here.
/// </summary>
[Table("items")]
public class LibraryRowEntity : ItemRow
{
    // Redeclared so the FK can carry [RemoteLink] — root_id -> RootRow doesn't match the
    // "{Name}Id -> {Name}" convention (the entity is RootRow, not Root).
    [Column("root_id")]
    [RemoteLink(typeof(RootRow))]
    public new long RootId { get => base.RootId; set => base.RootId = value; }

    [RemoteProperty(typeof(RootRow), nameof(RootId), nameof(RootRow.Included))]
    public bool RootIncluded { get; set; }

    [RemoteProperty(typeof(RootRow), nameof(RootId), nameof(RootRow.Alias))]
    public string RootAlias { get; set; } = "";

    [RemoteProperty(typeof(RootRow), nameof(RootId), nameof(RootRow.ColorTag))]
    public string? RootColor { get; set; }

    [RemoteProperty(typeof(RootRow), nameof(RootId), nameof(RootRow.Path))]
    public string RootPath { get; set; } = "";
}
