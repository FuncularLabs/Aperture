using System.Text;
using Aperture.Core.Models;

namespace Aperture.Core.Formatting;

/// <summary>
/// Renders a tile caption from a tokenized format string.
///
/// Tokens (inside braces): <c>{name}</c>, <c>{ext}</c>, <c>{alias}</c>, <c>{size}</c>,
/// <c>{camera}</c>, <c>{w}</c>, <c>{h}</c>, <c>{dim}</c>, <c>{date}</c> (EXIF taken
/// date falling back to file time), <c>{mtime}</c>, <c>{exif.date}</c>/<c>{taken}</c>
/// (EXIF only).
///
/// A token may carry a .NET format after a colon — <c>{date:yyyy-MM-dd HH.mm}</c> —
/// and a fallback chain with <c>??</c> — <c>{exif.date ?? mtime : yyyy-MM-dd}</c>,
/// which uses the first token that yields a non-empty value.
/// </summary>
public static class CaptionFormatter
{
    public static string Format(string format, MediaItem item, string alias)
    {
        if (string.IsNullOrEmpty(format))
            return "";

        var sb = new StringBuilder(format.Length + 16);
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c == '{')
            {
                var end = format.IndexOf('}', i + 1);
                if (end < 0)
                {
                    sb.Append(format, i, format.Length - i);
                    break;
                }
                sb.Append(ResolveExpression(format.Substring(i + 1, end - i - 1), item, alias));
                i = end + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string ResolveExpression(string expr, MediaItem item, string alias)
    {
        // Split off the format specifier at the first ':' so time formats like
        // "HH:mm" (which contain a colon) stay intact.
        string? fmt = null;
        var colon = expr.IndexOf(':');
        var valuePart = expr;
        if (colon >= 0)
        {
            valuePart = expr[..colon];
            fmt = expr[(colon + 1)..].Trim();
        }

        foreach (var token in valuePart.Split("??"))
        {
            var value = ResolveToken(token.Trim(), fmt, item, alias);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return "";
    }

    private static string ResolveToken(string token, string? fmt, MediaItem item, string alias) =>
        token.ToLowerInvariant() switch
        {
            "name" => item.FileName,
            "ext" => item.Ext,
            "alias" => alias,
            "size" => FormatBytes(item.SizeBytes),
            "camera" => item.Camera ?? "",
            "w" => item.Width?.ToString() ?? "",
            "h" => item.Height?.ToString() ?? "",
            "dim" => item is { Width: { } w, Height: { } h } ? $"{w}×{h}" : "",
            "date" => FormatDate(item.TakenUtc ?? item.MTimeUtc.ToLocalTime(), fmt),
            "mtime" => FormatDate(item.MTimeUtc.ToLocalTime(), fmt),
            "exif.date" or "taken" => item.TakenUtc is { } t ? FormatDate(t, fmt) : "",
            _ => "",
        };

    private static string FormatDate(DateTime date, string? fmt) =>
        date.ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM-dd HH:mm" : fmt);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
