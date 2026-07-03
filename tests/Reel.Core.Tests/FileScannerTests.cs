using Reel.Core.Indexing;
using Reel.Core.Tests.Support;

namespace Reel.Core.Tests;

public class FileScannerTests
{
    [Fact]
    public void Scan_ReturnsSupportedImages_Recursively()
    {
        using var lib = new TempDir();
        TestImages.Write(lib.Combine("a.jpg"), 100, 100);
        TestImages.Write(lib.Combine("nested", "deep", "b.png"), 100, 100);

        var files = FileScanner.Scan(lib.Path);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Scan_IgnoresUnsupportedExtensions()
    {
        using var lib = new TempDir();
        TestImages.Write(lib.Combine("keep.jpg"), 100, 100);
        File.WriteAllText(lib.Combine("readme.txt"), "x");
        File.WriteAllText(lib.Combine("movie.mp4"), "x");
        File.WriteAllText(lib.Combine("photo.heic"), "x"); // deferred format, not v1

        var files = FileScanner.Scan(lib.Path);

        var file = Assert.Single(files);
        Assert.EndsWith("keep.jpg", file.FullPath);
    }

    [Fact]
    public void Scan_RelativePaths_AreRelativeToRoot()
    {
        using var lib = new TempDir();
        TestImages.Write(lib.Combine("sub", "x.jpg"), 100, 100);

        var file = Assert.Single(FileScanner.Scan(lib.Path));

        Assert.Equal(Path.Combine("sub", "x.jpg"), file.RelPath);
    }

    [Fact]
    public void Scan_CapturesSizeAndMTime()
    {
        using var lib = new TempDir();
        var path = lib.Combine("x.jpg");
        TestImages.Write(path, 100, 100);

        var file = Assert.Single(FileScanner.Scan(lib.Path));

        Assert.Equal(new FileInfo(path).Length, file.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, file.MTimeTicks);
    }
}
