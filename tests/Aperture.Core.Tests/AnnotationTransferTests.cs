using Aperture.Core.Annotations;
using Aperture.Core.Models;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

public class AnnotationTransferTests
{
    private static string MakeFile(TempDir dir, string rel)
    {
        var full = dir.Combine(rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    [Fact]
    public void Export_then_import_remaps_by_alias_and_merges_upsert()
    {
        using var export = new TempDir();
        var exportFile = export.Combine("tags.json");

        // Machine A: root "Photos" with one annotated file at sub/pic.jpg.
        using (var a = new ApertureScope())
        {
            var fileA = MakeFile(a.Library, "sub/pic.jpg");
            a.Roots.Add(new Root { Path = a.Library.Path, Alias = "Photos", AddedUtc = DateTime.UtcNow });
            var storeA = new AnnotationStore(a.Database);
            storeA.Save(fileA, ["beach", "2019"], "note from A");

            var count = new AnnotationTransfer(storeA, a.Roots).Export(exportFile);
            Assert.Equal(1, count);
        }

        // Machine B: SAME alias + relative file at a DIFFERENT absolute location,
        // already carrying its own tag. Import should remap and union, not clobber.
        using var b = new ApertureScope();
        var fileB = MakeFile(b.Library, "sub/pic.jpg");
        b.Roots.Add(new Root { Path = b.Library.Path, Alias = "Photos", AddedUtc = DateTime.UtcNow });
        var storeB = new AnnotationStore(b.Database);
        storeB.Save(fileB, ["family"], "");

        var transfer = new AnnotationTransfer(storeB, b.Roots);
        var summary = transfer.Import(exportFile);

        Assert.Equal(1, summary.Applied);
        Assert.Equal(0, summary.Unresolved);

        var merged = storeB.Get(fileB);
        Assert.Contains("family", merged.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("beach", merged.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("2019", merged.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("note from A", merged.Note); // filled the empty local note

        // Idempotent: importing the same file again changes nothing.
        var again = transfer.Import(exportFile);
        Assert.Equal(1, again.Applied);
        var after = storeB.Get(fileB);
        Assert.Equal(merged.Tags.Count, after.Tags.Count);
        Assert.Equal("note from A", after.Note);
    }

    [Fact]
    public void AnImportForOneOfTwoNamesOnlyDotNetFoldsTogether_LeavesTheOtherFilesRow()
    {
        // Round 28, D F-1: an import reaches the row its entry's path resolves to (M4), not through the row the window
        // shows for it, which can be another file's: for "ς.jpg" beside a tagged "σ.jpg" (two files on NTFS, one path to
        // the window), it's σ.jpg's.
        using var export = new TempDir();
        var exportFile = export.Combine("tags.json");
        using var b = new ApertureScope();
        var sigma = MakeFile(b.Library, "σ.jpg");
        var final = MakeFile(b.Library, "ς.jpg");
        b.Roots.Add(new Root { Path = b.Library.Path, Alias = "Photos", AddedUtc = DateTime.UtcNow });
        var store = new AnnotationStore(b.Database);
        store.Save(sigma, ["mine"], "my note");
        File.WriteAllText(exportFile, $$"""
            {
              "version": 1,
              "entries": [
                { "path": {{System.Text.Json.JsonSerializer.Serialize(final)}}, "rootAlias": "Photos", "relPath": "ς.jpg", "tags": ["theirs"], "note": "" }
              ]
            }
            """);

        var summary = new AnnotationTransfer(store, b.Roots).Import(exportFile);

        Assert.Equal(1, summary.Applied);
        Assert.Equal(["mine"], store.Get(sigma).Tags);
        Assert.Equal("my note", store.Get(sigma).Note);
        Assert.Equal(["theirs"], store.Get(final).Tags); // nothing of σ.jpg's brought along
    }

    [Fact]
    public void Import_counts_unresolved_and_skips_empty_entries()
    {
        using var export = new TempDir();
        var exportFile = export.Combine("tags.json");
        var ghost = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gone.jpg"); // never created
        File.WriteAllText(exportFile, $$"""
            {
              "version": 1,
              "entries": [
                { "path": {{System.Text.Json.JsonSerializer.Serialize(ghost)}}, "tags": ["ghost"], "note": "" },
                { "path": {{System.Text.Json.JsonSerializer.Serialize(ghost)}}, "tags": [], "note": "" }
              ]
            }
            """);

        using var b = new ApertureScope();
        var storeB = new AnnotationStore(b.Database);
        var summary = new AnnotationTransfer(storeB, b.Roots).Import(exportFile);

        Assert.Equal(0, summary.Applied);
        Assert.Equal(1, summary.Unresolved); // no local root, path doesn't exist
        Assert.Equal(1, summary.Skipped);    // empty tags + note
    }
}
