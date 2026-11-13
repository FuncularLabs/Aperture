using System.Windows.Input;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The libraries in the nav, driven against a real <see cref="CatalogService"/> on temp folders with scripted
/// dialogs. Pins Paul's 2026-10-25 rulings (New makes a dedicated folder, Open needs an existing library,
/// Remove/Delete act only on libraries you aren't in, Delete never touches anything but Aperture's own files) and
/// his 2026-10-26 trial: every library listed in the pane, and New library asks for the folder to put in it.
/// </summary>
public class CatalogBarViewModelTests
{
    private sealed class FakeDialogs : ICatalogDialogs
    {
        public Queue<string?> Prompts { get; } = new();
        public Queue<string?> Folders { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public Queue<bool?> Subfolders { get; } = new();
        public List<string> SubfolderQuestions { get; } = [];
        public List<string> PromptInitials { get; } = [];
        public List<string> FolderInitialDirs { get; } = [];
        public List<string> Asked { get; } = []; // every question, in order
        public List<string> ConfirmMessages { get; } = [];
        public List<(string Message, bool IsError)> Messages { get; } = [];

        public string? PromptText(string title, string label, string initial)
        {
            Asked.Add("name");
            PromptInitials.Add(initial);
            return Prompts.Count > 0 ? Prompts.Dequeue() : null;
        }

        public string? PickFolder(string title, string initialDirectory)
        {
            Asked.Add("folder");
            FolderInitialDirs.Add(initialDirectory);
            return Folders.Count > 0 ? Folders.Dequeue() : null;
        }

        public bool Confirm(string title, string message)
        {
            ConfirmMessages.Add(message);
            return Confirms.Count > 0 && Confirms.Dequeue();
        }

        public bool? AskIncludeSubfolders(string folder)
        {
            Asked.Add("subfolders");
            SubfolderQuestions.Add(folder);
            return Subfolders.Count > 0 ? Subfolders.Dequeue() : null;
        }

        public void ShowMessage(string title, string message, bool isError) => Messages.Add((message, isError));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TempDir Outer { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public CatalogService Service { get; }
        public CatalogBarViewModel Bar { get; }
        public string LibrariesDir => Outer.Combine("Aperture Libraries");
        public string PicturesDir => Outer.Combine("Pictures");
        public List<NewLibraryFolder> HandedOver { get; } = [];
        public List<string?> ActiveWhenHandedOver { get; } = [];

        public Fixture(Func<string, int?>? countLibrary = null)
        {
            Service = CatalogService.Open(Outer.Combine("catalogs.json"), Outer.Combine("default"));
            Bar = new CatalogBarViewModel(Service, Dialogs, LibrariesDir, PicturesDir, countLibrary);
            Bar.NewLibraryFolderChosen += (_, folder) =>
            {
                HandedOver.Add(folder);
                ActiveWhenHandedOver.Add(Service.ActiveCatalog?.Name);
            };
        }

        public CatalogEntry AddLibrary(string name)
        {
            var dir = Outer.Combine(name);
            using (new LibraryService(dir)) { }
            var entry = Service.Registry.Add(name, dir);
            Bar.Refresh();
            return entry;
        }

        public async Task Run(ICommand command, object? parameter = null)
        {
            command.Execute(parameter);
            if (Bar.PendingOperation is { } operation)
                await operation;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Outer.Dispose();
        }
    }

    private static string Listed(CatalogBarViewModel bar) =>
        string.Join(", ", bar.Libraries.Select(l => l.IsActive ? $"[{l.Name}]" : l.Name));

    [Fact]
    public async Task Libraries_ListEveryLibraryByName_MarkingTheOpenOne()
    {
        await using var f = new Fixture();
        var zoo = f.AddLibrary("zoo");
        f.AddLibrary("Apple");

        Assert.Equal("Apple, [Default], zoo", Listed(f.Bar));
        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Equal(zoo.Dir, f.Bar.Libraries.Single(l => l.Name == "zoo").Dir);
        Assert.Equal(zoo.Id, f.Bar.Libraries.Single(l => l.Name == "zoo").Id);
    }

    [Fact]
    public async Task Libraries_CarryEachLibrarysLastCount_UntilItsOwnFilesAreRead()
    {
        var held = new ManualResetEventSlim();
        await using var f = new Fixture(dir => { held.Wait(TimeSpan.FromSeconds(10)); return LibraryCount.Read(dir); });
        var work = f.AddLibrary("Work");
        f.Service.Registry.SetItemCount(work.Id, 42);

        f.Bar.Refresh();

        Assert.Equal(42, f.Bar.Libraries.Single(l => l.Name == "Work").ItemCount); // at once: the count it had
        Assert.Null(f.Bar.Libraries.Single(l => l.Name == "Default").ItemCount);   // the open one: its live count instead
        held.Set();
        await f.Bar.PendingCounts;
        Assert.Equal(0, f.Bar.Libraries.Single(l => l.Name == "Work").ItemCount);  // then what its files say (it's empty)
    }

    [Fact]
    public async Task Libraries_WithOneLibrary_ListJustIt()
    {
        await using var f = new Fixture();

        Assert.Equal("[Default]", Listed(f.Bar));
    }

    [Fact]
    public async Task Libraries_FollowASwitch()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        var changes = new List<string?>();
        f.Bar.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await f.Run(f.Bar.SwitchCommand, work.Id);

        Assert.Equal("Work", f.Bar.ActiveName);
        Assert.Equal("Default, [Work]", Listed(f.Bar));
        Assert.Contains(nameof(CatalogBarViewModel.Libraries), changes);
        Assert.Contains(nameof(CatalogBarViewModel.ActiveName), changes);
    }

