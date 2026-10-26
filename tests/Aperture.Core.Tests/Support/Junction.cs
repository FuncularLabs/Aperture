using System.Diagnostics;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// Creates an NTFS directory junction (no admin rights needed), so tests can reach one physical folder by two
/// different paths — the way a user might through a junction, a mapped drive, or an 8.3 short name.
/// </summary>
public static class Junction
{
    public static void Create(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J failed: {process.StandardError.ReadToEnd()}");
    }
}
