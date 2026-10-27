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
/// second time alongside: two runs on one folder overwrite each other's pause/cancel handle. A folder removed in the
/// meantime isn't indexed at all (round 17); nor is one added after the library has closed, or once a library switch
/// closes it while the add's question is open (round 18). A real view model on a real library, its dispatcher pumped
/// on an STA thread.
/// </summary>
public class BackgroundRefreshTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Older { get; } = new();
        public TempDir Added { get; } = new();
        public LibraryService Library { get; }
        public MainViewModel Vm { get; }
        public ConcurrentQueue<string> Starts { get; } = new();

        public Fixture(TimeSpan reconcileDelay, bool addedIsARootToo = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            TestImages.Write(Path.Combine(Older.Path, "a.jpg"), 64, 48);
            foreach (var name in new[] { "b1.jpg", "b2.jpg", "b3.jpg" })
                TestImages.Write(Path.Combine(Added.Path, name), 64, 48);

            Library = new LibraryService(Data.Path);
            Library.IndexRoot(Library.AddRoot(Older.Path, "Older"));
            if (addedIsARootToo)
                Library.IndexRoot(Library.AddRoot(Added.Path, "Added"));
            Library.IndexStarting = root => Starts.Enqueue(root.Path);
            Vm = new MainViewModel(Library, new ThumbnailService(Library)) { StartupReconcileDelay = reconcileDelay };
        }

        public void Close()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Close(); // both idempotent
            Added.Dispose();
            Older.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void AFolderAddedBeforeTheStartupReconcile_IsIndexedOnce()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(TimeSpan.FromMilliseconds(150));

            f.Vm.StartBackgroundRefresh();       // as App does for a library it has just opened…
            f.Vm.AddFolder(f.Added.Path, includeSubfolders: true); // …then New library's folder is handed over

            Assert.True(Sta.PumpUntil(() => f.Starts.Contains(f.Older.Path) && !f.Vm.IsIndexing, TimeSpan.FromSeconds(20)));
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(400)); // anything else would have started by now

            Assert.Equal(1, f.Starts.Count(p => p == f.Added.Path));
            Assert.Equal(1, f.Starts.Count(p => p == f.Older.Path)); // the reconcile still covers the folders it opened with
            Assert.Equal(4, f.Vm.TotalItemCount);
        });
    }

    [Fact]
    public void AFolderRemovedBeforeTheStartupReconcile_IsNotIndexedOrWatched()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(TimeSpan.FromMilliseconds(150), addedIsARootToo: true);

            f.Vm.StartBackgroundRefresh();
            f.Vm.RemoveRootCommand.Execute(f.Vm.Roots.Single(r => r.Path == f.Older.Path)); // removed during the delay

            // The kept folder's reconcile shows the reconcile has run (no fixed wait to outlast).
            Assert.True(Sta.PumpUntil(() => f.Starts.Contains(f.Added.Path) && !f.Vm.IsIndexing, TimeSpan.FromSeconds(20)));
            Assert.Equal([f.Added.Path], f.Starts);
            Assert.Equal(1, f.Library.WatcherCount); // the kept folder's, not the removed one's
        });
    }

    [Fact]
    public void AFolderAddedAfterTheLibraryHasClosed_IsIgnored()
    {
        // An add whose dialog was still open when a library switch closed this library: answering it must not reach
        // the closed database (an exception there would end the app).
        Sta.Run(() =>
        {
            using var f = new Fixture(TimeSpan.FromSeconds(30));
            f.Close();

            f.Vm.AddFolder(f.Added.Path, includeSubfolders: true);

            Assert.Single(f.Vm.Roots);
            Assert.Empty(f.Starts);
        });
    }

    [Fact]
    public void AddingFoldersToAClosedLibrary_AsksNothingAndAddsNothing()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(TimeSpan.FromSeconds(30));
            var asked = 0;
            f.Vm.AskIncludeSubfolders = _ => { asked++; return true; };
            f.Close();

            f.Vm.AddFolders([f.Added.Path]); // Add folder's picker, or a drop, answered after a switch closed this library

            Assert.Equal(0, asked);
            Assert.Single(f.Vm.Roots);
            Assert.Empty(f.Starts);
        });
    }

    [Fact]
    public void ALibrarySwitchWhileTheSubfolderQuestionIsOpen_AddsNothing()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(TimeSpan.FromSeconds(30));
            f.Vm.AskIncludeSubfolders = _ => { f.Close(); return true; }; // the switch finishes while the user answers

            f.Vm.AddFolders([f.Added.Path]);

            Assert.Single(f.Vm.Roots);
            Assert.Empty(f.Starts);
        });
    }
}
