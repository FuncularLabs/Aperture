using Aperture.App.Updates;

var repo = ".";
string? versionOverride = null;
string? tagsFile = null;
var writeGithub = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    string Next() => i + 1 < args.Length
        ? args[++i]
        : throw new ArgumentException($"Missing value for {arg}");

    switch (arg)
    {
        case "--repo":
            repo = Next();
            break;
        case "--version":
            versionOverride = Next();
            break;
        case "--tags-file":
            tagsFile = Next();
            break;
        case "--github-output":
            writeGithub = true;
            break;
        default:
            throw new ArgumentException($"Unknown argument: {arg}");
    }
}

var version = versionOverride ?? ReleaseTag.ReadLandedVersion(repo);
if (string.IsNullOrWhiteSpace(version) || UpdateVersion.Parse(version) is null)
{
    Console.Error.WriteLine(
        "No parseable <Version> in src/Aperture.App/Aperture.App.csproj or Directory.Build.props.");
    return 1;
}

IEnumerable<string> tags = tagsFile is null
    ? Array.Empty<string>()
    : File.ReadAllLines(tagsFile);

var latest = ReleaseTag.LatestTag(tags);
var tag = ReleaseTag.TagToCreate(version, tags);
var shouldTag = tag is not null;

var reason = tag is not null
    ? latest is null
        ? $"{version} is the first v* tag"
        : $"{version} is greater than {latest}"
    : latest is not null && string.Equals(ReleaseTag.TagNameFor(version), latest, StringComparison.Ordinal)
        ? $"{version} already has tag {latest}"
        : latest is not null && tags.Any(t => string.Equals(t.Trim(), ReleaseTag.TagNameFor(version), StringComparison.Ordinal))
            ? $"tag {ReleaseTag.TagNameFor(version)} already exists"
            : latest is null
                ? $"{version} is not taggable"
                : $"{version} is not greater than {latest}";

Console.WriteLine($"version={version}");
Console.WriteLine($"latest_tag={latest ?? ""}");
Console.WriteLine($"should_tag={shouldTag.ToString().ToLowerInvariant()}");
Console.WriteLine($"tag={tag ?? ""}");
Console.WriteLine($"reason={reason}");

if (writeGithub)
{
    var output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (string.IsNullOrWhiteSpace(output))
        throw new InvalidOperationException("--github-output set but GITHUB_OUTPUT is empty.");
    File.AppendAllLines(output, new[]
    {
        $"version={version}",
        $"latest_tag={latest ?? ""}",
        $"should_tag={shouldTag.ToString().ToLowerInvariant()}",
        $"tag={tag ?? ""}",
        $"reason={reason}",
    });
}

return 0;
