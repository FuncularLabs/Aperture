using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Models;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The window stays responsive while folders are indexed (0.9.1 batch, item 3: "Run indexing in the background so the
/// rest of the app stays fully responsive"). The indexer always ran off the UI thread; what froze the window was what
/// each run handed back to it: every row read again (and the grid built) every 1.2 s while a folder streamed in, a
/// folder's count once a second, and the rows and the tree again at every run's end. Those reads now run in the
/// background through one refresh at a time, which merges what's asked for while it runs, and never shows rows older
/// than a change the user made meanwhile. Real view models on real libraries; the library's ReadingContents and
/// ReadingCount hooks see (and hold) the real reads.
/// </summary>
public class IndexingResponsivenessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; }
        public MainViewModel Vm { get; }
        public int UiThread { get; } = Environment.CurrentManagedThreadId;

        // Every contents and count read the library makes from now on, with the thread it ran on.
        public List<int> ContentReads { get; } = [];
        public List<int> CountReads { get; } = [];

        // Set to hold the next contents read (after its queries) until Release; it records whether the test let it go.
        private ManualResetEventSlim? _hold;
        public ManualResetEventSlim Held { get; } = new();
        public bool HoldTimedOut { get; private set; }

        public Fixture(params string[] files)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            foreach (var file in files)
                TestImages.Write(Path.Combine(Photos.Path, file), 32, 24);
            Library = new LibraryService(Data.Path);
            if (files.Length > 0)
                Library.IndexRoot(Library.AddRoot(Photos.Path, "Photos"));
            Vm = new MainViewModel(Library, new ThumbnailService(Library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            Assert.True(Sta.PumpUntil(() => Vm.LoadedItemCount is not null && !Vm.IsLoadingView && Vm.RefreshIdle.IsCompleted, Wait));
            Library.ReadingContents = () =>
            {
                lock (ContentReads)
                    ContentReads.Add(Environment.CurrentManagedThreadId);
                var hold = Interlocked.Exchange(ref _hold, null);
                if (hold is null)
                    return;
                Held.Set();
                HoldTimedOut = !hold.Wait(TimeSpan.FromSeconds(5));
            };
            Library.ReadingCount = _ =>
            {
                lock (CountReads)
                    CountReads.Add(Environment.CurrentManagedThreadId);
            };
        }

        public ManualResetEventSlim HoldNextRead()
        {
            var release = new ManualResetEventSlim();
            Held.Reset();
            _hold = release;
            return release;
        }

        public bool Settled() => !Vm.IsIndexing && Vm.RefreshIdle.IsCompleted && !Vm.IsLoadingView;

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    // A fresh library opens its top-level folders by default; the tests that need a folder's expansion remembered expand
    // it as the user would: one that was closed, opened.
    private static void ExpandAsTheUserDoes(FolderNodeVm node)
    {
        node.IsExpanded = false;
        node.IsExpanded = true;
    }

    private static string[] Photos(int count, string dir = "") =>
        [.. Enumerable.Range(0, count).Select(i => Path.Combine(dir, $"p{i:000}.jpg"))];

    [Fact]
    public void Streaming_ReadsOffTheUiThread()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var folder = Path.Combine(f.Photos.Path, "new");
            foreach (var file in Photos(450))
                TestImages.Write(Path.Combine(folder, file), 32, 24);

            f.Vm.AddFolder(folder, includeSubfolders: true); // a new folder streams its thumbnails in as it's indexed
            Assert.True(Sta.PumpUntil(f.Settled, Wait));

            Assert.Equal(450, f.Vm.TotalItemCount);
            lock (f.ContentReads)
            {
                Assert.True(f.ContentReads.Count >= 2, $"{f.ContentReads.Count} reads: the stream's and the run's end at least");
                Assert.DoesNotContain(f.UiThread, f.ContentReads);
            }
        });
    }

    [Fact]
    public void TheClimbingCount_IsReadOffTheUiThread()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var folder = Path.Combine(f.Photos.Path, "new");
            foreach (var file in Photos(450))
                TestImages.Write(Path.Combine(folder, file), 32, 24);

            f.Vm.AddFolder(folder, includeSubfolders: true);
            Assert.True(Sta.PumpUntil(f.Settled, Wait));

            Assert.Equal(450, f.Vm.Roots.Single().Count);
            lock (f.CountReads)
            {
                Assert.True(f.CountReads.Count >= 2, $"{f.CountReads.Count} count reads: one while indexing, one at the end");
                Assert.DoesNotContain(f.UiThread, f.CountReads);
            }
        });
    }

    [Fact]
    public void TheRunsEnd_RefreshesOffTheUiThread()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            TestImages.Write(Path.Combine(f.Photos.Path, "later.jpg"), 32, 24);

            f.Vm.ResumeRootIndexCommand.Execute(f.Vm.Roots.Single()); // a re-index of a folder the grid already shows
            Assert.True(Sta.PumpUntil(() => f.Settled() && f.Vm.TotalItemCount == 4, Wait));

            Assert.Equal(4, f.Vm.Roots.Single().Count);
            lock (f.ContentReads)
            {
                Assert.NotEmpty(f.ContentReads);
                Assert.DoesNotContain(f.UiThread, f.ContentReads);
            }
            lock (f.CountReads)
                Assert.DoesNotContain(f.UiThread, f.CountReads);
        });
    }

    [Fact]
    public void AHeldRead_LeavesTheWindowResponsive()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            TestImages.Write(Path.Combine(f.Photos.Path, "later.jpg"), 32, 24);
            var release = f.HoldNextRead(); // the run's end reads slowly, as on a weak PC

            f.Vm.ResumeRootIndexCommand.Execute(f.Vm.Roots.Single());
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));

            // Input posted while the read is held is handled while it's held, not after.
            bool? ranWhileHeld = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, () => ranWhileHeld = !release.IsSet);
            Assert.True(Sta.PumpUntil(() => ranWhileHeld is not null, Wait));
            Assert.True(ranWhileHeld);
            release.Set();

            Assert.True(Sta.PumpUntil(() => f.Settled() && f.Vm.TotalItemCount == 4, Wait));
            Assert.False(f.HoldTimedOut, "the read was let go by the test, not by its time-out: it ran off the UI thread");
        });
    }

    [Fact]
    public void ARunsEnd_AskedForDuringAnotherRefresh_IsStillDone()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(3), .. Photos(2, "Trip")]);
            var root = f.Vm.Roots.Single();
            // Two more photos indexed behind the window's back: the next refresh that counts the folder finds 7.
            TestImages.Write(Path.Combine(f.Photos.Path, "Trip", "t1.jpg"), 32, 24);
            TestImages.Write(Path.Combine(f.Photos.Path, "New", "n1.jpg"), 32, 24);
            f.Library.IndexRoot(f.Library.GetRoots().Single());
            var release = f.HoldNextRead();

            f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null);   // one refresh, held
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: root.Id); // a run's end, while it's held
            f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null);   // then another stream tick
            release.Set();
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));

            Assert.Equal(7, root.Count);                                     // the run's end's count…
            Assert.Contains(f.Vm.FolderTree.Single().Children.Select(c => c.Name), n => n == "New"); // …and its tree
            Assert.Equal(7, f.Vm.TotalItemCount);
        });
    }

    [Fact]
    public void RequestsDuringARefresh_AreDoneOnce_Afterwards()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            var release = f.HoldNextRead();

            f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null);
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));
            for (var i = 0; i < 5; i++)
                f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null);
            release.Set();
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));

            lock (f.ContentReads)
                Assert.Equal(2, f.ContentReads.Count); // the held one, then one for all five
        });
    }

    [Fact]
    public void HidingAFolder_DuringARefreshRead_StaysHidden()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(2, "Trip")]);
            var release = f.HoldNextRead();
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null);   // reads the rows with Trip shown, then is held
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));

            var trip = f.Vm.FolderTree.Single().Children.Single(c => c.Name == "Trip");
            trip.IsShown = false;                                          // the user hides Trip meanwhile
            Assert.True(Sta.PumpUntil(() => f.Vm.TotalItemCount == 1, Wait));
            release.Set();
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted && !f.Vm.IsLoadingView, Wait));

            Assert.Equal(1, f.Vm.TotalItemCount);                          // the older rows weren't shown
            Assert.False(f.Vm.FolderTree.Single().Children.Single(c => c.Name == "Trip").IsShown);
        });
    }

    [Fact]
    public void RemovingAFolder_DuringARefreshRead_StaysRemoved()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            var release = f.HoldNextRead();
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: f.Vm.Roots.Single().Id);
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));

            f.Vm.RemoveRootCommand.Execute(f.Vm.Roots.Single());
            release.Set();
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted && !f.Vm.IsLoadingView, Wait));

            Assert.Equal(0, f.Vm.TotalItemCount);
            Assert.Empty(f.Vm.FolderTree);
            Assert.Empty(f.Vm.Roots);
        });
    }

    [Fact]
    public void TheStartupRead_LandingAfterAChange_IsNotShown()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            foreach (var file in Photos(3))
                TestImages.Write(Path.Combine(photos.Path, file), 32, 24);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var first = 1;
            library.ReadingContents = () =>
            {
                if (Interlocked.Exchange(ref first, 0) == 0)
                    return;
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10)); // the startup read, slow
            };
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            try
            {
                Assert.True(held.Wait(Wait));
                vm.RemoveRootCommand.Execute(vm.Roots.Single()); // removed while the startup read is under way
                release.Set();
                Assert.True(Sta.PumpUntil(() => !vm.IsLoadingView && vm.RefreshIdle.IsCompleted, Wait));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

                Assert.Equal(0, vm.TotalItemCount); // its rows (read before the removal) weren't shown
                Assert.Empty(vm.FolderTree);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFailedRead_KeepsTheRows()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            f.Library.ReadingContents = () => throw new IOException("the disk went away");

            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null);
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));

            Assert.Equal(3, f.Vm.TotalItemCount);
            Assert.Single(f.Vm.FolderTree);
            f.Library.ReadingContents = null;
            TestImages.Write(Path.Combine(f.Photos.Path, "later.jpg"), 32, 24);
            f.Library.IndexRoot(f.Library.GetRoots().Single());
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null); // and the next refresh works
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted && f.Vm.TotalItemCount == 4, Wait));
        });
    }

    [Fact]
    public void TheTree_UsesTheIndex()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(1, @"A\B\C"), .. Photos(1, @"A\D"), .. Photos(1, "E")]);
            Core.Library.FolderTree.SubfoldersCalls = 0;

            var root = f.Vm.FolderTree.Single();
            ExpandAsTheUserDoes(root);
            var a = root.Children.Single(c => c.Name == "A");
            a.IsExpanded = true;
            a.Children.Single(c => c.Name == "B").IsExpanded = true;
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null); // the tree built again, its expansion restored
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));

            Assert.Equal(["C"], f.Vm.FolderTree.Single().Children.Single(c => c.Name == "A").Children
                .Single(c => c.Name == "B").Children.Select(c => c.Name));
            Assert.Equal(0, Core.Library.FolderTree.SubfoldersCalls); // the index, not a walk over every row per folder
        });
    }

    [Fact]
    public void HidingAFolder_RebuildsTheTreeFromTheNewRows()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(2, @"Trip\Day 1"), .. Photos(1, @"Trip\Day 2")]);
            var root = f.Vm.FolderTree.Single();
            ExpandAsTheUserDoes(root);
            var trip = root.Children.Single(c => c.Name == "Trip");
            trip.IsExpanded = true;

            trip.Children.Single(c => c.Name == "Day 1").IsShown = false;
            Assert.True(Sta.PumpUntil(() => f.Vm.TotalItemCount == 2, Wait));

            var tripNow = f.Vm.FolderTree.Single().Children.Single(c => c.Name == "Trip");
            Assert.Equal(1, tripNow.Count);
            var day1 = tripNow.Children.Single(c => c.Name == "Day 1");
            Assert.False(day1.IsShown);
            Assert.Equal(2, day1.Count);
        });
    }

    // --- Review round 1 -----------------------------------------------------------------------------------------------

    // A library of `photos`, its startup read held (the hook's first call) until `releaseStartup`, the next contents read
    // held until `releaseNext` (when given); the view model built over it.
    private static void WithHeldStartup(
        int photos, Action<LibraryService, MainViewModel, ManualResetEventSlim, string> body,
        ManualResetEventSlim releaseStartup, ManualResetEventSlim? heldNext = null, ManualResetEventSlim? releaseNext = null)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            foreach (var file in Photos(photos))
                TestImages.Write(Path.Combine(folder.Path, file), 32, 24);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var heldStartup = new ManualResetEventSlim();
            var reads = 0;
            library.ReadingContents = () =>
            {
                switch (Interlocked.Increment(ref reads))
                {
                    case 1:
                        heldStartup.Set();
                        releaseStartup.Wait(TimeSpan.FromSeconds(10));
                        break;
                    case 2 when releaseNext is not null:
                        heldNext?.Set();
                        releaseNext.Wait(TimeSpan.FromSeconds(10));
                        break;
                }
            };
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            try
            {
                Assert.True(heldStartup.Wait(Wait));
                body(library, vm, heldStartup, folder.Path);
            }
            finally
            {
                releaseStartup.Set();
                releaseNext?.Set();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ARefreshRead_LandingAfterTheOlderStartupRead_IsDoneAgain()
    {
        // Review R1-B1: the startup read (older) lands while a refresh's read (newer) is under way; the refresh's rows,
        // overtaken, must be read again rather than dropped.
        var releaseStartup = new ManualResetEventSlim();
        var heldRefresh = new ManualResetEventSlim();
        var releaseRefresh = new ManualResetEventSlim();
        WithHeldStartup(3, (library, vm, _, folder) =>
        {
            TestImages.Write(Path.Combine(folder, "later.jpg"), 32, 24);
            library.IndexRoot(library.GetRoots().Single()); // 4 now; the startup read has 3

            vm.RequestRefresh(rows: true, tree: true, countRoot: null);
            Assert.True(heldRefresh.Wait(Wait));
            releaseStartup.Set();
            Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount == 3, Wait)); // the older rows land first
            releaseRefresh.Set();

            Assert.True(Sta.PumpUntil(() => vm.RefreshIdle.IsCompleted && !vm.IsLoadingView && vm.TotalItemCount == 4, Wait),
                $"the refresh's rows were dropped: {vm.TotalItemCount} shown");
        }, releaseStartup, heldRefresh, releaseRefresh);
    }

    [Fact]
    public void TheStartupTree_IsBuilt_WhenARefreshOvertookStartup()
    {
        // Review R1-A3: a refresh (a folder streaming in) landing before the startup read did left the tree unbuilt.
        var releaseStartup = new ManualResetEventSlim();
        WithHeldStartup(3, (_, vm, _, _) =>
        {
            vm.RequestRefresh(rows: true, tree: false, countRoot: null); // lands first, and builds the grid
            Assert.True(Sta.PumpUntil(() => vm.RefreshIdle.IsCompleted && vm.TotalItemCount == 3, Wait));
            releaseStartup.Set();

            Assert.True(Sta.PumpUntil(() => vm.FolderTree.Count == 1 && !vm.IsLoadingView, Wait), "the tree was never built");
        }, releaseStartup);
    }

    [Fact]
    public void Refreshes_ShowNoLoadingCard()
    {
        // Review R1-A4: every refresh while a folder streamed in flashed "Loading…" over the grid.
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            var shown = 0;
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsLoadingView) && f.Vm.IsLoadingView)
                    shown++;
            };

            for (var i = 0; i < 3; i++)
            {
                f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null);
                Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300)); // its grid build, debounced, runs
            }

            Assert.Equal(0, shown);
        });
    }

    [Fact]
    public void TheResults_AreAppliedOnTheUiThread()
    {
        // Review R1-B2: the reads run off the UI thread; what they bring is applied on it (the tree's collection is bound).
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(1, "Trip")]);
            var applied = new List<int>();
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.TotalItemCount))
                    lock (applied) applied.Add(Environment.CurrentManagedThreadId);
            };
            f.Vm.FolderTree.CollectionChanged += (_, _) => { lock (applied) applied.Add(Environment.CurrentManagedThreadId); };
            f.Vm.Roots.Single().PropertyChanged += (_, _) => { lock (applied) applied.Add(Environment.CurrentManagedThreadId); };
            TestImages.Write(Path.Combine(f.Photos.Path, "later.jpg"), 32, 24);
            f.Library.IndexRoot(f.Library.GetRoots().Single());

            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: f.Vm.Roots.Single().Id);
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted && f.Vm.TotalItemCount == 3, Wait));

            lock (applied)
            {
                Assert.NotEmpty(applied);
                Assert.All(applied, thread => Assert.Equal(f.UiThread, thread));
            }
        });
    }

    [Fact]
    public void TheFolderIndex_IsBuiltOffTheUiThread_AndOnceForRowsReadOnIt()
    {
        // Review R1-B3: the index built on the UI thread, or built again for every folder, would bring back the freeze.
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(1, @"A\B\C"), .. Photos(1, @"A\D"), .. Photos(1, "E")]);
            Core.Library.FolderIndex.BuildCalls = 0; // on this, the UI thread: before the refresh (review R2-1)
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null);
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));
            Assert.Equal(0, Core.Library.FolderIndex.BuildCalls); // the refresh built it, in the background

            var root = f.Vm.FolderTree.Single();
            ExpandAsTheUserDoes(root);
            var a = root.Children.Single(c => c.Name == "A");
            a.IsExpanded = true;
            a.Children.Single(c => c.Name == "B").IsExpanded = true;
            Assert.Equal(0, Core.Library.FolderIndex.BuildCalls); // the refresh built it, in the background

            root.Children.Single(c => c.Name == "E").IsShown = false; // read on the UI thread, as a hide always is
            Assert.True(Sta.PumpUntil(() => f.Vm.TotalItemCount == 3, Wait));
            var aNow = f.Vm.FolderTree.Single().Children.Single(c => c.Name == "A");
            aNow.IsExpanded = false;
            aNow.IsExpanded = true;
            Assert.Equal(1, Core.Library.FolderIndex.BuildCalls); // once for those rows, however many folders open
        });
    }

    [Fact]
    public void TheWindowSaysIndexing_UntilARunsRefreshLands()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3));
            TestImages.Write(Path.Combine(f.Photos.Path, "later.jpg"), 32, 24);
            var release = f.HoldNextRead(); // the run's end's refresh

            f.Vm.ResumeRootIndexCommand.Execute(f.Vm.Roots.Single());
            Assert.True(Sta.PumpUntil(() => f.Held.IsSet, Wait));
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(100));

            Assert.True(f.Vm.IsIndexing); // the run has ended, but the grid hasn't caught up with it yet
            release.Set();
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsIndexing, Wait));
            Assert.Equal(4, f.Vm.TotalItemCount);
        });
    }

    [Fact]
    public void TheIndexingLines_FollowTheRun()
    {
        // Review R1-B4: what the bar and the card say, through the view model: counting, then how far, then nothing.
        Sta.Run(() =>
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            try
            {
                using var f = new Fixture();
                var folder = Path.Combine(f.Photos.Path, "new");
                foreach (var file in Photos(30))
                    TestImages.Write(Path.Combine(folder, file), 32, 24);
                var cloud = Path.Combine(folder, "p004.jpg");
                File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
                var headlines = new List<string>();
                var notes = new List<string>();
                f.Vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IndexingText))
                        headlines.Add(f.Vm.IndexingText);
                    if (e.PropertyName == nameof(MainViewModel.IndexingNote))
                        notes.Add(f.Vm.IndexingNote);
                };

                f.Vm.AddFolder(folder, includeSubfolders: true);
                Assert.True(Sta.PumpUntil(f.Settled, Wait));

                Assert.Contains(headlines, h => h.StartsWith("Counting files in new…"));
                Assert.Contains("Indexing new: 0 of 30 files", headlines);
                Assert.True(headlines.FindIndex(h => h.StartsWith("Counting")) < headlines.IndexOf("Indexing new: 0 of 30 files"));
                Assert.Contains("This might take a while. You can keep browsing. 1 of the files is online-only, so it's "
                                + "downloaded as it's indexed.", notes);
                Assert.Equal(("", ""), (f.Vm.IndexingText, f.Vm.IndexingNote));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        });
    }

    // --- Review round 2 -----------------------------------------------------------------------------------------------

    [Fact]
    public void ARunsEnd_OvertakenByTheStartupRead_StillEndsTheIndexing_AndBuildsTheTree()
    {
        // Review R2-2: the refresh read again after being overtaken carries the run's end and its tree with it.
        var releaseStartup = new ManualResetEventSlim();
        var heldRefresh = new ManualResetEventSlim();
        var releaseRefresh = new ManualResetEventSlim();
        WithHeldStartup(3, (_, vm, _, folder) =>
        {
            TestImages.Write(Path.Combine(folder, "New", "n.jpg"), 32, 24);
            vm.ResumeRootIndexCommand.Execute(vm.Roots.Single()); // a run whose end's refresh is held
            Assert.True(Sta.PumpUntil(() => heldRefresh.IsSet, Wait));
            releaseStartup.Set();
            // The startup read lands first, and startup finishes (its tree built from its rows), so only the refresh
            // read again can bring the new folder into the tree (review round 3's sweep: startup's own tree hid it).
            Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount == 3 && vm.FolderTree.Count == 1 && !vm.IsLoadingView, Wait));
            Assert.DoesNotContain("New", vm.FolderTree.Single().Children.Select(c => c.Name));
            releaseRefresh.Set();

            Assert.True(Sta.PumpUntil(() => !vm.IsIndexing && vm.RefreshIdle.IsCompleted && vm.TotalItemCount == 4, Wait),
                "the run's end never landed");
            Assert.Contains("New", vm.FolderTree.Single().Children.Select(c => c.Name));
        }, releaseStartup, heldRefresh, releaseRefresh);
    }

    [Fact]
    public void AFolderAddedDuringStartup_LeavesTheOthersTreeAsTheStartupReadShowsIt()
    {
        // Review R2-5 (the reviewer's probe): adding a folder builds a tree from no rows while startup still reads; the
        // startup's rows, when they land, must build it again — not leave the other folders without their subfolders.
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            using var added = new TempDir();
            TestImages.Write(Path.Combine(folder.Path, "Trip", "a.jpg"), 32, 24);
            TestImages.Write(Path.Combine(folder.Path, "Trip", "b.jpg"), 32, 24);
            TestImages.Write(Path.Combine(added.Path, "n.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var heldStartup = new ManualResetEventSlim();
            var releaseStartup = new ManualResetEventSlim();
            var heldNext = new ManualResetEventSlim();
            var releaseNext = new ManualResetEventSlim();
            var reads = 0;
            library.ReadingContents = () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    heldStartup.Set();
                    releaseStartup.Wait(TimeSpan.FromSeconds(20));
                }
                else
                {
                    heldNext.Set();
                    releaseNext.Wait(TimeSpan.FromSeconds(20));
                }
            };
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            try
            {
                Assert.True(heldStartup.Wait(Wait));
                vm.AddFolder(added.Path, includeSubfolders: true); // added while startup still reads
                Assert.True(Sta.PumpUntil(() => heldNext.IsSet, Wait)); // the added folder's refresh, held (a long run)
                releaseStartup.Set();
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == 2, Wait));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

                var photos = vm.FolderTree.Single(n => n.Name == "Photos");
                Assert.True(photos.HasChildren, "Photos' Trip subfolder is missing from the tree after startup landed");
            }
            finally
            {
                releaseStartup.Set();
                releaseNext.Set();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUsersChange_ShowsLoading_EvenIfARefreshTakesOverItsBuild(bool refresh)
    {
        // Review R2-6 (the reviewer's probe): a quiet refresh asked for within a user's change's debounce took its build,
        // and with it the "Loading…" the change should show.
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(2));
            var shown = 0;
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsLoadingView) && f.Vm.IsLoadingView)
                    shown++;
            };

            f.Vm.ShowVideos = !f.Vm.ShowVideos; // the user's change: its build shows "Loading…"
            if (refresh)
                f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null); // a refresh asked for meanwhile
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(600));

            Assert.Equal(1, shown);
            Assert.False(f.Vm.IsLoadingView);
        });
    }

    // --- Review round 3 -----------------------------------------------------------------------------------------------

    // Hands a run's reports on, and holds the run (on its own thread) after its first "Indexing" one until released.
    private sealed class HoldAfterTheCount(IProgress<IndexProgress> inner, ManualResetEventSlim holding, ManualResetEventSlim release)
        : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value)
        {
            inner.Report(value);
            if (value.Phase != IndexPhase.Indexing || holding.IsSet)
                return;
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public void ALaterRefresh_IsQuietAgain_OnceAUsersChangeHasLanded()
    {
        // Review R3-1 (the reviewer's probe): the user's change keeps "Loading…" wanted only until its build lands.
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(2));
            var applied = 0;
            f.Vm.ViewApplied += () => applied++;
            f.Vm.ShowVideos = !f.Vm.ShowVideos; // the user's change: loud
            Assert.True(Sta.PumpUntil(() => applied >= 1 && !f.Vm.IsLoadingView, Wait)); // it lands
            var shown = 0;
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsLoadingView) && f.Vm.IsLoadingView)
                    shown++;
            };

            var before = applied;
            f.Vm.RequestRefresh(rows: true, tree: false, countRoot: null); // a background refresh afterwards
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted && applied > before && !f.Vm.IsLoadingView, Wait));

            Assert.Equal(0, shown);
        });
    }

    [Fact]
    public void TwoFoldersIndexedAtOnce_TheBarAddsThemUp()
    {
        // Review R3-2 (the reviewer's probe): the window's bar is both folders' files while both are indexed.
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var maxes = new List<double>();
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IndexMax))
                    maxes.Add(f.Vm.IndexMax);
            };
            foreach (var (name, count) in new[] { ("first", 300), ("second", 30) })
            {
                var folder = Path.Combine(f.Photos.Path, name);
                foreach (var file in Photos(count))
                    TestImages.Write(Path.Combine(folder, file), 32, 24);
            }
            // The first folder's run waits once it has said how many files it has (before it writes anything), so the
            // second is indexed while the first is too: the two runs' database writes would otherwise take turns.
            var holding = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            f.Library.WrapIndexProgress = (root, progress) => root.Alias != "first" || progress is null
                ? progress
                : new HoldAfterTheCount(progress, holding, release);

            f.Vm.AddFolder(Path.Combine(f.Photos.Path, "first"), includeSubfolders: true);
            Assert.True(Sta.PumpUntil(() => holding.IsSet, Wait));
            f.Vm.AddFolder(Path.Combine(f.Photos.Path, "second"), includeSubfolders: true);
            Assert.True(Sta.PumpUntil(() => maxes.Contains(330.0), Wait), "maxes: " + string.Join(",", maxes.Distinct()));
            var second = f.Vm.Roots.Single(r => r.Alias == "second");
            Assert.True(Sta.PumpUntil(() => !second.IsIndexing, Wait));
            Assert.Equal(300.0, f.Vm.IndexMax); // the second's run has ended: the bar is the first folder's alone (R4-1)
            release.Set();
            Assert.True(Sta.PumpUntil(f.Settled, TimeSpan.FromSeconds(120)));

            Assert.Equal(330, f.Vm.TotalItemCount);
        });
    }

    [Fact]
    public void TheStartupTree_IsBuilt_WhenARefreshLandsDuringItsGridCompute()
    {
        // Review R3-3: startup's other overtaken case (a refresh landing while it computes its grid).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            foreach (var file in Photos(3))
                TestImages.Write(Path.Combine(folder.Path, file), 32, 24);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var computing = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var vm = new MainViewModel(library, new ThumbnailService(library))
            {
                StartupReconcileDelay = TimeSpan.FromHours(1),
                ComputingStartupView = () =>
                {
                    computing.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                },
            };
            try
            {
                Assert.True(Sta.PumpUntil(() => computing.IsSet, Wait)); // startup reaches its compute through this thread
                vm.RequestRefresh(rows: true, tree: false, countRoot: null); // lands while startup computes its grid
                Assert.True(Sta.PumpUntil(() => vm.RefreshIdle.IsCompleted, Wait));
                release.Set();

                Assert.True(Sta.PumpUntil(() => vm.FolderTree.Count == 1 && !vm.IsLoadingView, Wait),
                    $"the tree was never built: {vm.FolderTree.Count} nodes, loading {vm.IsLoadingView}, rows {vm.TotalItemCount}");
            }
            finally
            {
                release.Set();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFreshLibrary_KeepsTheFoldersItOpenedByDefault_WhenTheUserExpandsASubfolder()
    {
        // Review R3-6 (and the quirk the 0.9.1 batch's report names): a fresh library opens its top-level folders without
        // remembering it, so the first subfolder the user opened was remembered alone, and the next rebuild of the tree
        // closed the folder around it.
        Sta.Run(() =>
        {
            using var f = new Fixture([.. Photos(1), .. Photos(1, @"Trip\Day")]);
            var root = f.Vm.FolderTree.Single();
            Assert.True(root.IsExpanded); // open by default

            root.Children.Single(c => c.Name == "Trip").IsExpanded = true; // the user's first expansion
            f.Vm.RequestRefresh(rows: true, tree: true, countRoot: null);  // the tree built again
            Assert.True(Sta.PumpUntil(() => f.Vm.RefreshIdle.IsCompleted, Wait));

            var rebuilt = f.Vm.FolderTree.Single();
            Assert.True(rebuilt.IsExpanded);
            Assert.True(rebuilt.Children.Single(c => c.Name == "Trip").IsExpanded);
        });
    }

    [Fact]
    public void TheWindow_SaysIndexingOnce_FromARunsStartToItsRefresh()
    {
        // Review R3-8, R4-2: a run's last report reaches the window in two hops (Progress posts it, and the view model posts
        // it on again), so it can be handled after the run's own end, which counted it out of "indexing" before its
        // refresh had landed: the bar swept early and "indexing" went off and back on. This makes that order happen: the
        // window is kept busy as the run's last report is posted, until the run's task has completed. And all the while
        // the top bar's pill never says "up to date" (review R4-3).
        Sta.Run(() =>
        {
            using var f = new Fixture(Photos(3)); // rows already shown: the pill has a count to call "up to date"
            var folder = Path.Combine(f.Photos.Path, "new");
            foreach (var file in Photos(30))
                TestImages.Write(Path.Combine(folder, file), 32, 24);
            f.Library.WrapIndexProgress = (_, progress) =>
                progress is null ? null : new BusyWindowAtTheEnd(progress, Dispatcher.CurrentDispatcher);
            var changes = new List<bool>();
            var stalePills = new List<string>();
            f.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsIndexing))
                    changes.Add(f.Vm.IsIndexing);
                if (e.PropertyName == nameof(MainViewModel.SyncStatus) && f.Vm.IsIndexing && f.Vm.SyncStatus.Contains("up to date"))
                    stalePills.Add(f.Vm.SyncStatus);
            };

            f.Vm.AddFolder(folder, includeSubfolders: true);
            Assert.DoesNotContain("up to date", f.Vm.SyncStatus); // from the run's start, not only once the pill changes (R5-1)
            Assert.True(Sta.PumpUntil(f.Settled, TimeSpan.FromSeconds(60)));

            Assert.Equal([true, false], changes);
            Assert.Empty(stalePills);
            Assert.Equal(33, f.Vm.TotalItemCount);
            Assert.Equal("33 items  ·  up to date", f.Vm.SyncStatus); // and caught up, once the run's refresh has landed
        });
    }

    // Hands a run's reports on; at its last one, keeps the window busy (a dispatcher operation that waits) from just before
    // the report is posted until a moment after, when the run's task has completed and its end is queued behind it.
    private sealed class BusyWindowAtTheEnd(IProgress<IndexProgress> inner, Dispatcher window) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value)
        {
            if (value.Phase != IndexPhase.Done)
            {
                inner.Report(value);
                return;
            }
            var release = new ManualResetEventSlim();
            window.BeginInvoke(() => release.Wait(TimeSpan.FromSeconds(5)));
            inner.Report(value);
            _ = Task.Run(async () =>
            {
                await Task.Delay(300); // the run returns at once after its last report: its end is queued by then
                release.Set();
            });
        }
    }
}
