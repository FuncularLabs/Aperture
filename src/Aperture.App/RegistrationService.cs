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

    /// <summary>
    /// Caption the About surface and updater already share: "installed" when
    /// this process <em>is</em> the AppData exe, otherwise "portable". Same
    /// check as <see cref="IsRunningFromAppDataInstall"/> — not "a copy exists
    /// under AppData while this process is still a download."
    /// </summary>
    public static string RunningKind => IsRunningFromAppDataInstall() ? "installed" : "portable";

    // ===== AppData install + Start Menu =====

    /// <summary>
    /// Copy the current exe — and every native DLL beside it — to
    /// %LocalAppData%\Programs\Aperture. Throws if any of them can't be copied.
    /// </summary>
    public static string InstallToAppData()
    {
        // If we're already running from the target location, CopyProgram skips —
        // the file is locked anyway.
        CopyProgram(CurrentExePath, AppDataInstallExe);
        return AppDataInstallExe;
    }

    /// <summary>
    /// Copy a program folder's runnable set: the exe plus every <c>*.dll</c> that sits
    /// directly beside it. A single-file publish that bundles its natives has no
    /// siblings and this is a plain exe copy; a publish whose natives are loose files
    /// (e_sqlite3.dll, libSkiaSharp.dll, Microsoft.Data.SqlClient.SNI.dll) needs all of
    /// them next to the copy or the copy dies at startup with DllNotFoundException.
    ///
    /// Natives go first, then the exe, then every native is checked at the
    /// destination. If a native can't land the exe at the destination is untouched —
    /// a NEW exe beside missing natives is precisely the broken install this guards
    /// against. Any failure throws; the caller reports it.
    /// </summary>
    internal static void CopyProgram(string sourceExe, string destExe)
    {
        var sourceFull = Path.GetFullPath(sourceExe);
        var destFull = Path.GetFullPath(destExe);
        if (string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
            return;

        var destDir = Path.GetDirectoryName(destFull)
                      ?? throw new IOException("Install destination has no folder: " + destFull);
        Directory.CreateDirectory(destDir);

        var natives = NativeSiblings(sourceFull);
        foreach (var native in natives)
        {
            var target = Path.Combine(destDir, Path.GetFileName(native));
            try
            {
                File.Copy(native, target, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Couldn't copy {Path.GetFileName(native)} to {destDir} — the installed copy " +
                    "would not start without it, so nothing was installed. " + ex.Message, ex);
            }
        }

        File.Copy(sourceFull, destFull, overwrite: true);

        // Fail loudly rather than leave an exe that dies on launch: anything (a
        // scanner, a quarantine) that removed a native between the copy and here.
        var missing = natives
            .Select(Path.GetFileName)
            .Where(name => !File.Exists(Path.Combine(destDir, name!)))
            .ToList();
        if (missing.Count > 0)
            throw new IOException(
                "Installed copy is missing native libraries it needs to start: " +
                string.Join(", ", missing) + " (in " + destDir + ").");
    }

    /// <summary>
    /// The <c>*.dll</c> files directly beside <paramref name="exePath"/>, sorted by
    /// name. Top level only: the docs folder shipped next to the exe is not program.
    /// </summary>
    internal static IReadOnlyList<string> NativeSiblings(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    /// <summary>Copy the installed exe (and its natives) back to <paramref name="destPath"/>.</summary>
    public static bool RestoreToOriginal(string destPath) => RestoreProgram(AppDataInstallExe, destPath);

    /// <summary>
    /// Same copy as the install, in reverse; a bare exe put back in Downloads would
    /// recreate the no-natives crash there. False when there is nothing to restore
    /// or the copy failed.
    /// </summary>
    internal static bool RestoreProgram(string installedExe, string destPath)
    {
        try
        {
            if (!File.Exists(installedExe)) return false;
            CopyProgram(installedExe, destPath);
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
