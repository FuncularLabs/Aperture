using System.IO;
using System.Text.Json;
using Aperture.Core.Startup;

namespace Aperture.App;

/// <summary>
/// Per-user Windows integration for Aperture: copy to AppData and create Start-menu
/// / Desktop shortcuts. No file association — Aperture is a browser, not an editor.
///
/// Everything writes to LocalAppData / per-user Start Menu — no admin.
/// </summary>
internal static class RegistrationService
{
    private const string DisplayName = "Aperture";
    private const string ExeCanonicalName = "Aperture.exe";
    private const string ShortcutLinkName = "Aperture.lnk";
    private const string InstallInfoName = "install-info.json";

    public static string CurrentExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine current exe path.");

    public static string AppDataInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Aperture");

    public static string AppDataInstallExe => Path.Combine(AppDataInstallDir, ExeCanonicalName);

    public static string StartMenuLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutLinkName);

    public static string DesktopLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutLinkName);

    private static string InstallInfoPath => Path.Combine(AppDataInstallDir, InstallInfoName);

    public static bool IsInstalledToAppData() => File.Exists(AppDataInstallExe);
    public static bool HasStartMenuShortcut() => File.Exists(StartMenuLinkPath);
    public static bool HasDesktopShortcut() => File.Exists(DesktopLinkPath);

    /// <summary>Where the exe originally came from, and whether we moved (deleted) it.</summary>
    public sealed record InstallInfo(string? OriginalPath, bool Moved);

    public static bool IsRunningFromAppDataInstall()
    {
        try
        {
            var here = Path.GetFullPath(CurrentExePath);
            return string.Equals(here, Path.GetFullPath(AppDataInstallExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ===== AppData install + Start Menu =====

    /// <summary>Copy the current exe to %LocalAppData%\Programs\Aperture.</summary>
    public static string InstallToAppData()
    {
        Directory.CreateDirectory(AppDataInstallDir);
        var source = CurrentExePath;
        // If we're already running from the target location, skip the copy —
        // the file is locked anyway.
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(AppDataInstallExe),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, AppDataInstallExe, overwrite: true);
        }
        return AppDataInstallExe;
    }

    public static void UninstallFromAppData()
    {
        try
        {
            if (Directory.Exists(AppDataInstallDir))
                Directory.Delete(AppDataInstallDir, recursive: true);
        }
        catch { /* usually file-in-use; user needs to close first */ }
    }

    private static void CreateShortcut(string linkPath, string targetExe)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetExe;
            link.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? string.Empty;
            link.IconLocation = targetExe + ",0";
            link.Description = "Aperture — a fast local image & video browser.";
            link.Save();
        }
        catch { /* best-effort — shortcuts are a bonus, not critical */ }
    }

    public static void CreateStartMenuShortcut(string targetExe) => CreateShortcut(StartMenuLinkPath, targetExe);
    public static void CreateDesktopShortcut(string targetExe) => CreateShortcut(DesktopLinkPath, targetExe);

    public static void RemoveStartMenuShortcut()
    {
        try { if (File.Exists(StartMenuLinkPath)) File.Delete(StartMenuLinkPath); } catch { }
    }

    public static void RemoveDesktopShortcut()
    {
        try { if (File.Exists(DesktopLinkPath)) File.Delete(DesktopLinkPath); } catch { }
    }

    // ===== Original-location memorialization + move / restore =====

    /// <summary>Record where the exe came from (so uninstall can offer to restore it).</summary>
    public static void SaveInstallInfo(string? originalPath, bool moved)
    {
        try
        {
            Directory.CreateDirectory(AppDataInstallDir);
            File.WriteAllText(InstallInfoPath,
                JsonSerializer.Serialize(new InstallInfo(originalPath, moved)));
        }
        catch { /* best-effort */ }
    }

    public static InstallInfo? ReadInstallInfo()
    {
        try
        {
            if (!File.Exists(InstallInfoPath)) return null;
            return JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(InstallInfoPath));
        }
        catch { return null; }
    }

    /// <summary>Copy the installed exe back to <paramref name="destPath"/>.</summary>
    public static bool RestoreToOriginal(string destPath)
    {
        try
        {
            if (!File.Exists(AppDataInstallExe)) return false;
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(AppDataInstallExe, destPath, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Delete the original downloaded exe after a "move" install. Called by the
    /// freshly-launched AppData copy (passed <c>--finish-move &lt;path&gt;</c>);
    /// the original process needs a moment to exit and release the file lock.
    /// </summary>
    public static void FinishMove(string originalPath)
    {
        try
        {
            // Never delete the installed copy, even if a caller passes it by mistake.
            if (string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(AppDataInstallExe),
                    StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { return; }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 40; attempt++) // ~10s total
            {
                try
                {
                    if (!File.Exists(originalPath)) return;
                    File.Delete(originalPath);
                    return;
                }
                catch { await Task.Delay(250); }
            }
        });
    }

    /// <summary>
    /// After copying to AppData: if Start with Windows is on (Run key points at
    /// the download, or already at the install), rewrite it to the installed exe.
    /// </summary>
    public static void RetargetStartWithWindows(string fromExe, string installedExe)
    {
        try
        {
            WindowsStartup.TryRetargetIfPointsAt(new CurrentUserRunKeyStore(), fromExe, installedExe);
        }
        catch { /* install already landed */ }
    }

    /// <summary>
    /// When this process IS the AppData copy and a Run value exists (any path),
    /// rewrite it to the canonical installed exe. Covers the case where install
    /// copied the file but died before retargeting, so logon would still fire a
    /// Downloads path.
    /// </summary>
    public static void RefreshAutostartToInstalledCopy()
    {
        if (!IsRunningFromAppDataInstall()) return;
        try
        {
            var store = new CurrentUserRunKeyStore();
            if (WindowsStartup.HasValue(store))
                WindowsStartup.TrySetEnabled(store, AppDataInstallExe, enabled: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Drop the Run value if it still points at the AppData copy being removed.
    /// </summary>
    public static void ClearStartWithWindowsIfPointsAtInstall()
    {
        try
        {
            var store = new CurrentUserRunKeyStore();
            if (WindowsStartup.IsEnabled(store, AppDataInstallExe))
                WindowsStartup.TrySetEnabled(store, AppDataInstallExe, enabled: false);
        }
        catch { /* uninstall already happened */ }
    }
}
