using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

namespace Aperture.Core.Media;

/// <summary>
/// Extracts a representative still frame from a video with ffmpeg. ffmpeg carries
/// its own decoders, so this produces real thumbnails even for codecs/containers
/// the Windows shell won't thumbnail (e.g. files where VLC has taken over the
/// association without providing a thumbnail handler).
///
/// ffmpeg is located via <c>REEL_FFMPEG</c>, then PATH, then a copy placed next to
/// the app. When ffmpeg isn't available the caller falls back to the shell.
/// </summary>
public static class VideoFrameExtractor
{
    private const int TimeoutMs = 20_000;

    private static readonly Lazy<string?> Ffmpeg = new(Resolve);

    public static bool IsAvailable => Ffmpeg.Value is not null;

    /// <summary>Returns a frame fitted within <paramref name="maxEdge"/>, or null if extraction fails.</summary>
    public static SKBitmap? Extract(string videoPath, int maxEdge)
    {
        var ffmpeg = Ffmpeg.Value;
        if (ffmpeg is null || !File.Exists(videoPath))
            return null;

        var scale = $"scale=w={maxEdge}:h={maxEdge}:force_original_aspect_ratio=decrease";

        // 1) Let ffmpeg's thumbnail filter pick the most representative frame from the
        //    opening ~300 frames — it avoids blank/black/transitional frames.
        // 2..3) Fall back to a plain seek for clips the filter can't process.
        var bytes = Run(ffmpeg, videoPath, seekSeconds: null, videoFilter: $"thumbnail=300,{scale}");
        if (bytes is null || bytes.Length == 0)
            bytes = Run(ffmpeg, videoPath, seekSeconds: 1.0, videoFilter: scale);
        if (bytes is null || bytes.Length == 0)
            bytes = Run(ffmpeg, videoPath, seekSeconds: 0.0, videoFilter: scale);
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            return SKBitmap.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? Run(string ffmpeg, string input, double? seekSeconds, string videoFilter)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (seekSeconds is { } seek)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(seek.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var arg in new[]
                 {
                     "-i", input,
                     "-frames:v", "1",
                     "-vf", videoFilter,
                     "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "3",
                     "-",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // Drain stderr so its pipe can't fill and block ffmpeg.
            _ = process.StandardError.ReadToEndAsync();

            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);

            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            copy.Wait(2000);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string? Resolve()
    {
        var candidates = new List<string>();

        var overridePath = Environment.GetEnvironmentVariable("REEL_FFMPEG");
        if (!string.IsNullOrWhiteSpace(overridePath))
            candidates.Add(overridePath);

        candidates.Add("ffmpeg"); // resolved via PATH
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")); // bundled

        foreach (var candidate in candidates)
        {
            if (CanRun(candidate))
                return candidate;
        }
        return null;
    }

    private static bool CanRun(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null)
                return false;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
