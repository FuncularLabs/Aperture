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
                var node = Assert.Single(vm.FolderTree);
                var seenWhileIndexing = new List<int>();
                node.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderNodeVm.Count) && vm.IsIndexing)
                        seenWhileIndexing.Add(node.Count);
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
}
