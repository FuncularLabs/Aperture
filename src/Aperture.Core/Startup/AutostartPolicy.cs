namespace Aperture.Core.Startup;

/// <summary>
/// Start-with-Windows write rules. An installed copy (or one that just copied
/// itself to AppData this session) may write or delete the Run key. A portable
/// copy must not enable Run — that would point logon at a Downloads/USB path.
/// </summary>
public static class AutostartPolicy
{
    /// <summary>
    /// True when turning autostart on would write a portable path. The UI must
    /// ask to install first instead of calling <see cref="TryApply"/>.
    /// </summary>
    public static bool MustInstallFirst(bool enable, bool runningFromInstall, bool installedThisSession)
        => enable && !runningFromInstall && !installedThisSession;

    /// <summary>
    /// Path the Run key should name: the AppData exe after this process is (or
    /// just became) the installed copy; otherwise the running portable path.
    /// </summary>
    public static string? TargetExe(
        string? processExe, string installedExe, bool runningFromInstall, bool installedThisSession)
        => runningFromInstall || installedThisSession ? installedExe : processExe;

    /// <summary>
    /// Writes or deletes the Run value. Returns false and writes nothing when
    /// <paramref name="enable"/> would target a portable path — the caller must
    /// install first. Disabling always deletes the named value.
    /// </summary>
    public static bool TryApply(
        IRunKeyStore store,
        string? processExe,
        string installedExe,
        bool enable,
        bool runningFromInstall,
        bool installedThisSession)
    {
        if (MustInstallFirst(enable, runningFromInstall, installedThisSession))
            return false;
        return WindowsStartup.TrySetEnabled(
            store,
            TargetExe(processExe, installedExe, runningFromInstall, installedThisSession),
            enable);
    }
}
