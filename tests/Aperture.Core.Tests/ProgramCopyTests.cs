using Aperture.App;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The install and restore copies must carry Aperture's native libraries when they
/// sit loose beside the exe. 0.8.6-beta1 moved only Aperture.exe to
/// %LocalAppData%\Programs\Aperture, so a publish whose natives were loose files
/// (e_sqlite3.dll, libSkiaSharp.dll, Microsoft.Data.SqlClient.SNI.dll) crashed at
/// startup with DllNotFoundException. And ONLY those: the exe usually runs from
/// Downloads, and the other DLLs there are not ours to copy, block on, or restore over.
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
    public void KnownNativeLibraries_AreExactlyTheNativeAssetsTheBuildProduces()
    {
        // Drift guard: a package that adds (or drops) a native DLL must change the
        // list, or an un-bundled publish installs without it again.
        var nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        Assert.True(Directory.Exists(nativeDir), "expected native assets at " + nativeDir);

        var built = Directory.GetFiles(nativeDir, "*.dll")
            .Select(f => Path.GetFileName(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var known = RegistrationService.KnownNativeLibraries
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(known, built, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyProgram_CopiesTheKnownNatives_AndNothingElseFromTheFolder()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        Write(src.Path, "e_sqlite3.dll", "sqlite");
        Write(src.Path, "libSkiaSharp.dll", "skia");
        Write(src.Path, "Microsoft.Data.SqlClient.SNI.dll", "sni");
        Write(src.Path, "version.dll", "something else that landed in Downloads");
        Write(src.Path, "OtherApp.dll", "another program's file");
        Write(src.Path, "Aperture.pdb", "pdb");
        Write(src.Path, "README.md", "readme");
        Write(src.Path, Path.Combine("docs", "nested.dll"), "not a sibling");

        var destExe = dst.Combine("Aperture.exe");
        RegistrationService.CopyProgram(exe, destExe);

        Assert.Equal("exe", File.ReadAllText(destExe));
        Assert.Equal("sqlite", File.ReadAllText(dst.Combine("e_sqlite3.dll")));
        Assert.Equal("skia", File.ReadAllText(dst.Combine("libSkiaSharp.dll")));
        Assert.Equal("sni", File.ReadAllText(dst.Combine("Microsoft.Data.SqlClient.SNI.dll")));
        var copied = Directory.GetFiles(dst.Path).Select(f => Path.GetFileName(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(["Aperture.exe", "e_sqlite3.dll", "libSkiaSharp.dll", "Microsoft.Data.SqlClient.SNI.dll"], copied);
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
            Assert.Contains("Couldn't copy e_sqlite3.dll", ex.Message);
            Assert.Contains("the program file was not replaced", ex.Message);
            Assert.NotNull(ex.InnerException);
        }

        Assert.Equal("old exe", File.ReadAllText(dst.Combine("Aperture.exe")));
    }

    [Fact]
    public void CopyProgram_FailsLoudly_AndLeavesTheOldExe_WhenANativeDidNotLand()
    {
        // The copy "succeeds" but the file is not there afterwards — what a
        // quarantine-on-write scanner looks like. The exe must still be the old one.
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "new exe");
        Write(src.Path, "libSkiaSharp.dll", "new skia");
        Write(dst.Path, "Aperture.exe", "old exe");
        var copies = new List<string>();

        var ex = Assert.Throws<IOException>(() => RegistrationService.CopyProgram(
            exe, dst.Combine("Aperture.exe"),
            copy: (from, to) => copies.Add(Path.GetFileName(to)) /* writes nothing */));

        Assert.Contains("libSkiaSharp.dll", ex.Message);
        Assert.Contains("did not land", ex.Message);
        Assert.Equal(["libSkiaSharp.dll"], copies); // the exe copy was never attempted
        Assert.Equal("old exe", File.ReadAllText(dst.Combine("Aperture.exe")));
    }

    [Fact]
    public void CopyProgram_IgnoresAnUnrelatedLockedDllBesideTheExe()
    {
        // A foreign DLL in Downloads that can't even be read must not block the
        // install — Aperture never needed it.
        using var src = new TempDir();
        using var dst = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        var foreign = Write(src.Path, "version.dll", "foreign");

        using (new FileStream(foreign, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            RegistrationService.CopyProgram(exe, dst.Combine("Aperture.exe"));
        }

        Assert.Equal("exe", File.ReadAllText(dst.Combine("Aperture.exe")));
        Assert.False(File.Exists(dst.Combine("version.dll")));
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
    public void NativeSiblings_ListsOnlyKnownNativesThatArePresent()
    {
        using var src = new TempDir();
        var exe = Write(src.Path, "Aperture.exe", "exe");
        Write(src.Path, "libSkiaSharp.dll", "skia");
        Write(src.Path, "E_SQLITE3.DLL", "sqlite, in whatever case the filesystem kept");
        Write(src.Path, "libSkiaSharp.pdb", "pdb");
        Write(src.Path, "version.dll", "foreign");
        Write(src.Path, Path.Combine("docs", "Microsoft.Data.SqlClient.SNI.dll"), "nested, not beside");

        var names = RegistrationService.NativeSiblings(exe)
            .Select(f => Path.GetFileName(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(["e_sqlite3.dll", "libSkiaSharp.dll"], names, StringComparer.OrdinalIgnoreCase);
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

    [Fact]
    public void RestoreToOriginal_ReportsFalse_WhenTheCopyFails()
    {
        using var installed = new TempDir();
        using var original = new TempDir();
        var exe = Write(installed.Path, "Aperture.exe", "exe");
        Write(installed.Path, "e_sqlite3.dll", "sqlite");
        var locked = Write(original.Path, "e_sqlite3.dll", "in use");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(RegistrationService.RestoreProgram(exe, original.Combine("Aperture.exe")));
        }

        Assert.False(File.Exists(original.Combine("Aperture.exe")));
    }
}