    [Fact]
    public async Task IsExpanded_StartsOpen_AndToggles()
    {
        await using var f = new Fixture();
        var changes = new List<string?>();
        f.Bar.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(f.Bar.IsExpanded);
        f.Bar.ToggleExpandedCommand.Execute(null);
        Assert.False(f.Bar.IsExpanded);
        f.Bar.ToggleExpandedCommand.Execute(null);
        Assert.True(f.Bar.IsExpanded);
        Assert.Equal([nameof(CatalogBarViewModel.IsExpanded), nameof(CatalogBarViewModel.IsExpanded)], changes);
    }

    // --- New library ---------------------------------------------------------------------------

    [Fact]
    public async Task NewLibrary_AsksForANameThenAFolderThenSubfolders_CreatesItUnderTheLibrariesFolder_AndHandsOverTheFolder()
    {
        await using var f = new Fixture();
        var photos = f.Outer.Combine("My photos");
        Directory.CreateDirectory(photos);
        f.Dialogs.Prompts.Enqueue("Travel");
        f.Dialogs.Folders.Enqueue(photos);
        f.Dialogs.Subfolders.Enqueue(true);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Equal(["name", "folder", "subfolders"], f.Dialogs.Asked);
        Assert.Equal([f.PicturesDir], f.Dialogs.FolderInitialDirs); // a folder of photos — not where Aperture keeps its files
        Assert.Equal([photos], f.Dialogs.SubfolderQuestions);
        Assert.Equal("Travel", f.Bar.ActiveName);
        Assert.Equal(Path.Combine(f.LibrariesDir, "Travel"), f.Service.ActiveCatalog!.Dir);
        Assert.True(File.Exists(Path.Combine(f.LibrariesDir, "Travel", ApertureDatabase.MetadataFileName)));
        Assert.False(File.Exists(Path.Combine(photos, ApertureDatabase.MetadataFileName))); // the photos folder holds only photos
        Assert.Equal([new NewLibraryFolder(photos, IncludeSubfolders: true)], f.HandedOver);
        Assert.Equal(["Travel"], f.ActiveWhenHandedOver); // handed to the new library, once it's the open one
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task NewLibrary_WithoutSubfolders_HandsOverTheFolderWithoutThem()
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("Travel");
        f.Dialogs.Folders.Enqueue(f.Outer.Path);
        f.Dialogs.Subfolders.Enqueue(false);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Equal([new NewLibraryFolder(f.Outer.Path, IncludeSubfolders: false)], f.HandedOver);
    }

