using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The order of a library's top-level folders (0.9.1 batch, item 5: "Let both libraries and root-level folders be
/// reordered by dragging, and keep that order"). Kept with the library; alphabetical until the user moves one. A real
/// view model on a real library, its dispatcher pumped on an STA thread.
/// </summary>
public class RootOrderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; private set; }
        public MainViewModel Vm { get; private set; }
        public Dictionary<string, long> Ids { get; } = [];

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Library = new LibraryService(Data.Path);
            foreach (var alias in new[] { "Beta", "alpha", "Gamma" })
                Ids[alias] = AddIndexed(alias).Id;
            Vm = Open();
        }

        public Models.Root AddIndexed(string alias)
        {
            var dir = Path.Combine(Photos.Path, alias);
            TestImages.Write(Path.Combine(dir, "p.jpg"), 32, 24);
            var root = Library.AddRoot(dir, alias);
            Library.IndexRoot(root);
            return root;
        }

        private MainViewModel Open()
        {
            var vm = new MainViewModel(Library, new ThumbnailService(Library));
            Assert.True(Sta.PumpUntil(() => vm.LoadedItemCount is not null && !vm.IsLoadingView, Wait));
            return vm;
        }

        public string Tree() => string.Join(", ", Vm.FolderTree.Select(n => n.Name));

        public string RootList() => string.Join(", ", Vm.Roots.Select(r => r.Alias));

        // Aperture closes and opens the library again: what the user sees after a restart.
        public void Reopen()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Library = new LibraryService(Data.Path);
            Vm = Open();
        }

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void TheFolders_AreAlphabetical_UntilOneIsMoved()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();

            Assert.Equal("alpha, Beta, Gamma", f.Tree());
            Assert.Equal("alpha, Beta, Gamma", f.RootList());
        });
    }

    [Fact]
    public void MoveRoot_ReordersTheTree_AndKeepsIt_AcrossARestart()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var gamma = f.Vm.Roots.Single(r => r.Alias == "Gamma");

            f.Vm.MoveRoot(f.Ids["Gamma"], 0);

            Assert.Equal("Gamma, alpha, Beta", f.Tree());
            Assert.Equal("Gamma, alpha, Beta", f.RootList());
            Assert.Same(gamma, f.Vm.Roots[0]); // the same folder, with whatever it's doing (indexing, paused), moved
            f.Reopen();
            Assert.Equal("Gamma, alpha, Beta", f.Tree());
        });
    }

    [Fact]
    public void MoveRoot_ToEachPlace()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();

            f.Vm.MoveRoot(f.Ids["alpha"], 2); // the last place
            Assert.Equal("Beta, Gamma, alpha", f.Tree());
            f.Vm.MoveRoot(f.Ids["alpha"], 1);
            Assert.Equal("Beta, alpha, Gamma", f.Tree());
            f.Vm.MoveRoot(f.Ids["alpha"], 1); // where it is
            Assert.Equal("Beta, alpha, Gamma", f.Tree());
            f.Vm.MoveRoot(12345, 0);          // no such folder
            Assert.Equal("Beta, alpha, Gamma", f.Tree());
        });
    }

    [Fact]
    public void AFolderAddedLater_GoesAtTheEnd()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Vm.MoveRoot(f.Ids["Gamma"], 0);

            var dir = Path.Combine(f.Photos.Path, "Aardvark");
            TestImages.Write(Path.Combine(dir, "p.jpg"), 32, 24);
            f.Vm.AddFolder(dir, includeSubfolders: true);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsIndexing && f.Vm.Roots.Count == 4, Wait));

            Assert.Equal("Gamma, alpha, Beta, Aardvark", f.Tree());
            f.Reopen();
            Assert.Equal("Gamma, alpha, Beta, Aardvark", f.Tree());
        });
    }

    [Fact]
    public void ARemovedFolder_LeavesTheOrder_AndAddedAgainGoesAtTheEnd()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Vm.MoveRoot(f.Ids["Gamma"], 0); // Gamma, alpha, Beta

            f.Vm.RemoveRootCommand.Execute(f.Vm.Roots.Single(r => r.Alias == "alpha"));
            Assert.Equal("Gamma, Beta", f.Tree());

            f.Vm.AddFolder(Path.Combine(f.Photos.Path, "alpha"), includeSubfolders: true);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsIndexing && f.Vm.Roots.Count == 3, Wait));
            Assert.Equal("Gamma, Beta, alpha", f.Tree());
            f.Reopen();
            Assert.Equal("Gamma, Beta, alpha", f.Tree());
        });
    }

    [Fact]
    public void TheSelectedFolder_StaysSelected()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Vm.NavigateToNode(f.Vm.FolderTree.Single(n => n.Name == "Beta"));
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsLoadingView, Wait));

            f.Vm.MoveRoot(f.Ids["Beta"], 2);

            Assert.Equal("alpha, Gamma, Beta", f.Tree());
            Assert.True(f.Vm.FolderTree.Single(n => n.Name == "Beta").IsSelected);
            Assert.Equal($"{f.Ids["Beta"]}|", f.Vm.LocationKey);
        });
    }

    [Fact]
    public void AnExpandedFolder_StaysExpanded()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            TestImages.Write(Path.Combine(f.Photos.Path, "Beta", "Trip", "t.jpg"), 32, 24);
            f.Library.IndexRoot(f.Library.GetRoots().Single(r => r.Alias == "Beta"));
            f.Reopen();
            var beta = f.Vm.FolderTree.Single(n => n.Name == "Beta");
            beta.IsExpanded = true;

            f.Vm.MoveRoot(f.Ids["Beta"], 0);

            var moved = f.Vm.FolderTree[0];
            Assert.Equal("Beta", moved.Name);
            Assert.True(moved.IsExpanded);
            Assert.Equal(["Trip"], moved.Children.Select(c => c.Name));
        });
    }
}
