namespace Aperture.Core.Tests.Support;

/// <summary>A throwaway directory under the system temp path, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "aperture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort; temp cleanup isn't worth failing a test over */ }
    }
}
