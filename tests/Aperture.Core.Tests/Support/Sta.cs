using System.Runtime.ExceptionServices;

namespace Aperture.Core.Tests.Support;

/// <summary>Runs a test body on its own STA thread, as WPF elements require, and rethrows what it throws.</summary>
public static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
