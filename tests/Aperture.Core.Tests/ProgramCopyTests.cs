using Aperture.App;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The install and restore copies must carry every native DLL that sits beside the
/// exe. 0.8.6-beta1 moved only Aperture.exe to %LocalAppData%\Programs\Aperture, so
/// a publish whose natives were loose files (e_sqlite3.dll, libSkiaSharp.dll,
/// Microsoft.Data.SqlClient.SNI.dll) crashed at startup with DllNotFoundException.
/// </summary>
public class ProgramCopyTests
{
    private static string Write(string dir, string name, string content)
    {
        var p = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void CopyProgram_CopiesEveryDllBesideTheExe_AndNothingElse()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        Write(src.Path, "e_sqlite3.dll", "sqlite");
        Write(src.Path, "libSkiaSharp.dll", "skia");
        Write(src.Path, "Microsoft.Data.SqlClient.SNI.dll", "sni");
        Write(src.Path, "Aperture.pdb", "pdb");
        Write(src.Path, "README.md", "readme");
        Write(src.Path, Path.Combine("docs", "nested.dll"), "not a sibling");

        var destExe = dst.Combine("Aperture.exe");
        RegistrationService.CopyProgram(exe, destExe);

        Assert.Equal("exe", File.ReadAllText(destExe));
        Assert.Equal("sqlite", File.ReadAllText(dst.Combine("e_sqlite3.dll")));
        Assert.Equal("skia", File.ReadAllText(dst.Combine("libSkiaSharp.dll")));
        Assert.Equal("sni", File.ReadAllText(dst.Combine("Microsoft.Data.SqlClient.SNI.dll")));
        Assert.False(File.Exists(dst.Combine("Aperture.pdb")));
        Assert.False(File.Exists(dst.Combine("README.md")));
        Assert.False(File.Exists(dst.Combine("nested.dll")));
        Assert.False(Directory.Exists(dst.Combine("docs")));
    }

    [Fact]
    public void CopyProgram_SingleFileBundle_CopiesJustTheExe()
    {
        // A self-extracting single-file publish has no loose natives at all.
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "bundle");

        RegistrationService.CopyProgram(exe, dst.Combine("Aperture.exe"));

        Assert.Equal(["Aperture.exe"], Directory.GetFiles(dst.Path).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void CopyProgram_OverwritesStaleNativesAtTheDestination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "new exe");
        Write(src.Path, "e_sqlite3.dll", "new sqlite");
        Write(dst.Path, "Aperture.exe", "old exe");
        Write(dst.Path, "e_sqlite3.dll", "old sqlite");

        RegistrationService.CopyProgram(exe, dst.Combine("Aperture.exe"));

        Assert.Equal("new exe", File.ReadAllText(dst.Combine("Aperture.exe")));
        Assert.Equal("new sqlite", File.ReadAllText(dst.Combine("e_sqlite3.dll")));
    }

    [Fact]
    public void CopyProgram_CreatesTheDestinationFolder()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        Write(src.Path, "e_sqlite3.dll", "sqlite");
        var destExe = dst.Combine("Programs", "Aperture", "Aperture.exe");

        RegistrationService.CopyProgram(exe, destExe);

        Assert.True(File.Exists(destExe));
        Assert.True(File.Exists(dst.Combine("Programs", "Aperture", "e_sqlite3.dll")));
    }

    [Fact]
    public void CopyProgram_FailsLoudly_AndLeavesTheOldExe_WhenANativeCannotBeWritten()
    {
        // Natives go first: a native that can't land must not leave a NEW exe beside
        // OLD (or no) natives — that is exactly the silent-crash install.
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "new exe");
        Write(src.Path, "e_sqlite3.dll", "new sqlite");
        Write(dst.Path, "Aperture.exe", "old exe");
        var lockedDll = Write(dst.Path, "e_sqlite3.dll", "old sqlite");

        using (new FileStream(lockedDll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.ThrowsAny<IOException>(
                () => RegistrationService.CopyProgram(exe, dst.Combine("Aperture.exe")));
            Assert.Contains("e_sqlite3.dll", ex.Message);
        }

        Assert.Equal("old exe", File.ReadAllText(dst.Combine("Aperture.exe")));
    }

    [Fact]
    public void CopyProgram_SameFile_IsANoOp()
    {
        using var dir = new TempDir();
        var exe = Write(dir.Path, "Aperture.exe", "exe");
        Write(dir.Path, "e_sqlite3.dll", "sqlite");

        RegistrationService.CopyProgram(exe, exe);

        Assert.Equal("exe", File.ReadAllText(exe));
        Assert.Equal("sqlite", File.ReadAllText(dir.Combine("e_sqlite3.dll")));
    }

    [Fact]
    public void NativeSiblings_ListsOnlyTopLevelDlls_SortedByName()
    {
        using var src = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        Write(src.Path, "libSkiaSharp.dll", "skia");
        Write(src.Path, "e_sqlite3.dll", "sqlite");
        Write(src.Path, "libSkiaSharp.pdb", "pdb");
        Write(src.Path, "notes.dll.txt", "text");
        Write(src.Path, Path.Combine("docs", "nested.dll"), "nested");

        var names = RegistrationService.NativeSiblings(exe).Select(f => Path.GetFileName(f)).ToArray();

        Assert.Equal(["e_sqlite3.dll", "libSkiaSharp.dll"], names);
    }

    [Fact]
    public void RestoreToOriginal_CarriesTheNativesToo()
    {
        // The reverse copy has the same shape; restoring a bare exe would recreate
        // the bug in the user's Downloads folder.
        using var installed = new TempDir();
        using var original = new TempDir();
        var exe = Write(installed.Path, "Aperture.exe", "exe");
        Write(installed.Path, "e_sqlite3.dll", "sqlite");

        var ok = RegistrationService.RestoreProgram(exe, original.Combine("Aperture.exe"));

        Assert.True(ok);
        Assert.Equal("exe", File.ReadAllText(original.Combine("Aperture.exe")));
        Assert.Equal("sqlite", File.ReadAllText(original.Combine("e_sqlite3.dll")));
    }

    [Fact]
    public void RestoreToOriginal_ReportsFalse_WhenNothingIsInstalled()
    {
        using var original = new TempDir();
        var missingExe = original.Combine("nowhere", "Aperture.exe");

        Assert.False(RegistrationService.RestoreProgram(missingExe, original.Combine("Aperture.exe")));
    }
}
