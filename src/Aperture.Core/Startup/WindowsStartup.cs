using Microsoft.Win32;

namespace Aperture.Core.Startup;

/// <summary>
/// HKCU Run-key values used by "Start with Windows". Tests inject a fake store;
/// the live app uses <see cref="CurrentUserRunKeyStore"/>.
/// </summary>
public interface IRunKeyStore
{
    /// <summary>The named value's string data, or null when absent.</summary>
    string? GetValue(string name);

    void SetValue(string name, string data);

    void DeleteValue(string name);
}

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. Per-user, no admin.
/// </summary>
public sealed class CurrentUserRunKeyStore : IRunKeyStore
{
    public const string SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath, writable: false);
        var raw = key?.GetValue(name);
        return raw as string ?? raw?.ToString();
    }

    public void SetValue(string name, string data)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(SubKeyPath, writable: true);
        key.SetValue(name, data, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Launch-at-login via the current-user Run key. The checkbox is source of truth
/// against the registry: checked iff a value named <see cref="ValueName"/> is
/// present and points at this exe. Failures never throw to the UI.
/// </summary>
public static class WindowsStartup
{
    public const string ValueName = "Aperture";

    /// <summary>Quoted full path — the standard Run-key command form.</summary>
    public static string FormatCommand(string exePath) => $"\"{exePath}\"";

    /// <summary>
    /// The exe path encoded in a Run-key command: the quoted token if present,
    /// otherwise the first whitespace-delimited token. Null when empty/unusable.
    /// </summary>
    public static string? ExePathFromCommand(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var s = stored.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return null;
            return s[1..end];
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    /// <summary>True when <paramref name="stored"/> names the same file as <paramref name="exePath"/>.</summary>
    public static bool PointsAtThisExe(string? stored, string exePath)
    {
        var fromStore = ExePathFromCommand(stored);
        if (string.IsNullOrEmpty(fromStore) || string.IsNullOrWhiteSpace(exePath))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(fromStore),
                Path.GetFullPath(exePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsEnabled(IRunKeyStore store, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try { return PointsAtThisExe(store.GetValue(ValueName), exePath); }
        catch { return false; }
    }

    /// <summary>
    /// Writes or deletes the Run value. Returns false on registry failure
    /// (or a missing exe path); never throws.
    /// </summary>
    public static bool TrySetEnabled(IRunKeyStore store, string? exePath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            if (enabled)
                store.SetValue(ValueName, FormatCommand(exePath));
            else
                store.DeleteValue(ValueName);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
