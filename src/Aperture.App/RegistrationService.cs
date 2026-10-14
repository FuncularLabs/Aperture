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
    /// The native libraries Aperture loads at startup, by file name. A single-file
    /// publish carries them inside the exe (IncludeNativeLibrariesForSelfExtract); a
    /// publish without that flag leaves them loose beside it, and an exe copied without
    /// them dies at startup with DllNotFoundException. Only these names are ever copied
    /// by an install: the exe usually sits in Downloads, and the other DLLs there are
    /// not ours. A test pins this list to the native assets the build actually
    /// produces, so a new native dependency fails the suite until it is added here.
    /// </summary>
    internal static readonly IReadOnlyList<string> KnownNativeLibraries =
    [
        "e_sqlite3.dll",                     // SQLitePCLRaw bundle (Microsoft.Data.Sqlite)
        "libSkiaSharp.dll",                  // SkiaSharp
        "Microsoft.Data.SqlClient.SNI.dll",  // Microsoft.Data.SqlClient, via FunkyORM
    ];

    /// <summary>
    /// Copy the exe plus whichever of <see cref="KnownNativeLibraries"/> sit loose
    /// beside it. A bundled single-file exe has none and this is a plain exe copy.
    ///
    /// Not a general folder copy: a multi-file build (managed DLLs, .deps.json,
    /// .runtimeconfig.json beside the exe) is not something install supports.
    ///
    /// Natives first, then a check that each one is at the destination, then the exe.
    /// So if a native can't land, the exe at the destination is untouched — a NEW exe
    /// beside missing natives is precisely the broken install this guards against.
    /// Natives copied earlier in the same attempt may already be the new version; the
    /// next attempt overwrites them again. Any failure throws; the caller reports it.
    /// </summary>
    internal static void CopyProgram(string sourceExe, string destExe)
        => CopyProgram(sourceExe, destExe, (from, to) => File.Copy(from, to, overwrite: true));

    /// <summary>
    /// As above, with the file copy injectable so the "a native did not land" check can
    /// be exercised: a copy that reports success and leaves no file is what a
    /// quarantine-on-write scanner looks like from here.
    /// </summary>
    internal static void CopyProgram(string sourceExe, string destExe, Action<string, string> copy)
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
            var name = Path.GetFileName(native);
            try
            {
                copy(native, Path.Combine(destDir, name));
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Couldn't copy {name} to {destDir} — the installed copy would not start " +
                    "without it, so the program file was not replaced. " + ex.Message, ex);
            }
        }

        // Fail loudly, before the exe goes in, rather than leave an exe that dies on
        // launch: a scanner or quarantine can remove a native the instant it is written.
        var missing = natives
            .Select(n => Path.GetFileName(n))
            .Where(name => !File.Exists(Path.Combine(destDir, name)))
            .ToList();
        if (missing.Count > 0)
            throw new IOException(
                $"Native libraries did not land in {destDir}: {string.Join(", ", missing)}. " +
                "The installed copy would not start without them, so the program file was not replaced.");

        copy(sourceFull, destFull);
    }

    /// <summary>
    /// Which of <see cref="KnownNativeLibraries"/> sit directly beside
    /// <paramref name="exePath"/>, in list order. Nothing else beside the exe is
    /// considered, whatever its extension.
    /// </summary>
    internal static IReadOnlyList<string> NativeSiblings(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (string.IsNullOrEmpty(dir))
            return [];
        return KnownNativeLibraries
            .Select(name => Path.Combine(dir, name))
            .Where(File.Exists)
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
