using Aperture.Core.Media;
using Aperture.Core.Models;

namespace Aperture.Core.Indexing;

/// <summary>A supported media file found under a root.</summary>
public readonly record struct ScannedFile(
    string FullPath, string RelPath, string Ext, long SizeBytes, long MTimeTicks, MediaKind Kind);

/// <summary>
/// Recursively finds supported media files under a root. Enumeration is fault
/// tolerant: an unreadable subtree is skipped rather than aborting the whole scan.
/// </summary>
public static class FileScanner
{
    public static List<ScannedFile> Scan(string rootPath, bool includeVideos = true, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(rootPath);
        var results = new List<ScannedFile>();

        foreach (var file in SafeEnumerateFiles(root, ct))
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
                SupportedFormats.KindOf(ext)));
        }

        return results;
    }

    /// <summary>
    /// Manual DFS so a single inaccessible directory doesn't throw out of the
    /// whole enumeration the way <c>EnumerateFiles(AllDirectories)</c> would.
    /// </summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            IEnumerable<string> subDirs = [];
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (var sub in subDirs)
                stack.Push(sub);

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
