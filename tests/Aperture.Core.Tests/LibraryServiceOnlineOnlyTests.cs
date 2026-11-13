using Aperture.Core.Indexing;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The online-only question through the library's tracked runs (0.9.2 C1, C7): the run awaits the answer, a switch or
/// an exit never waits on an unanswered question, and an answer that never comes holds nothing up.
/// </summary>
public class LibraryServiceOnlineOnlyTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static (TempDir Data, TempDir Photos, LibraryService Library, Models.Root Root) Library()
    {
        var data = new TempDir();
        var photos = new TempDir();
        TestImages.Write(photos.Combine("here.jpg"), 32, 24);
        TestImages.Write(photos.Combine("cloud.jpg"), 32, 24);
        File.SetAttributes(photos.Combine("cloud.jpg"), File.GetAttributes(photos.Combine("cloud.jpg")) | FileAttributes.Offline);
        var library = new LibraryService(data.Path);
        return (data, photos, library, library.AddRoot(photos.Path, "Photos"));
    }

    [Fact]
    public async Task ARun_AwaitsTheAnswer_AndDoesWhatItSays()
    {
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        var answer = new TaskCompletionSource<OnlineOnlyChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnlineOnlyQuestion? asked = null;

        var run = library.IndexRootAsync(root, null, default, (q, _) => { asked = q; return answer.Task; });
        Assert.True(SpinWait.SpinUntil(() => asked is not null, Wait));
        Assert.False(run.IsCompleted);
        answer.SetResult(OnlineOnlyChoice.Skip);
        var result = await run.WaitAsync(Wait);

        Assert.Equal((1, 1), (asked!.Count, result.SkippedOnlineOnly));
        Assert.Equal(["here.jpg"], library.GetUnion().Select(r => r.Item.FileName));
        await library.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_WhileAQuestionWaits_DoesNotHang()
    {
        // An answer that never comes (the dialog still open, or an asker that ignores the token): a switch goes on.
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        var asked = false;
        var run = library.IndexRootAsync(root, null, default, (_, _) =>
        {
            asked = true;
            return new TaskCompletionSource<OnlineOnlyChoice>().Task; // never answered, deaf to the token
        });
        Assert.True(SpinWait.SpinUntil(() => asked, Wait));

        await library.ShutdownAsync().WaitAsync(Wait);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        using var reopened = new LibraryService(data.Path);
        Assert.Empty(reopened.GetUnion()); // nothing was read or written
    }

    [Fact]
    public async Task APausedRun_GivesUpItsQuestion()
    {
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        using var pause = new CancellationTokenSource();
        var asked = false;
        var run = library.IndexRootAsync(root, null, pause.Token, (_, _) =>
        {
            asked = true;
            return new TaskCompletionSource<OnlineOnlyChoice>().Task;
        });
        Assert.True(SpinWait.SpinUntil(() => asked, Wait));

        pause.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Wait));
        Assert.Empty(library.GetUnion());
        await library.ShutdownAsync();
    }

    [Fact]
    public void SyncDispose_WhileAQuestionWaits_DoesNotHang()
    {
        var (data, photos, library, root) = Library();
        using var _ = data;
        using var __ = photos;
        var asked = false;
        var run = library.IndexRootAsync(root, null, default, (_, _) =>
        {
            asked = true;
            return new TaskCompletionSource<OnlineOnlyChoice>().Task;
        });
        Assert.True(SpinWait.SpinUntil(() => asked, Wait));

        library.Dispose(); // process exit

        Assert.True(SpinWait.SpinUntil(() => run.IsCompleted, Wait));
        Assert.True(run.IsCanceled || run.IsFaulted);
    }
}
