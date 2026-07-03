using Microsoft.Data.Sqlite;

namespace Reel.Core.Storage;

/// <summary>
/// Owns the two SQLite files Reel uses and hands out configured connections.
///
/// Metadata (roots, items) and thumbnails live in separate files on purpose:
/// thumbnail BLOBs would otherwise bloat the metadata DB's page cache and slow
/// down the queries that drive the grid. Splitting keeps metadata scans fast.
/// </summary>
public sealed class ReelDatabase
{
    public const string MetadataFileName = "reel.db";
    public const string ThumbnailFileName = "thumbs.db";

    public string Directory { get; }
    public string MetadataPath { get; }
    public string ThumbnailPath { get; }

    private readonly string _metadataConnectionString;
    private readonly string _thumbnailConnectionString;

    public ReelDatabase(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);

        MetadataPath = Path.Combine(directory, MetadataFileName);
        ThumbnailPath = Path.Combine(directory, ThumbnailFileName);

        _metadataConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = MetadataPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        _thumbnailConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ThumbnailPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Opens a metadata connection with WAL + pragmas applied.</summary>
    public SqliteConnection OpenMetadata() => Open(_metadataConnectionString);

    /// <summary>Opens a thumbnail connection with WAL + pragmas applied.</summary>
    public SqliteConnection OpenThumbnails() => Open(_thumbnailConnectionString);

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA foreign_keys=ON;" +
            "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Creates the schema in both files if it does not already exist.</summary>
    public void Initialize()
    {
        using (var meta = OpenMetadata())
        using (var cmd = meta.CreateCommand())
        {
            cmd.CommandText = MetadataSchema;
            cmd.ExecuteNonQuery();
        }

        using (var thumbs = OpenThumbnails())
        using (var cmd = thumbs.CreateCommand())
        {
            cmd.CommandText = ThumbnailSchema;
            cmd.ExecuteNonQuery();
        }
    }

    private const string MetadataSchema = """
        CREATE TABLE IF NOT EXISTS roots (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            path        TEXT    NOT NULL UNIQUE,
            alias       TEXT    NOT NULL,
            included    INTEGER NOT NULL DEFAULT 1,
            color_tag   TEXT,
            added_ticks INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS items (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            root_id       INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            rel_path      TEXT    NOT NULL,
            file_name     TEXT    NOT NULL,
            ext           TEXT    NOT NULL,
            size_bytes    INTEGER NOT NULL,
            mtime_ticks   INTEGER NOT NULL,
            taken_ticks   INTEGER,
            width         INTEGER,
            height        INTEGER,
            camera        TEXT,
            orientation   INTEGER,
            indexed_ticks INTEGER NOT NULL,
            UNIQUE(root_id, rel_path)
        );

        CREATE INDEX IF NOT EXISTS ix_items_root ON items(root_id);
        CREATE INDEX IF NOT EXISTS ix_items_date ON items(taken_ticks, mtime_ticks);
        """;

    // Thumbnails cannot reference items across DB files, so cascade deletes are
    // done in code when items are removed.
    private const string ThumbnailSchema = """
        CREATE TABLE IF NOT EXISTS thumbs (
            item_id         INTEGER NOT NULL,
            size            INTEGER NOT NULL,
            src_mtime_ticks INTEGER NOT NULL,
            width           INTEGER NOT NULL,
            height          INTEGER NOT NULL,
            data            BLOB    NOT NULL,
            bytes           INTEGER NOT NULL,
            created_ticks   INTEGER NOT NULL,
            last_used_ticks INTEGER NOT NULL,
            PRIMARY KEY(item_id, size)
        );
        """;
}
