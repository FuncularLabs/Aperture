namespace Reel.Core.Library;

/// <summary>Finds common photo folders to offer on first run.</summary>
public static class FolderDetection
{
    public readonly record struct Candidate(string Path, string Alias);

    public static List<Candidate> DetectCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] probes =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(home, "Dropbox", "Camera Uploads"),
            Path.Combine(home, "OneDrive", "Pictures"),
            Path.Combine(home, "OneDrive", "Pictures", "Camera Roll"),
            Path.Combine(home, "Pictures", "iCloud Photos"),
            Path.Combine(home, "Pictures", "Screenshots"),
        ];

        var results = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in probes)
        {
            if (string.IsNullOrEmpty(path))
                continue;
            try
            {
                if (Directory.Exists(path) && seen.Add(path))
                    results.Add(new Candidate(path, new DirectoryInfo(path).Name));
            }
            catch
            {
                // Inaccessible — skip.
            }
        }

        return results;
    }
}
