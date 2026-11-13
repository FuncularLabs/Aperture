using Aperture.Core.Media;
using Aperture.Core.Models;

namespace Aperture.Core.Indexing;

/// <summary>A supported media file found under a root.</summary>
public readonly record struct ScannedFile(
    string FullPath, string RelPath, string Ext, long SizeBytes, long MTimeTicks, MediaKind Kind)
{
    /// <summary>Online only (a cloud placeholder): reading it downloads it first (<see cref="CloudFiles"/>).</summary>
    public bool IsOnlineOnly { get; init; }
}

/// <summary>
/// Finds supported media files under a root — the whole tree, or just the root's own folder when
/// <c>recursive</c> is false. Enumeration is fault tolerant: an unreadable subtree is skipped
/// rather than aborting the whole scan.
/// </summary>
public static class FileScanner
{
    /// <summary>How many files a scan finds between two reports of its count.</summary>
    public const int TallyStep = 250;

    /// <summary>
    /// Finds the supported files under <paramref name="rootPath"/>. <paramref name="onProgress"/>, if given, hears the
    /// count so far every <see cref="TallyStep"/> files found, and once at the end with the total.
    /// </summary>
    public static List<ScannedFile> Scan(
        string rootPath, bool includeVideos = true, CancellationToken ct = default, bool recursive = true,
        Action<ScanTally>? onProgress = null)
    {
        var root = Path.GetFullPath(rootPath);
        var results = new List<ScannedFile>();
        var onlineOnly = 0;

        foreach (var file in SafeEnumerateFiles(root, ct, recursive))
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!SupportedFormats.IsSupported(ext, includeVideos))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                    continue;
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            results.Add(new ScannedFile(
                file,
                Path.GetRelativePath(root, file),
                ext,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                SupportedFormats.KindOf(ext))
            {
                IsOnlineOnly = CloudFiles.IsOnlineOnly(info.Attributes), // read with the size and time: no extra disk call
            });
            if (results[^1].IsOnlineOnly)
                onlineOnly++;
            if (results.Count % TallyStep == 0)
                onProgress?.Invoke(new ScanTally(results.Count, onlineOnly));
        }

        onProgress?.Invoke(new ScanTally(results.Count, onlineOnly)); // the total: counting is done
        return results;
    }

    /// <summary>
    /// Manual DFS so a single inaccessible directory doesn't throw out of the
    /// whole enumeration the way <c>EnumerateFiles(AllDirectories)</c> would.
    /// When <paramref name="recursive"/> is false only the root folder itself is read —
    /// subdirectories are never even enumerated.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken ct, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            if (recursive)
            {
                IEnumerable<string> subDirs = [];
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                foreach (var sub in subDirs)
                    stack.Push(sub);
            }

            IEnumerable<string> files = [];
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (var file in files)
                yield return file;
        }
    }
}
