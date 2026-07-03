using Reel.Core.Indexing;
using Reel.Core.Media;
using Reel.Core.Models;
using Reel.Core.Storage;

namespace Reel.Core.Tests.Support;

/// <summary>
/// A ready-to-use database + indexer wired to a temp data dir, plus a temp
/// "library" dir to drop test images into. Disposes both.
/// </summary>
public sealed class ReelScope : IDisposable
{
    private readonly TempDir _dataDir = new();

    public TempDir Library { get; } = new();
    public ReelDatabase Database { get; }
    public RootStore Roots { get; }
    public ItemStore Items { get; }
    public ThumbnailStore Thumbnails { get; }
    public Indexer Indexer { get; }

    public ReelScope()
    {
        Database = new ReelDatabase(_dataDir.Path);
        Database.Initialize();
        Roots = new RootStore(Database);
        Items = new ItemStore(Database);
        Thumbnails = new ThumbnailStore(Database);
        Indexer = new Indexer(Database, new ThumbnailGenerator(), new MetadataReader());
    }

    /// <summary>Registers the library dir as a root with the given alias.</summary>
    public Root AddLibraryRoot(string alias = "Library") =>
        Roots.Add(new Root { Path = Library.Path, Alias = alias, AddedUtc = DateTime.UtcNow });

    public void Dispose()
    {
        Library.Dispose();
        _dataDir.Dispose();
    }
}
