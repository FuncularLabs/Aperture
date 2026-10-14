using System.Text;

namespace Aperture.App;

/// <summary>
/// Words for a startup that didn't happen. 0.8.6-beta1 died before its window with a
/// TypeInitializationException whose inner DllNotFoundException named e_sqlite3; the
/// crash log had it, the user saw nothing. The text here goes in a MessageBox, so it
/// is the exception chain — type and message at each level, outermost first — and
/// where the full stack trace was written, not the trace itself.
/// </summary>
internal static class StartupGuard
{
    public const string Title = "Aperture couldn't start";

    public static string Describe(Exception ex, string crashLogPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Aperture hit an error before it could open its window and has closed.");
        sb.AppendLine();

        var depth = 0;
        for (Exception? e = ex; e is not null; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.Append(new string(' ', depth * 2)).Append("caused by ");
            sb.Append(e.GetType().Name).Append(": ").AppendLine(e.Message);
        }

        sb.AppendLine();
        sb.Append("Full details were written to ").Append(crashLogPath);
        return sb.ToString();
    }
}
