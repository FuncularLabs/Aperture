using System.Text.Json;

namespace Aperture.Core.Tests;

/// <summary>
/// Transitive packages that carry a known advisory are pinned to a patched version in
/// Directory.Build.props. Restore warns (NU1902/NU1903) but does not fail, so a pin can
/// silently fall away in a package bump. These tests read this project's restore graph
/// (obj/project.assets.json, the graph NuGet audits) and fail if a pinned package is
/// back inside an advisory's vulnerable range.
///
/// The loaded assembly is not a usable witness: the test host runs with the
/// Microsoft.WindowsDesktop.App framework (via Aperture.App), which carries its own
/// System.Security.Cryptography.Pkcs and wins over any lower package version, so what
/// loads reflects the installed runtime, not the pin.
/// </summary>
public class DependencyAdvisoryTests
{
    private const string PkcsPackage = "System.Security.Cryptography.Pkcs";

    [Fact]
    public void Pkcs_RestoredPackageVersion_IsOutsideTheDenialOfServiceAdvisoryRanges()
    {
        // Funcular.Data.Orm -> Microsoft.Data.SqlClient 6.0.0 -> System.Security.Cryptography.Pkcs 9.0.0.
        // GHSA-846f-f764-76mx (high) and GHSA-2fmv-rv2c-59x7 (moderate) cover
        // 8.0.0–8.0.1, 9.0.0–9.0.20 and 10.0.0–10.0.12.
        var graph = RestoredPackages();
        Assert.Contains(graph.Keys, id => id == "Microsoft.Data.SqlClient"); // the graph is real and reaches Pkcs

        // A project that gets Pkcs from a shared framework prunes the package from its
        // graph entirely; that is not a vulnerable package and the test has nothing to say.
        if (!graph.TryGetValue(PkcsPackage, out var version))
            return;

        var vulnerable =
            version.Major < 8 ||
            (version.Major == 8 && version.Minor == 0 && version.Build <= 1) ||
            (version.Major == 9 && version.Minor == 0 && version.Build <= 20) ||
            (version.Major == 10 && version.Minor == 0 && version.Build <= 12);

        Assert.False(vulnerable,
            $"{PkcsPackage} {version} is inside an advisory range; " +
            "the pin in Directory.Build.props is missing or too low.");
    }

    [Fact]
    public void RestoredPackages_ReadsEveryLibraryOfThisProjectsGraph()
    {
        // The graph reader must see transitive packages, or the Pkcs test above would
        // pass vacuously by never finding the package.
        var graph = RestoredPackages();

        Assert.True(graph.ContainsKey("Funcular.Data.Orm"), "direct package of Aperture.Core missing");
        Assert.True(graph.ContainsKey("Microsoft.Data.SqlClient"), "transitive package missing");
        Assert.True(graph.ContainsKey("SQLitePCLRaw.bundle_e_sqlite3"), "Directory.Build.props pin missing");
        Assert.Equal(new Version(3, 0, 3), graph["SQLitePCLRaw.bundle_e_sqlite3"]);
    }

    /// <summary>
    /// Package id -> version for every package in this test project's restore graph.
    /// The assets file sits in obj/ three levels above bin/{Configuration}/{TFM}/.
    /// </summary>
    private static Dictionary<string, Version> RestoredPackages()
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");
        Assert.True(File.Exists(assetsFile), "restore graph not found at " + assetsFile);

        using var doc = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var packages = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;

            var slash = library.Name.IndexOf('/');
            Assert.True(slash > 0, "unexpected library key " + library.Name);
            var id = library.Name[..slash];
            var text = library.Name[(slash + 1)..];
            var dash = text.IndexOf('-');
            if (dash >= 0) text = text[..dash]; // prerelease label

            Assert.True(Version.TryParse(text, out var version), $"unparseable version in '{library.Name}'");
            packages[id] = version!;
        }

        return packages;
    }
}
