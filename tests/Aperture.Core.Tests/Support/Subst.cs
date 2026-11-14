using System.Diagnostics;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// A drive letter for a folder (subst, no admin rights needed), so a test can unplug a "drive" the way a USB drive goes:
/// the letter itself is gone, not just a folder on it. Always <see cref="Remove"/> it in a finally.
/// </summary>
public static class Subst
{
    /// <summary>
    /// A letter no drive (local, mapped or substituted) has now, from H: to Z:, chosen at random: two test runs on one PC
    /// (a mutation sweep and a review, say) rarely want the same one, even when one has just let its own go (round 6,
    /// N6-6).
    /// </summary>
    public static string FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('H', 'Z' - 'H' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();
        if (free.Count == 0)
            throw new InvalidOperationException("no free drive letter");
        return $"{free[Random.Shared.Next(free.Count)]}:";
    }

    public static void Create(string drive, string target) => Run($"{drive} \"{target}\"");

    /// <summary>
    /// Gives <paramref name="target"/> a free letter, trying the next one down if another program (another test run on
    /// this PC, say) takes it first (round 5, N-4 / F-9). Returns the letter.
    /// </summary>
    public static string CreateOnAFreeLetter(string target)
    {
        for (var tries = 0; ; tries++)
        {
            var drive = FreeLetter();
            try
            {
                Create(drive, target);
                return drive;
            }
            catch (InvalidOperationException) when (tries < 5 && Directory.Exists(drive + @"\"))
            {
                // taken meanwhile: the next free one
            }
        }
    }

    public static void Remove(string drive) => Run($"{drive} /D");

    private static void Run(string arguments)
    {
        var start = new ProcessStartInfo("subst.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"subst {arguments} failed: {process.StandardOutput.ReadToEnd()}{process.StandardError.ReadToEnd()}");
    }
}
