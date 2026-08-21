using Aperture.Core.Startup;

namespace Aperture.Core.Tests;

public class WindowsStartupTests
{
    private sealed class DictRunKeyStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string name) => Values.TryGetValue(name, out var v) ? v : null;
        public void SetValue(string name, string data) => Values[name] = data;
        public void DeleteValue(string name) => Values.Remove(name);
    }

    private sealed class ThrowingRunKeyStore : IRunKeyStore
    {
        public string? GetValue(string name) => throw new IOException("registry unavailable");
        public void SetValue(string name, string data) => throw new UnauthorizedAccessException("denied");
        public void DeleteValue(string name) => throw new IOException("registry unavailable");
    }

    private const string Exe = @"C:\Apps\Aperture.exe";
    private const string Other = @"C:\Apps\Other\Aperture.exe";

    [Fact]
    public void FormatCommand_QuotesThePath()
        => Assert.Equal("\"C:\\Apps\\Aperture.exe\"", WindowsStartup.FormatCommand(Exe));

    [Theory]
    [InlineData("\"C:\\Apps\\Aperture.exe\"", @"C:\Apps\Aperture.exe")]
    [InlineData(@"C:\Apps\Aperture.exe", @"C:\Apps\Aperture.exe")]
    [InlineData("\"C:\\Apps\\Aperture.exe\" --minimized", @"C:\Apps\Aperture.exe")]
    [InlineData(@"C:\Apps\Aperture.exe --minimized", @"C:\Apps\Aperture.exe")]
    [InlineData("  \"C:\\Apps\\Aperture.exe\"  ", @"C:\Apps\Aperture.exe")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    public void ExePathFromCommand_ReadsQuotedAndUnquoted(string? stored, string? expected)
        => Assert.Equal(expected, WindowsStartup.ExePathFromCommand(stored));

    [Fact]
    public void PointsAtThisExe_MatchesIgnoringQuotesAndCase()
    {
        Assert.True(WindowsStartup.PointsAtThisExe("\"C:\\Apps\\Aperture.exe\"", Exe));
        Assert.True(WindowsStartup.PointsAtThisExe(Exe, Exe));
        Assert.True(WindowsStartup.PointsAtThisExe("\"C:\\APPS\\APERTURE.EXE\"", Exe));
        Assert.False(WindowsStartup.PointsAtThisExe("\"C:\\Apps\\Other\\Aperture.exe\"", Exe));
        Assert.False(WindowsStartup.PointsAtThisExe(null, Exe));
        Assert.False(WindowsStartup.PointsAtThisExe("", Exe));
    }

    [Fact]
    public void PointsAtThisExe_InvalidPath_DoesNotThrow()
        => Assert.False(WindowsStartup.PointsAtThisExe("\"C:\\Apps\\Aperture.exe\"", ":::not-a-path:::"));

    [Fact]
    public void IsEnabled_TrueOnlyWhenValuePointsAtThisExe()
    {
        var store = new DictRunKeyStore();
        Assert.False(WindowsStartup.IsEnabled(store, Exe));

        store.SetValue(WindowsStartup.ValueName, WindowsStartup.FormatCommand(Other));
        Assert.False(WindowsStartup.IsEnabled(store, Exe));

        store.SetValue(WindowsStartup.ValueName, WindowsStartup.FormatCommand(Exe));
        Assert.True(WindowsStartup.IsEnabled(store, Exe));
        Assert.False(WindowsStartup.IsEnabled(store, Other));
    }

    [Fact]
    public void IsEnabled_MissingExePath_IsFalse()
        => Assert.False(WindowsStartup.IsEnabled(new DictRunKeyStore(), null));

    [Fact]
    public void TrySetEnabled_WritesQuotedPathAndDeletes()
    {
        var store = new DictRunKeyStore();
        Assert.True(WindowsStartup.TrySetEnabled(store, Exe, enabled: true));
        Assert.Equal("\"C:\\Apps\\Aperture.exe\"", store.GetValue(WindowsStartup.ValueName));
        Assert.True(WindowsStartup.IsEnabled(store, Exe));

        Assert.True(WindowsStartup.TrySetEnabled(store, Exe, enabled: false));
        Assert.Null(store.GetValue(WindowsStartup.ValueName));
        Assert.False(WindowsStartup.IsEnabled(store, Exe));
    }

    [Fact]
    public void TrySetEnabled_MissingExePath_IsFalseAndWritesNothing()
    {
        var store = new DictRunKeyStore();
        Assert.False(WindowsStartup.TrySetEnabled(store, null, enabled: true));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void RegistryFailures_NeverThrow()
    {
        var store = new ThrowingRunKeyStore();
        Assert.False(WindowsStartup.IsEnabled(store, Exe));
        Assert.False(WindowsStartup.TrySetEnabled(store, Exe, enabled: true));
        Assert.False(WindowsStartup.TrySetEnabled(store, Exe, enabled: false));
    }

    [Fact]
    public void HasValue_TracksPresenceIndependentOfPath()
    {
        var store = new DictRunKeyStore();
        Assert.False(WindowsStartup.HasValue(store));
        store.SetValue(WindowsStartup.ValueName, WindowsStartup.FormatCommand(Other));
        Assert.True(WindowsStartup.HasValue(store));
        Assert.False(WindowsStartup.IsEnabled(store, Exe));
    }

    [Fact]
    public void TryRetargetIfPointsAt_RewritesADownloadPathToTheInstalledExe()
    {
        var store = new DictRunKeyStore();
        var download = @"C:\Users\me\Downloads\Aperture-v0.8.0-beta1-win-x64.exe";
        var installed = @"C:\Users\me\AppData\Local\Programs\Aperture\Aperture.exe";
        WindowsStartup.TrySetEnabled(store, download, enabled: true);

        Assert.True(WindowsStartup.TryRetargetIfPointsAt(store, download, installed));
        Assert.True(WindowsStartup.IsEnabled(store, installed));
        Assert.False(WindowsStartup.IsEnabled(store, download));
        Assert.Equal(WindowsStartup.FormatCommand(installed), store.GetValue(WindowsStartup.ValueName));
    }

    [Fact]
    public void TryRetargetIfPointsAt_DoesNotCreateARunValueTheUserNeverOptedInto()
    {
        var store = new DictRunKeyStore();
        var installed = @"C:\Users\me\AppData\Local\Programs\Aperture\Aperture.exe";
        Assert.False(WindowsStartup.TryRetargetIfPointsAt(store, Exe, installed));
        Assert.False(WindowsStartup.HasValue(store));
    }

    [Fact]
    public void HasValueThenSetEnabled_IsHowAnInstalledLaunchRepointsAStaleDownloadPath()
    {
        // The startup refresh: a Run value exists (pointing at a Downloads exe)
        // and this process is the AppData copy — rewrite, don't leave logon on
        // a path that may already have been deleted by --finish-move.
        var store = new DictRunKeyStore();
        var download = @"C:\Users\me\Downloads\Aperture-v0.8.0-beta1-win-x64.exe";
        var installed = @"C:\Users\me\AppData\Local\Programs\Aperture\Aperture.exe";
        WindowsStartup.TrySetEnabled(store, download, enabled: true);
        Assert.True(WindowsStartup.HasValue(store));
        Assert.True(WindowsStartup.TrySetEnabled(store, installed, enabled: true));
        Assert.True(WindowsStartup.IsEnabled(store, installed));
        Assert.False(WindowsStartup.IsEnabled(store, download));
    }

    [Fact]
    public void TryRetargetIfPointsAt_LeavesADifferentExeAlone()
    {
        var store = new DictRunKeyStore();
        WindowsStartup.TrySetEnabled(store, Other, enabled: true);
        Assert.False(WindowsStartup.TryRetargetIfPointsAt(store, Exe, @"C:\Apps\New\Aperture.exe"));
        Assert.True(WindowsStartup.IsEnabled(store, Other));
    }

    [Fact]
    public void CurrentUserRunKeyStore_RoundTripsAThrowawayValue()
    {
        var store = new CurrentUserRunKeyStore();
        var name = "Aperture-test-" + Guid.NewGuid().ToString("N");
        try
        {
            store.SetValue(name, "\"C:\\Apps\\Aperture.exe\"");
            Assert.Equal("\"C:\\Apps\\Aperture.exe\"", store.GetValue(name));
            store.DeleteValue(name);
            Assert.Null(store.GetValue(name));
            store.DeleteValue(name); // missing: must not throw
        }
        finally
        {
            try { store.DeleteValue(name); } catch { /* leftover */ }
        }
    }
}
