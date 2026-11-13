using Aperture.Core.Indexing;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The question a run asks before it reads online-only files, and what each answer does (0.9.2 C1–C4; Paul: "prompt
/// user that an indexing operation includes online-only cloud files, should it skip, do in background, or do
/// immediately?"). Online-only files are made with the Offline attribute, which a placeholder carries.
/// </summary>
public class IndexerOnlineOnlyTests
{
    private static void MakeOnlineOnly(string path) => File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

    private sealed class SynchronousSink(List<IndexProgress> sink) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => sink.Add(value);
    }

    // here.jpg, then cloud1.jpg and cloud2.jpg online only (plus any more names given).
    private static (ApertureScope Scope, Root Root, List<string> Reads) Library(params string[] more)
    {
        var scope = new ApertureScope();
        foreach (var name in new[] { "here.jpg", "cloud1.jpg", "cloud2.jpg" }.Concat(more))
            TestImages.Write(scope.Library.Combine(name), 64, 48);
        foreach (var name in new[] { "cloud1.jpg", "cloud2.jpg" }.Concat(more.Where(m => m.StartsWith("cloud"))))
            MakeOnlineOnly(scope.Library.Combine(name));
        var reads = new List<string>();
        scope.Indexer.Reading = reads.Add;
        return (scope, scope.AddLibraryRoot(), reads);
    }

    private static IndexResult Run(
        ApertureScope scope, Root root, OnlineOnlyChoice answer, List<IndexProgress>? reports = null,
        List<OnlineOnlyQuestion>? asked = null) =>
        scope.Indexer.IndexRoot(root, reports is null ? null : new SynchronousSink(reports), default, true, (q, _) =>
        {
            asked?.Add(q);
            return answer;
        });

    private static List<string> Names(ApertureScope scope, Root root) =>
        [.. scope.Items.GetForRoot(root.Id).Select(i => i.FileName).Order()];

    [Fact]
    public void ARunThatReadsOnlineOnlyFiles_AsksOnce_BeforeReadingAny()
    {
        var (scope, root, reads) = Library();
        using var _ = scope;
        var reports = new List<IndexProgress>();
        var asked = new List<(OnlineOnlyQuestion Question, int ReadsSoFar, int Rows, int IndexingReports)>();

        scope.Indexer.IndexRoot(root, new SynchronousSink(reports), default, true, (q, _) =>
        {
            asked.Add((q, reads.Count, scope.Items.CountForRoot(root.Id), reports.Count(r => r.Phase == IndexPhase.Indexing)));
            return OnlineOnlyChoice.Now;
        });

        var (question, readsSoFar, rows, indexingReports) = Assert.Single(asked);
        Assert.Equal(0, readsSoFar);       // before any file is read…
        Assert.Equal(0, rows);             // …or written…
        Assert.Equal(0, indexingReports);  // …and before the run says how many it will download
        Assert.Equal(2, question.Count);   // the two it would download
        Assert.Equal(root.Id, question.Root.Id);
        Assert.Equal(3, reads.Count);      // Now: and then all three read
    }

    [Fact]
    public async Task TheAsyncRun_HoldsNoThread_WhileItsQuestionWaits()
    {
        // Review gate 3: the question waits for the user; a run must not hold a thread meanwhile (every folder of a
        // library can ask at once, on a two-CPU machine). The call hands back its task with the question unanswered.
        var (scope, root, reads) = Library();
        using var _ = scope;
        var answer = new TaskCompletionSource<OnlineOnlyChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;

        // StartNew, not Task.Run: Task.Run would unwrap the run's task and wait for it.
        var started = Task.Factory.StartNew(() => scope.Indexer.IndexRootAsync(root, null, default, true, (q, _) =>
        {
            asked++;
            return answer.Task;
        }), CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var handedBack = started.Wait(TimeSpan.FromSeconds(20));
        answer.TrySetResult(OnlineOnlyChoice.Skip); // let a blocking implementation go, so the test ends either way

        Assert.True(handedBack, "the run held its thread while the question waited");
        var run = await started;
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, asked);
        Assert.Equal(["here.jpg"], reads);
        Assert.Equal(2, result.SkippedOnlineOnly);
    }

    [Fact]
    public async Task TheAsyncRun_WithNothingToAsk_IsTheSameRun()
    {
        var (scope, root, reads) = Library();
        using var _ = scope;
        File.SetAttributes(scope.Library.Combine("cloud1.jpg"), FileAttributes.Normal);
        File.SetAttributes(scope.Library.Combine("cloud2.jpg"), FileAttributes.Normal);

        var result = await scope.Indexer.IndexRootAsync(root, null, default, true,
            (_, _) => throw new InvalidOperationException("nothing to ask"));

        Assert.Equal(3, result.Added);
        Assert.Equal(3, reads.Count);
    }

    [Fact]
    public void ARunThatReadsNone_DoesNotAsk()
    {
        using var scope = new ApertureScope();
        TestImages.Write(scope.Library.Combine("here.jpg"), 64, 48);
        TestImages.Write(scope.Library.Combine("cloud.jpg"), 64, 48);
        var root = scope.AddLibraryRoot();
        var asked = new List<OnlineOnlyQuestion>();

        Run(scope, root, OnlineOnlyChoice.Now, asked: asked);  // nothing online only: no question
        Assert.Empty(asked);

        MakeOnlineOnly(scope.Library.Combine("cloud.jpg"));     // indexed already, unchanged, thumbnails fresh
        File.SetLastWriteTimeUtc(scope.Library.Combine("cloud.jpg"),
            scope.Items.GetForRoot(root.Id).Single(i => i.FileName == "cloud.jpg").MTimeUtc);
        Run(scope, root, OnlineOnlyChoice.Now, asked: asked);  // it would read none of them: no question
        Assert.Empty(asked);
    }

    [Fact]
    public void Skip_ReadsNoneOfThem_LeavesNewOnesOut_AndIndexesTheRest()
    {
        var (scope, root, reads) = Library();
        using var _ = scope;
        var reports = new List<IndexProgress>();

        var result = Run(scope, root, OnlineOnlyChoice.Skip, reports);

        Assert.Equal(["here.jpg"], reads);
        Assert.Equal(["here.jpg"], Names(scope, root));
        Assert.Equal(2, result.SkippedOnlineOnly);
        Assert.Equal(1, result.Added);
        Assert.All(reports, r => Assert.Equal(0, r.OnlineOnly)); // the line doesn't count them
        Assert.All(reports.Where(r => r.Phase is IndexPhase.Indexing or IndexPhase.Done), r => Assert.Equal(1, r.Total));
        var done = reports.Single(r => r.Phase == IndexPhase.Done);
        Assert.Equal((1, 1), (done.Processed, done.Total));
    }

    [Fact]
    public void Skip_KeepsTheRowsOfOnesAlreadyIndexed_AsTheyWere()
    {
        var (scope, root, reads) = Library();
        using var _ = scope;
        Run(scope, root, OnlineOnlyChoice.Now);
        var before = scope.Items.GetForRoot(root.Id).ToDictionary(i => i.FileName, i => i.MTimeUtc);
        var changed = scope.Library.Combine("cloud2.jpg");
        File.SetLastWriteTimeUtc(changed, File.GetLastWriteTimeUtc(changed).AddMinutes(1));      // changed
        scope.Thumbnails.DeleteForItems([.. scope.Items.GetForRoot(root.Id)
            .Where(i => i.FileName == "cloud1.jpg").Select(i => i.Id)]);                       // thumbnails gone
        reads.Clear();
        var asked = new List<OnlineOnlyQuestion>();

        var result = Run(scope, root, OnlineOnlyChoice.Skip, asked: asked);

        Assert.Equal(2, Assert.Single(asked).Count);
        Assert.Empty(reads);                                    // neither downloaded
        Assert.Equal(["cloud1.jpg", "cloud2.jpg", "here.jpg"], Names(scope, root)); // never pruned for being skipped
        Assert.Equal(0, result.Removed);
        Assert.Equal(before["cloud2.jpg"], scope.Items.GetForRoot(root.Id).Single(i => i.FileName == "cloud2.jpg").MTimeUtc);
        Assert.Equal(2, result.SkippedOnlineOnly);
    }

    [Fact]
    public void Skip_StillPrunesWhatIsGone()
    {
        var (scope, root, _) = Library("there.jpg");
        using var __ = scope;
        Run(scope, root, OnlineOnlyChoice.Now);
        File.Delete(scope.Library.Combine("there.jpg"));
        File.SetLastWriteTimeUtc(scope.Library.Combine("cloud1.jpg"), DateTime.UtcNow.AddMinutes(-5)); // to read again

        var result = Run(scope, root, OnlineOnlyChoice.Skip);

        Assert.Equal(1, result.Removed);
        Assert.Equal(["cloud1.jpg", "cloud2.jpg", "here.jpg"], Names(scope, root));
    }

    [Fact]
    public void Background_ReadsTheRestFirst_ThenThem()
    {
        var (scope, root, reads) = Library("cloud0.jpg", "a.jpg", "z.jpg");
        using var _ = scope;
        var reports = new List<IndexProgress>();

        Run(scope, root, OnlineOnlyChoice.Background, reports);

        var order = FileScanner.Scan(root.Path).Select(f => f.RelPath).ToList();
        var local = order.Where(n => !n.StartsWith("cloud")).ToList();
        var cloud = order.Where(n => n.StartsWith("cloud")).ToList();
        Assert.Equal([.. local, .. cloud], reads);              // the rest in their usual order, then them, in theirs
        Assert.Equal(6, Names(scope, root).Count);
        Assert.All(reports.Where(r => r.Phase == IndexPhase.Indexing),
            r => Assert.Equal((3, true, 6), (r.OnlineOnly, r.OnlineOnlyLast, r.Total))); // "after the rest"
    }

    [Fact]
    public void Now_ReadsInOrder_AsBefore()
    {
        var (scope, root, reads) = Library("cloud0.jpg", "a.jpg", "z.jpg");
        using var _ = scope;
        var reports = new List<IndexProgress>();

        Run(scope, root, OnlineOnlyChoice.Now, reports);

        Assert.Equal(FileScanner.Scan(root.Path).Select(f => f.RelPath), reads);
        Assert.All(reports.Where(r => r.Phase == IndexPhase.Indexing),
            r => Assert.Equal((3, false), (r.OnlineOnly, r.OnlineOnlyLast)));
    }

    [Fact]
    public void WithoutAQuestion_ItsNow()
    {
        var (scope, root, reads) = Library("cloud0.jpg", "a.jpg");
        using var _ = scope;

        scope.Indexer.IndexRoot(root);

        Assert.Equal(FileScanner.Scan(root.Path).Select(f => f.RelPath), reads);
    }

    [Fact]
    public void AQuestionGivenUp_EndsTheRun_ReadingNothing_AndPruningNothing()
    {
        // C7: a run paused or stopped, or a library closing, while its question waits: the asker throws on the token.
        var (scope, root, reads) = Library("there.jpg");
        using var _ = scope;
        Run(scope, root, OnlineOnlyChoice.Now);
        File.Delete(scope.Library.Combine("there.jpg"));
        File.SetLastWriteTimeUtc(scope.Library.Combine("cloud1.jpg"), DateTime.UtcNow.AddMinutes(-5));
        reads.Clear();
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => scope.Indexer.IndexRoot(root, null, cts.Token, true, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return OnlineOnlyChoice.Now;
        }));

        Assert.Empty(reads);
        Assert.Equal(4, Names(scope, root).Count); // there.jpg's row stays: a run that ended early prunes nothing
    }
}
