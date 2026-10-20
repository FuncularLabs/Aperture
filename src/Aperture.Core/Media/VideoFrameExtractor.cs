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

    // A frame whose mean luminance is at least this is clearly not a black/fade-in frame, so we stop
    // seeking deeper and use it. Below this we keep the brightest frame found across the seek points.
    private const int NotDarkThreshold = 24;

    private static readonly Lazy<string?> Ffmpeg = new(Resolve);

    public static bool IsAvailable => Ffmpeg.Value is not null;

    /// <summary>
    /// Returns a frame fitted within <paramref name="maxEdge"/>, or null if extraction fails.
    /// Many clips open on a black fade-in, so if the representative frame is dark we seek progressively
    /// deeper and keep the brightest frame — recovering real content instead of caching a black tile.
    /// A genuinely dark clip simply yields its brightest (still dark) frame, which is correct.
    /// </summary>
    public static SKBitmap? Extract(string videoPath, int maxEdge)
    {
        var ffmpeg = Ffmpeg.Value;
        if (ffmpeg is null || !File.Exists(videoPath))
            return null;

        var scale = $"scale=w={maxEdge}:h={maxEdge}:force_original_aspect_ratio=decrease";

        // Cheapest / most-representative first. The thumbnail filter already avoids transitional
        // frames in the opening window; the deeper seeks rescue clips with a long black intro.
        (double? Seek, string Filter)[] candidates =
        [
            (null, $"thumbnail=300,{scale}"),
            (3.0,  $"thumbnail=100,{scale}"),
            (10.0, scale),
            (30.0, scale),
            (1.0,  scale),
            (0.0,  scale),
        ];

        SKBitmap? best = null;
        var bestLuma = -1;
        foreach (var (seek, filter) in candidates)
        {
            var bytes = Run(ffmpeg, videoPath, seek, filter);
            if (bytes is null || bytes.Length == 0)
                continue;

            SKBitmap? frame;
            try { frame = SKBitmap.Decode(bytes); } catch { frame = null; }
            if (frame is null)
                continue;

            var luma = MeanLuma(frame);
            if (luma > bestLuma)
            {
                best?.Dispose();
                best = frame;
                bestLuma = luma;
            }
            else
            {
                frame.Dispose();
            }

            if (bestLuma >= NotDarkThreshold)
                break; // found a clearly-lit frame; no need to seek further
        }

        return best;
    }

    /// <summary>Mean luminance (0–255) over a sampled grid — the brightness signal that drives frame selection.</summary>
    public static int MeanLuma(SKBitmap bitmap)
    {
        long sum = 0;
        var n = 0;
        var sx = Math.Max(1, bitmap.Width / 32);
        var sy = Math.Max(1, bitmap.Height / 32);
        for (var y = 0; y < bitmap.Height; y += sy)
            for (var x = 0; x < bitmap.Width; x += sx)
            {
                var p = bitmap.GetPixel(x, y);
                sum += (p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000;
                n++;
            }
        return n == 0 ? 0 : (int)(sum / n);
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