    [Fact]
    public async Task NewLibrary_CancelledAtTheName_AsksNothingElse()
    {
        await using var f = new Fixture();

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Equal(["name"], f.Dialogs.Asked);
        Assert.Single(f.Service.Registry.List());
    }

    [Theory]
    [InlineData(false)] // cancelled at the folder
    [InlineData(true)]  // cancelled at the subfolder question
    public async Task NewLibrary_CancelledAtTheFolderOrTheSubfolderQuestion_CreatesNothing(bool pickAFolder)
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("Travel");
        if (pickAFolder)
            f.Dialogs.Folders.Enqueue(f.Outer.Path);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Equal(pickAFolder ? ["name", "folder", "subfolders"] : ["name", "folder"], f.Dialogs.Asked);
        Assert.Single(f.Service.Registry.List());
        Assert.False(Directory.Exists(Path.Combine(f.LibrariesDir, "Travel")));
        Assert.Empty(f.HandedOver);
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task NewLibrary_BadName_SaysWhy_BeforeAskingForAFolder()
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("a/b");

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Empty(f.Dialogs.FolderInitialDirs);
        var (message, isError) = Assert.Single(f.Dialogs.Messages);
        Assert.True(isError);
        Assert.Contains("can't contain", message);
    }

    [Fact]
    public async Task NewLibrary_ThatFails_HandsOverNothing()
    {
        await using var f = new Fixture();
        File.WriteAllText(f.LibrariesDir, "a file where the libraries folder should be"); // nothing can be created under it
        f.Dialogs.Prompts.Enqueue("Travel");
        f.Dialogs.Folders.Enqueue(f.Outer.Path);
        f.Dialogs.Subfolders.Enqueue(true);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.True(Assert.Single(f.Dialogs.Messages).IsError);
        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Empty(f.HandedOver);
    }

    // --- Open library ---------------------------------------------------------------------------

    [Fact]
    public async Task OpenLibrary_ExistingLibrary_SwitchesToIt()
    {
        await using var f = new Fixture();
        var elsewhere = f.Outer.Combine("Elsewhere");
        using (new LibraryService(elsewhere)) { }
        f.Dialogs.Folders.Enqueue(elsewhere);

        await f.Run(f.Bar.OpenLibraryCommand);

        Assert.Equal("Elsewhere", f.Bar.ActiveName);
    }

    [Fact]
    public async Task OpenLibrary_FolderWithoutALibrary_SaysWhy()
    {
        await using var f = new Fixture();
        var plain = f.Outer.Combine("Plain");
        Directory.CreateDirectory(plain);
        f.Dialogs.Folders.Enqueue(plain);

        await f.Run(f.Bar.OpenLibraryCommand);

        Assert.True(Assert.Single(f.Dialogs.Messages).IsError);
        Assert.Single(f.Service.Registry.List());
    }

    [Fact]
    public async Task OpenLibrary_Cancelled_DoesNothing()
    {
        await using var f = new Fixture();

        await f.Run(f.Bar.OpenLibraryCommand);

        Assert.Single(f.Service.Registry.List());
        Assert.Empty(f.Dialogs.Messages);
    }

    // --- Rename -----------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_RenamesTheOpenLibrary()
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("Home");

        await f.Run(f.Bar.RenameCommand, f.Service.ActiveCatalog!.Id);

