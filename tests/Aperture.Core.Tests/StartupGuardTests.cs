using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// A startup failure must be shown, not just logged. 0.8.6-beta1 died silently on a
/// TypeInitializationException whose inner DllNotFoundException named e_sqlite3 — the
/// message the user needs is two levels down the chain.
/// </summary>
public class StartupGuardTests
{
    private const string LogPath = @"C:\Users\someone\AppData\Local\Aperture\crash.log";

    [Fact]
    public void Describe_NamesEveryExceptionInTheChain()
    {
        var ex = new TypeInitializationException(
            "Microsoft.Data.Sqlite.SqliteConnection",
            new DllNotFoundException("Unable to load DLL 'e_sqlite3' or one of its dependencies"));

        var text = StartupGuard.Describe(ex, LogPath);

        Assert.Contains("TypeInitializationException", text);
        Assert.Contains("Microsoft.Data.Sqlite.SqliteConnection", text);
        Assert.Contains("DllNotFoundException", text);
        Assert.Contains("e_sqlite3", text);
    }

    [Fact]
    public void Describe_PointsAtTheCrashLog()
    {
        var text = StartupGuard.Describe(new InvalidOperationException("boom"), LogPath);

        Assert.Contains(LogPath, text);
    }

    [Fact]
    public void Describe_WithoutInner_StillNamesTypeAndMessage()
    {
        var text = StartupGuard.Describe(new InvalidOperationException("boom"), LogPath);

        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Describe_ListsInnerExceptionsInOrder_OutermostFirst()
    {
        var ex = new InvalidOperationException("outer",
            new IOException("middle", new DllNotFoundException("inner")));

        var text = StartupGuard.Describe(ex, LogPath);

        var outer = text.IndexOf("outer", StringComparison.Ordinal);
        var middle = text.IndexOf("middle", StringComparison.Ordinal);
        var inner = text.IndexOf("inner", StringComparison.Ordinal);
        Assert.True(outer >= 0 && outer < middle && middle < inner, text);
    }
}
