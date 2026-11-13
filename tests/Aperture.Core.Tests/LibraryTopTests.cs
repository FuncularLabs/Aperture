using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// What a library shows at its top, and its folders' counts while indexing (Paul's second trial, 2026-10-27). A real
/// view model on a real library, its dispatcher pumped on an STA thread.
/// </summary>
public class LibraryTopTests
{
    private static int TilesInView(MainViewModel vm) => vm.ItemsView?.Cast<object>().OfType<TileVm>().Count() ?? -1;

    [Fact]
    public void EverythingInAOneFolderLibrary_ShowsAllOfIt()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            foreach (var file in new[] { "a.jpg", @"Trip\b.jpg", @"Trip\Day 1\c.jpg" })
                TestImages.Write(Path.Combine(photos.Path, file), 64, 48);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos")); // the library's one folder
            var vm = new MainViewModel(library, new ThumbnailService(library));
            try
            {
                Assert.True(Sta.PumpUntil(() => TilesInView(vm) == 3 && vm.FolderTree.Count == 1, TimeSpan.FromSeconds(10)),
                    $"Everything shows {TilesInView(vm)} of 3");

                vm.NavigateToNode(vm.FolderTree[0]); // the folder itself: its own photo, not its subfolders'
                Assert.True(Sta.PumpUntil(() => TilesInView(vm) == 1, TimeSpan.FromSeconds(10)), $"the folder shows {TilesInView(vm)}");

                vm.NavigateHomeCommand.Execute(null);
                Assert.True(Sta.PumpUntil(() => TilesInView(vm) == 3, TimeSpan.FromSeconds(10)), $"back at the top: {TilesInView(vm)}");
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFoldersCount_RisesWhileItIsIndexed()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            for (var i = 0; i < 250; i++) // the indexer saves (and reports) every 200 files
                TestImages.Write(Path.Combine(photos.Path, $"p{i:000}.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromSeconds(30) };
            try
            {
                vm.AddFolder(photos.Path, includeSubfolders: true);
                Assert.Single(vm.FolderTree);
                // The folder's node, as the tree has it: the tree is built again when the startup read lands (the
                // folder was added before it did), so the node is replaced mid-run (review R2-5).
                var seenWhileIndexing = new List<int>();
                void Watch(FolderNodeVm node) => node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderNodeVm.Count) && vm.IsIndexing)
                        seenWhileIndexing.Add(node.Count);
                };
                Watch(vm.FolderTree.Single());
                vm.FolderTree.CollectionChanged += (_, e) =>
                {
                    foreach (var added in e.NewItems?.OfType<FolderNodeVm>() ?? [])
                        Watch(added);
                };

                Assert.True(Sta.PumpUntil(() => !vm.IsIndexing, TimeSpan.FromSeconds(60)));

                Assert.Contains(200, seenWhileIndexing); // after the first saved batch, before the end
                Assert.Equal(250, Assert.Single(vm.FolderTree).Count);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFoldersCount_IsReadAtMostOnceAnInterval()
    {
        // Each read counts the whole folder, so one per saved batch would add up on a big folder (round 22).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            for (var i = 0; i < 600; i++) // batches saved (and reported) at 200, 400 and 600
                TestImages.Write(Path.Combine(photos.Path, $"p{i:000}.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library))
            {
                StartupReconcileDelay = TimeSpan.FromSeconds(30),
                IndexCountInterval = TimeSpan.FromHours(1),
            };
            try
            {
                vm.AddFolder(photos.Path, includeSubfolders: true);
                Assert.Single(vm.FolderTree);
                // The folder's node, as the tree has it: the tree is built again when the startup read lands (the
                // folder was added before it did), so the node is replaced mid-run (review R2-5).
                var seenWhileIndexing = new List<int>();
                void Watch(FolderNodeVm node) => node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderNodeVm.Count) && vm.IsIndexing)
                        seenWhileIndexing.Add(node.Count);
                };
                Watch(vm.FolderTree.Single());
                vm.FolderTree.CollectionChanged += (_, e) =>
                {
                    foreach (var added in e.NewItems?.OfType<FolderNodeVm>() ?? [])
                        Watch(added);
                };

                Assert.True(Sta.PumpUntil(() => !vm.IsIndexing, TimeSpan.FromSeconds(120)));

                Assert.Contains(200, seenWhileIndexing); // the first report is read at once
                Assert.DoesNotContain(400, seenWhileIndexing); // the next, within the interval, isn't
                Assert.Equal(600, Assert.Single(vm.FolderTree).Count); // the end is
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFoldersCount_IsReadAtMostOnceASecond_ByDefault()
    {
        // The tests above set an interval of an hour, so a slow run can't read twice; Aperture runs with the default
        // (round 36: with none, the count was read at every report).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library));
            try
            {
                Assert.Equal(TimeSpan.FromSeconds(1), vm.IndexCountInterval);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFoldersCountInterval_AsSet_RunsOnTheSystemsMillisecondClock()
    {
        // The gate's boundaries are tested on a clock the tests set (IntervalGateTests); this pins the view model's own
        // gate: the interval set through the view model, one gate that remembers, and the clock it reads — between two of
        // the system's readings, not sampled twice (rounds 39–40).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library)) { IndexCountInterval = TimeSpan.FromHours(1) };
            try
            {
                Assert.Equal(TimeSpan.FromHours(1), vm.CountReads.Interval);
                Assert.True(vm.CountReads.IsDue(1));
                Assert.False(vm.CountReads.IsDue(1));

                var before = Environment.TickCount64;
                var now = vm.CountReads.Now;
                Assert.InRange(now, before, Environment.TickCount64);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void EachFoldersCount_HasAnIntervalOfItsOwn()
    {
        // A library's folders are indexed side by side (at launch, all of them): one folder's reads mustn't hold back
        // another's (round 23).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var first = new TempDir();
            using var second = new TempDir();
            foreach (var folder in new[] { first, second })
                for (var i = 0; i < 250; i++) // one saved batch reported each, at 200; each folder is counted again as its run ends
                    TestImages.Write(Path.Combine(folder.Path, $"p{i:000}.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library))
            {
                StartupReconcileDelay = TimeSpan.FromSeconds(30),
                IndexCountInterval = TimeSpan.FromHours(1),
            };
            try
            {
                vm.AddFolder(first.Path, includeSubfolders: true);
                vm.AddFolder(second.Path, includeSubfolders: true);
                Assert.Equal(2, vm.FolderTree.Count);
                // By folder: the tree is built again when a folder finishes, so its nodes are replaced mid-run.
                var seenWhileIndexing = vm.FolderTree.ToDictionary(n => n.RootId, _ => new List<int>());
                void Watch(FolderNodeVm node) => node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderNodeVm.Count) && vm.IsIndexing)
                        seenWhileIndexing[node.RootId].Add(node.Count);
                };
                foreach (var node in vm.FolderTree)
                    Watch(node);
                vm.FolderTree.CollectionChanged += (_, e) =>
                {
                    foreach (var node in e.NewItems?.OfType<FolderNodeVm>() ?? [])
                        Watch(node);
                };

                Assert.True(Sta.PumpUntil(() => !vm.IsIndexing, TimeSpan.FromSeconds(120)));

                // Each folder's count was read as it was indexed — whatever it had reached by the time this thread got to
                // the report (the second folder's would be held back by the first's read, were the interval shared).
                Assert.All(seenWhileIndexing.Values, Assert.NotEmpty);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ReportsBetweenCountReads_KeepTheProgressBarFilling()
    {
        // A report whose count read isn't due still moves the bar: it mustn't flip it to the indeterminate sweep (round 23).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            for (var i = 0; i < 600; i++) // reports at 200 (count read), 400 and 600 (not)
                TestImages.Write(Path.Combine(photos.Path, $"p{i:000}.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library))
            {
                StartupReconcileDelay = TimeSpan.FromSeconds(30),
                IndexCountInterval = TimeSpan.FromHours(1),
            };
            try
            {
                var flips = new List<(bool Indeterminate, double Value)>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IndexIndeterminate))
                        flips.Add((vm.IndexIndeterminate, vm.IndexValue));
                };

                vm.AddFolder(photos.Path, includeSubfolders: true);
                Assert.True(Sta.PumpUntil(() => !vm.IsIndexing, TimeSpan.FromSeconds(120)));

                var filling = flips.FindIndex(f => !f.Indeterminate);
                Assert.True(filling >= 0, "the bar never filled");
                var swept = flips.Skip(filling).First(f => f.Indeterminate); // once files are done: the clean-up sweeps
                Assert.True(600 == swept.Value, string.Join(" ", flips.Select(f => $"{(f.Indeterminate ? "sweep" : "fill")}@{f.Value}")));
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ALibrarysCount_IsUnknownUntilItsItemsAreRead()
    {
        // The library list records the count of a library being left: one left before its items were read must keep
        // the count it had, not get 0 (round 22).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            foreach (var file in new[] { "a.jpg", "b.jpg", "c.jpg" })
                TestImages.Write(Path.Combine(photos.Path, file), 64, 48);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library));
            try
            {
                Assert.Equal(0, vm.TotalItemCount);
                Assert.Null(vm.LoadedItemCount); // the read's result waits for this thread's dispatcher

                Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount == 3, TimeSpan.FromSeconds(10)), $"{vm.LoadedItemCount}");
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ALibrarysCount_IsReportedAgainAsItsItemsChange()
    {
        // The library list shows the open library's count as it changes: indexing new photos reports it (round 24).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            using var more = new TempDir();
            foreach (var file in new[] { "a.jpg", "b.jpg", "c.jpg" })
                TestImages.Write(Path.Combine(photos.Path, file), 64, 48);
            foreach (var file in new[] { "d.jpg", "e.jpg" })
                TestImages.Write(Path.Combine(more.Path, file), 64, 48);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromSeconds(30) };
            try
            {
                Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount == 3, TimeSpan.FromSeconds(10)));
                var reported = new List<int?>();
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.LoadedItemCount))
                        reported.Add(vm.LoadedItemCount);
                };

                vm.AddFolder(more.Path, includeSubfolders: true);

                Assert.True(Sta.PumpUntil(() => !vm.IsIndexing && reported.Contains(5), TimeSpan.FromSeconds(30)), $"reported: {string.Join(", ", reported)}");
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AFoldersCountWhileItIsIndexed_LeavesItsHiddenSubfoldersOut()
    {
        // The reviewer's probe (round 35): a folder of 200 photos with a hidden subfolder of 30, reconciled. The run
        // reports at 200 files, and the folder's first report reads its count (round 36: the report is checked, so the
        // test can't pass without one). Its count in the tree stays 200 — the hidden subfolder out, as when the tree is
        // built — during the run and after (a run that changes nothing doesn't build the tree again).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            for (var i = 0; i < 200; i++)
                TestImages.Write(Path.Combine(photos.Path, $"p{i:000}.jpg"), 32, 24);
            for (var i = 0; i < 30; i++)
                TestImages.Write(Path.Combine(photos.Path, "hidden", $"h{i:00}.jpg"), 32, 24);
            var library = new LibraryService(data.Path);
            var root = library.AddRoot(photos.Path, "Photos");
            library.IndexRoot(root);
            library.SetFolderShown(root.Id, "hidden", false);
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.Zero };
            try
            {
                Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount == 200 && vm.FolderTree.Count == 1, TimeSpan.FromSeconds(10)));
                var node = vm.FolderTree[0];
                Assert.Equal(200, node.Count);
                var seen = new List<int>();
                node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderNodeVm.Count))
                        seen.Add(node.Count);
                };
                var rootVm = Assert.Single(vm.Roots);
                var statuses = new List<string>();
                rootVm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RootVm.Status))
                        statuses.Add(rootVm.Status);
                };

                vm.StartBackgroundRefresh();
                Assert.True(Sta.PumpUntil(() => statuses.Contains("indexing 200 / 230") && !rootVm.IsIndexing, TimeSpan.FromSeconds(30)),
                    $"no report mid-run, or the run didn't end: {string.Join(", ", statuses)}");
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

                Assert.DoesNotContain(230, seen);
                Assert.Equal(200, Assert.Single(vm.FolderTree).Count);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }
}
