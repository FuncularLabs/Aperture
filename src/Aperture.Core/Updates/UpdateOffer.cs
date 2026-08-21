namespace Aperture.Core.Updates;

/// <summary>
/// Decides what the About surface should actually offer.
///
/// The releases feed keeps reporting the newest prerelease forever, which is only
/// interesting while it leads. Once a stable release catches up — 0.8.0-beta1 next
/// to a shipped 0.8.0 — showing it is worse than showing nothing: it reads as a
/// newer, more adventurous build when it is in fact older code that has already
/// been superseded. So a prerelease is offered only while it leads BOTH the
/// running version and the newest stable.
/// </summary>
public static class UpdateOffer
{
    /// <summary>Is this prerelease still ahead of everything else?</summary>
    public static bool ShowPrerelease(ReleaseInfo? prerelease, ReleaseInfo? stable, UpdateVersion? current)
    {
        if (prerelease is null) return false;
        // Nothing to measure against at all — don't push a prerelease on a guess.
        // (Unlike a stable release, where offering the newest is a safe default.)
        if (current is null && stable is null) return false;
        if (current is not null && prerelease.Version.CompareTo(current) <= 0) return false;
        if (stable is not null && prerelease.Version.CompareTo(stable.Version) <= 0) return false;
        return true;
    }

    /// <summary>
    /// Is this stable release worth offering? Yes when it's newer than what's
    /// running — and also when we can't tell what's running, because the failure
    /// that must never happen is a real update going unoffered.
    /// </summary>
    public static bool ShowStableUpdate(ReleaseInfo? stable, UpdateVersion? current) =>
        stable is not null && (current is null || stable.Version.CompareTo(current) > 0);

    /// <summary>
    /// Does this instance need *restarting* rather than updating?
    ///
    /// True when the exe on disk already satisfies what's being offered, or when
    /// the exe on disk is ahead of the version we are running (another process
    /// already applied an update). Unknown answers false — let it try and report
    /// honestly rather than refusing an update on a guess.
    /// </summary>
    public static bool NeedsRestartNotUpdate(
        UpdateVersion? onDisk, UpdateVersion? wanted, UpdateVersion? running)
    {
        if (onDisk is null) return false;
        if (wanted is not null && onDisk.CompareTo(wanted) >= 0) return true;
        return running is not null && onDisk.CompareTo(running) > 0;
    }
}
