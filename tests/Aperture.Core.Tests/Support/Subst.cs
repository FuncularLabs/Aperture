using System.Diagnostics;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// A drive letter for a folder (subst, no admin rights needed), so a test can unplug a "drive" the way a USB drive goes:
/// the letter itself is gone, not just a folder on it. Always <see cref="Remove"/> it in a finally.
/// </summary>
public static class Subst
{
    /// <summary>A letter no drive (local, mapped or substituted) has now, from Z: down.</summary>
    public static string FreeLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'H'; letter--)
        {
            if (!used.Contains(letter))
                return $"{letter}:";
        }
        throw new InvalidOperationException("no free drive letter");
    }

    public static void Create(string drive, string target) => Run($"{drive} \"{target}\"");

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
