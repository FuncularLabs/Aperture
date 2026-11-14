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
    public void AFileGoneFromDisk_IsSaid_NotDroppedQuietly()
    {
        // Round 1, F6: the files aren't checked on disk on the UI thread; the move says which were no longer there.
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg");
            File.Delete(f.Photos.Combine("Photos", "b.jpg"));

            f.MoveTo(f.Trips, "a.jpg");

            var summary = Assert.Single(f.Summaries);
            Assert.Contains("Moved 1 of 2 files", summary);
            Assert.Contains("1 — no longer on disk", summary);
        });
    }

    [Fact]
    public void TheQuestion_SaysOneFileOrSeveral()
    {
        var root = new Models.Root { Id = 2, Path = @"D:\Phone", Alias = "Phone", Recursive = false };
        var notIndexed = new MoveDestination(MoveDestinationKind.NotIndexedHere, @"D:\Phone\Camera") { UnderRoot = root };
        var other = new MoveDestination(MoveDestinationKind.OtherLibrary, @"F:\Work") { LibraryName = "Work" };
        var none = new MoveDestination(MoveDestinationKind.NoLibrary, @"H:\Elsewhere");

        Assert.EndsWith("Move 1 file there? It will leave the view.", MainViewModel.MoveQuestion(notIndexed, 1));
        Assert.EndsWith("Move 2 files there? They will leave the view.", MainViewModel.MoveQuestion(notIndexed, 2));
        Assert.Contains("Move 1 file there? It will leave this library, and the tags and notes you gave it here",
            MainViewModel.MoveQuestion(other, 1));
        Assert.Contains("Move 3 files there? They won't show in Aperture any more (their tags", MainViewModel.MoveQuestion(none, 3));
        Assert.Null(MainViewModel.MoveQuestion(new MoveDestination(MoveDestinationKind.ThisLibrary, @"C:\Photos"), 1));
    }

    [Fact]
    public void TheSummary_SaysWhenTagsStayedBehind()
    {
        var folder = @"C:\Photos\Trips";
        FileMove M(string name, MoveOutcome outcome) => new(@"C:\Photos\" + name, folder + @"\" + name, outcome);

        Assert.Null(MainViewModel.MoveSummary([M("a.jpg", MoveOutcome.Moved)], folder));
        var summary = MainViewModel.MoveSummary(
            [M("a.jpg", MoveOutcome.MovedTagsBehind), M("b.jpg", MoveOutcome.Moved), M("c.jpg", MoveOutcome.InUse)], folder)!;

        Assert.StartsWith("Moved 2 of 3 files to “Trips”.", summary);
        Assert.Contains("1 file moved without its tags and notes", summary);
        Assert.Contains("• 1 — in use by another program", summary);
        Assert.Equal("Moved 1 of 1 file to “Trips”.\n\n1 file moved without its tags and notes: the library couldn't be "
                     + "written, so they stay under the old path.",
            MainViewModel.MoveSummary([M("a.jpg", MoveOutcome.MovedTagsBehind)], folder));
    }

    [Fact]
    public void TheSummary_SaysAFileCantStayEncrypted_OrWasntReached()
    {
        // Round 4, F6 / N-b: said of one file or many alike.
        var folder = @"E:\Photos";
        var summary = MainViewModel.MoveSummary([new FileMove(@"C:\Photos\a.jpg", folder + @"\a.jpg", MoveOutcome.CantStayEncrypted)], folder);

        Assert.Contains("• 1 — encrypted, and that drive can't keep files encrypted", summary);
        Assert.Contains("• 1 — not reached: Aperture stopped the move", // round 5, F-8 (e)
            MainViewModel.MoveSummary([new FileMove(@"C:\Photos\b.jpg", folder + @"\b.jpg", MoveOutcome.Stopped)], folder));
    }

    [Fact]
    public void AMovedOnlineOnlyFile_StaysInTheView_UnderSkip()
    {
        // Round 1 (lens 2, finding 2): its row moves with it, so the next run has nothing to read or ask about.
        Sta.Run(() =>
        {
            using var f = new Fixture(library =>
            {
                var cloud = Path.Combine(library.GetRoots()[0].Path, "cloud.jpg");
                TestImages.Write(cloud, 32, 24);
                File.SetAttributes(cloud, File.GetAttributes(cloud) | FileAttributes.Offline);
                library.IndexRoot(library.GetRoots()[0]); // indexed, thumbnails and all
                new OnlineOnlyChoices(library.Settings).Answer(library.GetRoots()[0].Id, OnlineOnlyChoice.Skip, keep: true);
            });
            var asked = 0;
            f.Vm.ShowOnlineOnlyDialog = _ => { asked++; return null; };

            f.MoveTo(f.Trips, "cloud.jpg");

            Assert.True(Sta.PumpUntil(() => f.Tiles.Any(t => t.FullPath == Path.Combine(f.Trips, "cloud.jpg"))
                && !f.Vm.IsIndexing, Wait));
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.Contains(f.Tiles, t => t.FullPath == Path.Combine(f.Trips, "cloud.jpg")); // and it stays
            Assert.Equal(0, asked);
        });
    }

    [Fact]
    public void AFileShownTwice_ByNestedFolders_IsCutOnce_AndMovedOnce()
    {
        // Round 1 (lens 2, finding 5): the same file under two of the library's folders is two tiles.
        Sta.Run(() =>
        {
            using var f = new Fixture(library => library.IndexRoot(
                library.AddRoot(Path.Combine(library.GetRoots()[0].Path, "Trips"), "Trips")));
            var clipboard = new List<System.Windows.IDataObject>();
            f.Vm.SetClipboard = clipboard.Add;
            var both = f.Tiles.Where(t => t.FileName == "t.jpg").ToList();
            Assert.Equal(2, both.Count);
            f.Vm.SetSelectedItems(both);

            f.Vm.CutFileCommand.Execute(both[0]);
            Assert.Single(((System.Windows.DataObject)Assert.Single(clipboard)).GetFileDropList());

            f.Chosen = f.Outside.Path;
            f.Vm.MoveToCommand.Execute(both[0]);
            Assert.True(Sta.PumpUntil(() => !f.Vm.IsMoving, Wait));
            Assert.Empty(f.Summaries); // one move, done: not a second "no longer on disk"
            Assert.True(File.Exists(f.Outside.Combine("t.jpg")));
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
