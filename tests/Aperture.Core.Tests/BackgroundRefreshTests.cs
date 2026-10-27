using System.Collections.Concurrent;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// A library's view model reconciles its folders shortly after it opens. A folder added in the meantime — New
/// library hands its folder over right then (review round 16) — is already being indexed, and must not be indexed a
/// second time alongside: two runs on one folder overwrite each other's pause/cancel handle. A real view model on a
/// real library, its dispatcher pumped on an STA thread.
/// </summary>
public class BackgroundRefreshTests
{
    [Fact]
    public void AFolderAddedBeforeTheStartupReconcile_IsIndexedOnce()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var older = new TempDir();
            using var added = new TempDir();
            TestImages.Write(Path.Combine(older.Path, "a.jpg"), 64, 48);
            foreach (var name in new[] { "b1.jpg", "b2.jpg", "b3.jpg" })
                TestImages.Write(Path.Combine(added.Path, name), 64, 48);

            var library = new LibraryService(data.Path);
            var olderRoot = library.AddRoot(older.Path, "Older");
            library.IndexRoot(olderRoot);
            var starts = new ConcurrentQueue<string>();
            library.IndexStarting = root => starts.Enqueue(root.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromMilliseconds(150) };
            try
            {
                vm.StartBackgroundRefresh();       // as App does for a library it has just opened…
                vm.AddFolder(added.Path, includeSubfolders: true); // …then New library's folder is handed over

                Assert.True(Sta.PumpUntil(() => starts.Contains(older.Path) && !vm.IsIndexing, TimeSpan.FromSeconds(20)));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(400)); // anything else would have started by now

                Assert.Equal(1, starts.Count(p => p == added.Path));
                Assert.Equal(1, starts.Count(p => p == older.Path)); // the reconcile still covers the folders it opened with
                Assert.Equal(4, vm.TotalItemCount);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }
}
