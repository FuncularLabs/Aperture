using Aperture.Core.Indexing;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The scan counts what it finds as it goes, and which of the files are online-only (0.9.1 batch, item 4: "first make
/// a best-effort count of the files to process"; cloud files are downloaded as they're read).
/// </summary>
public class FileScannerTallyTests
{
    [Fact]
    public void ReportsTheCount_WhileScanning()
    {
        using var lib = new TempDir();
        for (var i = 0; i < 600; i++)
        {
            Directory.CreateDirectory(lib.Combine($"d{i % 7}"));
            File.WriteAllText(lib.Combine($"d{i % 7}", $"f{i}.jpg"), "x"); // extension-only: the scanner filters by it
        }
        File.WriteAllText(lib.Combine("readme.txt"), "x"); // not counted

        var tallies = new List<ScanTally>();
        var files = FileScanner.Scan(lib.Path, onProgress: tallies.Add);

        Assert.Equal(600, files.Count);
        // Every 250 files found, then once at the end with the total: the count climbs while the tree is walked.
        Assert.Equal([250, 500, 600], tallies.Select(t => t.Found));
        Assert.All(tallies, t => Assert.Equal(0, t.OnlineOnly));
    }

    [Fact]
    public void ReportsOnce_AtTheEnd_ForAFolderWithNothingInIt()
    {
        using var lib = new TempDir();

        var tallies = new List<ScanTally>();
        FileScanner.Scan(lib.Path, onProgress: tallies.Add);

        Assert.Equal([new ScanTally(0, 0)], tallies);
    }

    [Fact]
    public void ReportsTheLastCount_WhenTheTotalIsAMultipleOfTheStep()
    {
        using var lib = new TempDir();
        for (var i = 0; i < 250; i++)
            File.WriteAllText(lib.Combine($"f{i}.jpg"), "x");

        var tallies = new List<ScanTally>();
        FileScanner.Scan(lib.Path, onProgress: tallies.Add);

        Assert.Equal([250, 250], tallies.Select(t => t.Found)); // the end is always reported: it's what says "done counting"
    }

    [Fact]
    public void CountsOfflineFiles_AsOnlineOnly()
    {
        using var lib = new TempDir();
        File.WriteAllText(lib.Combine("here.jpg"), "x");
        var cloud = lib.Combine("cloud.jpg");
        File.WriteAllText(cloud, "x");
        File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline); // what a placeholder carries
        var skipped = lib.Combine("cloud.txt");
        File.WriteAllText(skipped, "x");
        File.SetAttributes(skipped, File.GetAttributes(skipped) | FileAttributes.Offline); // not a photo: not counted

        var tallies = new List<ScanTally>();
        var files = FileScanner.Scan(lib.Path, onProgress: tallies.Add);

        Assert.Equal(2, files.Count);
        Assert.Equal(new ScanTally(2, 1), tallies[^1]);
    }

    [Fact]
    public void WithoutAListener_ScansAsBefore()
    {
        using var lib = new TempDir();
        File.WriteAllText(lib.Combine("a.jpg"), "x");

        Assert.Single(FileScanner.Scan(lib.Path));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReadOnly, false)]
    [InlineData(FileAttributes.Offline, true)]                      // 0x1000: older sync clients' placeholders
    [InlineData((FileAttributes)0x40000, true)]                     // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((FileAttributes)0x400000, true)]                    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (OneDrive, Dropbox)
    [InlineData(FileAttributes.Archive | (FileAttributes)0x400000, true)]
    [InlineData((FileAttributes)0x80000, false)]                    // pinned (always kept on this device): it's here
    [InlineData((FileAttributes)0x100000, false)]                   // unpinned alone: still here until it's freed
    public void IsOnlineOnly(FileAttributes attributes, bool onlineOnly) =>
        Assert.Equal(onlineOnly, CloudFiles.IsOnlineOnly(attributes));
}
