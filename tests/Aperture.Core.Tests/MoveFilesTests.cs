using System.Windows.Threading;
using Aperture.App;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Indexing;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Move to… on the grid (0.9.2 M1–M6; Paul: "a Move context menu, and warn the user if they are moving the contents
/// into a non-indexed / not-in-any-library location (or a different library, in either case, ask for confirmation)").
/// A real view model on a real library of real files; the folder picker, the question, the summary and the other
/// libraries are the view model's seams.
/// </summary>
public class MoveFilesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public TempDir Outside { get; } = new();
        public LibraryService Library { get; }
        public MainViewModel Vm { get; }
        public Models.Root Root { get; }
        public string? Chosen { get; set; }
        public List<string?> Started { get; } = [];
        public bool Yes { get; set; } = true;
        public List<string> Questions { get; } = [];
        public List<string> Summaries { get; } = [];
        public List<OtherLibrary> Others { get; } = [];
        public List<int> OthersReadOn { get; } = [];
        public int UiThread { get; } = Environment.CurrentManagedThreadId;

        public Fixture(Action<LibraryService>? before = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
                TestImages.Write(Photos.Combine("Photos", name), 32, 24);
            TestImages.Write(Photos.Combine("Photos", "Trips", "t.jpg"), 32, 24);
            Library = new LibraryService(Data.Path);
            Root = Library.AddRoot(Photos.Combine("Photos"), "Photos");
            Library.IndexRoot(Root);
            before?.Invoke(Library);
            Vm = new MainViewModel(Library, new ThumbnailService(Library))
            {
                PickMoveDestination = start =>
                {
                    Started.Add(start);
                    return Chosen;
                },
                ConfirmMove = question =>
                {
                    Questions.Add(question);
                    return Yes;
                },
                ShowMoveSummary = Summaries.Add,
                OtherLibraries = () =>
                {
                    OthersReadOn.Add(Environment.CurrentManagedThreadId);
                    return Others;
                },
            };
            Assert.True(Sta.PumpUntil(() => Vm.LoadedItemCount is not null && !Vm.IsLoadingView && Tiles.Count >= 4, Wait));
        }

        public string Trips => Photos.Combine("Photos", "Trips");

        public List<TileVm> Tiles => Vm.ItemsView?.Cast<object>().OfType<TileVm>().ToList() ?? [];

        public TileVm Tile(string name) => Tiles.First(t => t.FileName == name && Path.GetDirectoryName(t.FullPath) == Photos.Combine("Photos"));

        public void Select(params string[] names) => Vm.SetSelectedItems(names.Select(Tile).ToList());

        public void MoveTo(string? folder, string tile)
        {
            Chosen = folder;
            Vm.MoveToCommand.Execute(Tile(tile));
            Assert.True(Sta.PumpUntil(() => !Vm.IsMoving, Wait), "still moving");
        }

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Outside.Dispose();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void IntoAFolderOfThisLibrary_TheSelectionMoves_WithoutAQuestion_NorReadingOtherLibraries()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg");

            f.MoveTo(f.Trips, "a.jpg");

            Assert.True(File.Exists(Path.Combine(f.Trips, "a.jpg")));
            Assert.True(File.Exists(Path.Combine(f.Trips, "b.jpg")));
            Assert.True(File.Exists(f.Photos.Combine("Photos", "c.jpg")));
            Assert.Empty(f.Questions);
            Assert.Empty(f.OthersReadOn);
            Assert.Empty(f.Summaries);
        });
    }

    [Fact]
    public void ATileOutsideTheSelection_MovesAlone()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg");

            f.MoveTo(f.Trips, "c.jpg");

            Assert.Equal(["c.jpg", "t.jpg"], Directory.GetFiles(f.Trips).Select(Path.GetFileName).Order());
        });
    }

    [Fact]
    public void ThePicker_StartsInTheFolderShown()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.MoveTo(null, "a.jpg");                 // Everything: no folder to start in
            var photos = f.Vm.FolderTree.Single();
            photos.IsExpanded = true;
            f.Vm.NavigateToNode(photos);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsLoadingView && f.Tiles.Count >= 3, Wait));

            f.MoveTo(null, "a.jpg");

            Assert.Equal([null, f.Photos.Combine("Photos")], f.Started);
        });
    }

    [Fact]
    public void Cancel_MovesNothing()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();

            f.MoveTo(null, "a.jpg");

            Assert.True(File.Exists(f.Photos.Combine("Photos", "a.jpg")));
            Assert.Empty(f.Questions);
            Assert.Empty(f.Summaries);
        });
    }

    [Fact]
    public void IntoAnotherLibrary_AsksNamingIt()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Others.Add(new OtherLibrary("Work", [new LibraryFolder(f.Outside.Path, Recursive: true)]));

            f.MoveTo(f.Outside.Path, "a.jpg");

            var question = Assert.Single(f.Questions);
            Assert.Contains("“Work”", question);
            Assert.Contains("tags and notes", question);
            Assert.True(File.Exists(f.Outside.Combine("a.jpg")));
            Assert.NotEqual(f.UiThread, Assert.Single(f.OthersReadOn)); // read off the UI thread
        });
    }

    [Fact]
    public void OutsideEveryLibrary_Asks_AndNo_MovesNothing()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Yes = false;

            f.MoveTo(f.Outside.Path, "a.jpg");

            Assert.Contains("isn't in any of your libraries", Assert.Single(f.Questions));
            Assert.True(File.Exists(f.Photos.Combine("Photos", "a.jpg")));
            Assert.Empty(Directory.GetFiles(f.Outside.Path));
        });
    }

    [Fact]
    public void UnderAFolderWithoutSubfolders_Asks_NamingIt()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var phone = f.Photos.Combine("Phone");
            Directory.CreateDirectory(Path.Combine(phone, "Camera"));
            f.Vm.AddFolder(phone, includeSubfolders: false);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsIndexing, Wait));
            f.Vm.NavigateToNode(null);

            f.MoveTo(Path.Combine(phone, "Camera"), "a.jpg");

            var question = Assert.Single(f.Questions);
            Assert.Contains("“Phone”", question);
            Assert.Contains("subfolders", question);
        });
    }

    [Fact]
    public void SomeNotMoved_SaysHowManyAndWhy()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            TestImages.Write(Path.Combine(f.Trips, "b.jpg"), 32, 24); // b's name is taken there
            f.Select("a.jpg", "b.jpg");

            f.MoveTo(f.Trips, "a.jpg");

            var summary = Assert.Single(f.Summaries);
            Assert.Contains("Moved 1 of 2 files", summary);
            Assert.Contains("1 — a file of that name is already there", summary);
        });
    }

    [Fact]
    public void AfterAMove_AnUnwatchedFolder_IsReindexed_AndTheGridFollows()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var started = new List<long>();
            f.Library.IndexStarting = root => started.Add(root.Id);

            f.MoveTo(f.Trips, "a.jpg");

            Assert.Contains(f.Root.Id, started);
            Assert.True(Sta.PumpUntil(() => f.Tiles.Any(t => t.FullPath == Path.Combine(f.Trips, "a.jpg"))
                && f.Tiles.All(t => t.FullPath != f.Photos.Combine("Photos", "a.jpg")), Wait));
        });
    }

    [Fact]
    public void AfterAMove_AWatchedFolder_FollowsByItsWatcher()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Library.Watch(f.Root);
            var started = 0;
            f.Library.IndexStarting = _ => started++;

            f.MoveTo(f.Trips, "a.jpg");

            Assert.Equal(0, started); // none started by the move itself: the watcher's own
            Assert.True(Sta.PumpUntil(() => f.Tiles.Any(t => t.FullPath == Path.Combine(f.Trips, "a.jpg")), Wait));
        });
    }

    [Fact]
    public void AMovedFile_ShowsItsTags_InItsNewFolder()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture(library =>
                library.SaveAnnotation(Path.Combine(library.GetRoots()[0].Path, "a.jpg"), ["trip"], "rome"));

            f.MoveTo(f.Trips, "a.jpg");

            Assert.True(Sta.PumpUntil(() => f.Tiles.Any(t => t.FullPath == Path.Combine(f.Trips, "a.jpg")), Wait));
            var moved = f.Tiles.Single(t => t.FullPath == Path.Combine(f.Trips, "a.jpg"));
            Assert.Equal(["trip"], moved.Annotation.Tags);
            Assert.Equal("rome", moved.Annotation.Note);
        });
    }

    [Fact]
    public void WhileAMoveRuns_MoveToIsOff_AndTheStatusSaysSo()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            using var release = new ManualResetEventSlim();
            f.Library.MovingFile = _ => release.Wait(Wait);
            f.Select("a.jpg", "b.jpg");
            f.Chosen = f.Trips;

            f.Vm.MoveToCommand.Execute(f.Tile("a.jpg"));

            Assert.True(f.Vm.IsMoving);
            Assert.False(f.Vm.MoveToCommand.CanExecute(f.Tile("c.jpg")));
            Assert.Equal("Moving 2 files to “Trips”…", f.Vm.MoveText);
            release.Set();
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsMoving, Wait));
            Assert.Equal("", f.Vm.MoveText);
            Assert.True(f.Vm.MoveToCommand.CanExecute(f.Tile("c.jpg")));
        });
    }

    [Fact]
    public void AMovedFolderPaused_IsLeftPaused()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var cloud = f.Photos.Combine("Cloud");
            TestImages.Write(Path.Combine(cloud, "c1.jpg"), 32, 24);
            File.SetAttributes(Path.Combine(cloud, "c1.jpg"), FileAttributes.Offline);
            f.Vm.ShowOnlineOnlyDialog = q =>
            {
                f.Vm.PauseRootIndexCommand.Execute(f.Vm.Roots.Single(r => r.Alias == "Cloud")); // paused at its question
                return new OnlineOnlyAnswer(OnlineOnlyChoice.Skip, Remember: false);
            };
            f.Vm.AddFolder(cloud, includeSubfolders: true);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsIndexing, Wait));
            var cloudRoot = f.Vm.Roots.Single(r => r.Alias == "Cloud");
            Assert.True(cloudRoot.IsPaused);
            var started = new List<long>();
            f.Library.IndexStarting = root => started.Add(root.Id);

            f.MoveTo(cloud, "a.jpg");

            Assert.DoesNotContain(cloudRoot.Id, started);
            Assert.True(cloudRoot.IsPaused);
            Assert.Contains(f.Root.Id, started); // the folder it left is re-indexed
        });
    }
}