        Assert.Equal(["Default"], f.Dialogs.PromptInitials); // prefilled with the current name
        Assert.Equal("Home", f.Bar.ActiveName);
    }

    [Fact]
    public async Task Rename_RenamesTheLibraryItIsGiven()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        f.Dialogs.Prompts.Enqueue("Office");

        await f.Run(f.Bar.RenameCommand, work.Id);

        Assert.Equal(["Work"], f.Dialogs.PromptInitials);
        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Equal("[Default], Office", Listed(f.Bar));
    }

    [Fact]
    public async Task Rename_CancelledOrUnchanged_DoesNothing()
    {
        await using var f = new Fixture();
        var open = f.Service.ActiveCatalog!.Id;
        await f.Run(f.Bar.RenameCommand, open); // cancelled
        f.Dialogs.Prompts.Enqueue("Default");
        await f.Run(f.Bar.RenameCommand, open); // unchanged

        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Rename_ToATakenName_SaysWhy()
    {
        await using var f = new Fixture();
        f.AddLibrary("Work");
        f.Dialogs.Prompts.Enqueue("work");

        await f.Run(f.Bar.RenameCommand, f.Service.ActiveCatalog!.Id);

        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.True(Assert.Single(f.Dialogs.Messages).IsError);
    }

    // --- Remove / delete (only libraries you aren't in) -------------------------------------------

    [Fact]
    public async Task Remove_AnotherLibrary_ForgetsIt_AndKeepsItsFiles()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");

        await f.Run(f.Bar.RemoveCommand, work.Id);

        Assert.Equal("[Default]", Listed(f.Bar));
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task RemoveAndDelete_AreDisabledForTheOpenLibrary()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        var open = f.Service.ActiveCatalog!.Id;

        Assert.False(f.Bar.RemoveCommand.CanExecute(open));
        Assert.False(f.Bar.DeleteFilesCommand.CanExecute(open));
        Assert.True(f.Bar.RemoveCommand.CanExecute(work.Id));
        Assert.True(f.Bar.DeleteFilesCommand.CanExecute(work.Id));
        Assert.True(f.Bar.RenameCommand.CanExecute(open));
    }

    [Fact]
    public async Task DeleteFiles_Declined_KeepsEverything()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        f.Dialogs.Confirms.Enqueue(false);

        await f.Run(f.Bar.DeleteFilesCommand, work.Id);

        Assert.Equal("[Default], Work", Listed(f.Bar));
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFiles_Confirmed_DeletesOnlyApertureFiles_AndTheWarningSaysSoTruthfully()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        File.WriteAllText(Path.Combine(work.Dir, "keep.txt"), "not Aperture's");
        f.Dialogs.Confirms.Enqueue(true);

        await f.Run(f.Bar.DeleteFilesCommand, work.Id);

        var warning = Assert.Single(f.Dialogs.ConfirmMessages);
        Assert.Contains(work.Dir, warning);
        Assert.Contains("not touched", warning);
        Assert.Equal("[Default]", Listed(f.Bar));
        Assert.False(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
        Assert.True(File.Exists(Path.Combine(work.Dir, "keep.txt")));
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task DeleteFiles_SomeFilesInUse_SaysWhichWereLeft()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        var settings = Path.Combine(work.Dir, "settings.json");
        File.WriteAllText(settings, "{}");
        f.Dialogs.Confirms.Enqueue(true);

        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            await f.Run(f.Bar.DeleteFilesCommand, work.Id);

        var (message, isError) = Assert.Single(f.Dialogs.Messages);
        Assert.True(isError);
        Assert.Contains(settings, message);
        Assert.Contains("the files below couldn't be deleted", message); // true whether some or all were left
        Assert.Contains("another program may be using them, or Windows didn't allow it", message); // not one guessed cause
    }

    [Fact]
    public async Task DeleteFiles_WhenTheFolderGoesAwayMidDelete_SaysNotEverythingWasDeleted_AndHowToFinish()
    {
        await using var f = new Fixture();
        var usb = f.AddLibrary("USB");
        await f.Bar.PendingCounts; // its count is read (and remembered) before the drive drops: only the delete may move it
        var unplugged = f.Outer.Combine("USB-unplugged");
        f.Service.Registry.BetweenReadAndChange = () => Directory.Move(usb.Dir, unplugged); // the drive drops mid-delete
        f.Dialogs.Confirms.Enqueue(true);

        await f.Run(f.Bar.DeleteFilesCommand, usb.Id);
        f.Service.Registry.BetweenReadAndChange = null;

        var (message, isError) = Assert.Single(f.Dialogs.Messages);
        Assert.True(isError);
        Assert.Contains(usb.Dir, message);
        Assert.Contains("not all of its files could be deleted", message);
        Assert.Contains("Open library", message); // how to finish once the drive is back —
        Assert.Contains("switch to another library", message); // a library can't be deleted while it's the open one
        Assert.DoesNotContain("another program", message);
        Assert.True(File.Exists(Path.Combine(unplugged, ApertureDatabase.MetadataFileName)));
    }

    // --- Busy / errors ----------------------------------------------------------------------------

    [Fact]
    public async Task WhileASwitchRuns_EveryCommandIsDisabled_AndIgnored()
    {
        await using var f = new Fixture();
        using var lib = new TempDir();
        TestImages.Write(Path.Combine(lib.Path, "a.jpg"), 64, 64);
        var work = f.AddLibrary("Work");
        var held = new BlockingProgress();
        var indexing = f.Service.Current.IndexRootAsync(f.Service.Current.AddRoot(lib.Path, "L"), held);
        Assert.True(held.Entered.Wait(5000));

        f.Bar.SwitchCommand.Execute(work.Id);
        var switching = f.Bar.PendingOperation!;
        Assert.True(f.Bar.IsBusy);
        Assert.False(f.Bar.SwitchCommand.CanExecute(work.Id));
        Assert.False(f.Bar.NewLibraryCommand.CanExecute(null));
        Assert.False(f.Bar.DeleteFilesCommand.CanExecute(work.Id));
        f.Bar.RenameCommand.Execute(work.Id); // ignored, so no prompt is shown
        Assert.Same(switching, f.Bar.PendingOperation);

        held.Release();
        await switching;
        await Record.ExceptionAsync(() => indexing);

        Assert.False(f.Bar.IsBusy);
        Assert.True(f.Bar.SwitchCommand.CanExecute(work.Id));
        Assert.Empty(f.Dialogs.PromptInitials);
        Assert.Equal("Work", f.Bar.ActiveName);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsShown_NotThrown()
    {
        await using var f = new Fixture();

        await f.Run(f.Bar.SwitchCommand, "no-such-library");

        var (message, isError) = Assert.Single(f.Dialogs.Messages);
        Assert.True(isError);
        Assert.StartsWith("The library operation failed", message);
        Assert.False(f.Bar.IsBusy);
    }

    // --- Order (0.9.1 batch, item 5) ----------------------------------------------------------------

    private static async Task<CatalogBarViewModel> NextLaunch(Fixture f)
    {
        // Another session over the same list file: what the user sees after a restart.
        var service = CatalogService.Open(f.Outer.Combine("catalogs.json"), f.Outer.Combine("default"));
        var bar = new CatalogBarViewModel(service, f.Dialogs, f.LibrariesDir, f.PicturesDir, _ => null);
        await service.DisposeAsync();
        return bar;
    }

    [Fact]
    public async Task TheList_IsAlphabetical_UntilALibraryIsMoved_ThenFollowsTheOrder_AcrossARestart()
    {
        await using var f = new Fixture(_ => null);
        var zoo = f.AddLibrary("zoo");
        f.AddLibrary("Apple");
        Assert.Equal("Apple, [Default], zoo", Listed(f.Bar));

        f.Bar.MoveLibrary(zoo.Id, 0);

        Assert.Equal("zoo, Apple, [Default]", Listed(f.Bar));
        Assert.Equal("zoo, Apple, [Default]", Listed(await NextLaunch(f)));
    }

    [Fact]
    public async Task MoveLibrary_ToEachPlace()
    {
        await using var f = new Fixture(_ => null);
        var a = f.AddLibrary("A");
        f.AddLibrary("B");
        Assert.Equal("A, B, [Default]", Listed(f.Bar));

        f.Bar.MoveLibrary(a.Id, 2);  // the last place
        Assert.Equal("B, [Default], A", Listed(f.Bar));
        f.Bar.MoveLibrary(a.Id, 1);
        Assert.Equal("B, A, [Default]", Listed(f.Bar));
        f.Bar.MoveLibrary(a.Id, 1);  // where it is: nothing to save
        Assert.Equal("B, A, [Default]", Listed(f.Bar));
        f.Bar.MoveLibrary("gone", 0); // a library another window removed meanwhile
        Assert.Equal("B, A, [Default]", Listed(f.Bar));
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task ALibraryAddedLater_GoesAtTheEnd()
    {
        await using var f = new Fixture(_ => null);
        var b = f.AddLibrary("B");
        f.Bar.MoveLibrary(b.Id, 1); // after Default: an order of the user's own

        f.AddLibrary("Apple");

        Assert.Equal("[Default], B, Apple", Listed(f.Bar));
    }

    [Fact]
    public async Task AMoveThatCannotBeSaved_SaysSo_AndTheOrderStaysAsItWas()
    {
        await using var f = new Fixture(_ => null);
        var zoo = f.AddLibrary("zoo");
        var path = f.Outer.Combine("catalogs.json");

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            f.Bar.MoveLibrary(zoo.Id, 0);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal("[Default], zoo", Listed(f.Bar));
        var (message, isError) = Assert.Single(f.Dialogs.Messages);
        Assert.True(isError);
        Assert.Contains("couldn't save its list of libraries", message);
    }

    // --- Counts of the libraries that aren't open (0.9.1 batch, item 6) ---------------------------------

    [Fact]
    public async Task EveryOtherLibrary_GetsItsCount_ReadFromItsOwnFiles()
    {
        var asked = new List<string>();
        await using var f = new Fixture(dir =>
        {
            lock (asked)
                asked.Add(dir);
            return Path.GetFileName(dir) switch { "Work" => 7, "Home" => 3, _ => 99 };
        });
        var work = f.AddLibrary("Work");
        var home = f.AddLibrary("Home");
        await f.Bar.PendingCounts;

        Assert.Equal((7, 3), (f.Bar.Libraries.Single(l => l.Id == work.Id).ItemCount,
            f.Bar.Libraries.Single(l => l.Id == home.Id).ItemCount));
        Assert.Null(f.Bar.Libraries.Single(l => l.IsActive).ItemCount); // the open one shows its live count, not a read
        lock (asked)
            Assert.DoesNotContain(f.Service.ActiveCatalog!.Dir, asked);
    }

    [Fact]
    public async Task TheCount_IsRemembered_ForTheNextLaunch()
    {
        await using var f = new Fixture(_ => 7);
        var work = f.AddLibrary("Work");
        await f.Bar.PendingCounts;

        Assert.Equal(7, new CatalogRegistry(f.Outer.Combine("catalogs.json")).List().Single(c => c.Id == work.Id).ItemCount);
        Assert.Equal(7, (await NextLaunch(f)).Libraries.Single(l => l.Id == work.Id).ItemCount); // shown before any read
    }

    [Fact]
    public async Task ALibraryWhoseFilesCannotBeRead_KeepsTheCountItHad()
    {
        await using var f = new Fixture(_ => null);
        var work = f.AddLibrary("Work");
        f.Service.Registry.SetItemCount(work.Id, 42);
        var unrecorded = f.AddLibrary("Unrecorded");

        f.Bar.Refresh();
        await f.Bar.PendingCounts;

        Assert.Equal(42, f.Bar.Libraries.Single(l => l.Id == work.Id).ItemCount);
        Assert.Null(f.Bar.Libraries.Single(l => l.Id == unrecorded.Id).ItemCount);
        Assert.Equal(42, f.Service.Registry.List().Single(c => c.Id == work.Id).ItemCount);
    }

    [Fact]
    public async Task ACountFromAnOlderRefresh_IsDropped()
    {
        var firstRead = new ManualResetEventSlim();
        var reading = new ManualResetEventSlim();
        var reads = 0;
        var holding = false;
        await using var f = new Fixture(_ =>
        {
            if (!Volatile.Read(ref holding))
                return null;
            if (Interlocked.Increment(ref reads) == 1)
            {
                reading.Set();
                firstRead.Wait(TimeSpan.FromSeconds(10)); // the older refresh's read is slow…
                return 5;
            }
            return null;                                  // …and the newer one can't read the library at all
        });
        var work = f.AddLibrary("Work");
        await f.Bar.PendingCounts;
        f.Service.Registry.SetItemCount(work.Id, 42);
        Volatile.Write(ref holding, true);
        f.Bar.Refresh();                 // the older refresh: its read is held
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
        var older = f.Bar.PendingCounts;

        f.Bar.Refresh();                 // the newer: reads nothing once the older lets go of the library
        firstRead.Set();
        await older;
        await f.Bar.PendingCounts;

        // The older refresh's 5 lands nowhere: not on the list, not in the list file (review R1-B5: a newer read always
        // overwrote it before, so the test could never see it land).
        Assert.Equal(42, f.Bar.Libraries.Single(l => l.Id == work.Id).ItemCount);
        Assert.Equal(42, f.Service.Registry.List().Single(c => c.Id == work.Id).ItemCount);
    }

    [Fact]
    public async Task AMoveToItsOwnPlace_WritesNoOrder()
    {
        // Review R1-B8: a drop on its own place would make the alphabetical list an order of the user's own.
        await using var f = new Fixture(_ => null);
        var zoo = f.AddLibrary("zoo");
        Assert.Equal("[Default], zoo", Listed(f.Bar));

        f.Bar.MoveLibrary(zoo.Id, 1);

        Assert.Null(f.Service.Registry.Order);
        Assert.Null(new CatalogRegistry(f.Outer.Combine("catalogs.json")).Order);
    }

    [Fact]
    public async Task ACountThatLands_ChangesTheItemTheListShows()
    {
        // The list isn't rebuilt for a count: the item it shows says its count changed, for the binding.
        var held = new ManualResetEventSlim();
        await using var f = new Fixture(_ => { held.Wait(TimeSpan.FromSeconds(10)); return 7; });
        var work = f.AddLibrary("Work");
        var item = f.Bar.Libraries.Single(l => l.Id == work.Id);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        held.Set();
        await f.Bar.PendingCounts;

        Assert.Same(item, f.Bar.Libraries.Single(l => l.Id == work.Id));
        Assert.Equal(7, item.ItemCount);
        Assert.Equal([nameof(LibraryListItem.ItemCount)], changed);
    }

    [Fact]
    public async Task DeleteFiles_WaitsForACountReadOfThatLibrary()
    {
        // A read holds the library's database open while it runs: deleting its files meanwhile would leave some behind.
        var held = new ManualResetEventSlim();
        var reading = new ManualResetEventSlim();
        await using var f = new Fixture(dir =>
        {
            reading.Set();
            held.Wait(TimeSpan.FromSeconds(10));
            return LibraryCount.Read(dir);
        });
        var work = f.AddLibrary("Work");
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)));
        f.Dialogs.Confirms.Enqueue(true);

        f.Bar.DeleteFilesCommand.Execute(work.Id);
        var deleting = f.Bar.PendingOperation!;
        await Task.Delay(200);
        Assert.False(deleting.IsCompleted); // waiting for the read
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));

        held.Set();
        await deleting;
        Assert.Equal("[Default]", Listed(f.Bar));
        Assert.False(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
        Assert.Empty(f.Dialogs.Messages);
    }
}
