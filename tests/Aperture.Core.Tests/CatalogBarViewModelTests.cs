using System.Windows.Input;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The nav's library switcher, driven against a real <see cref="CatalogService"/> on temp folders with
/// scripted dialogs. Pins Paul's 2026-10-25 rulings: New makes a dedicated folder, Open needs an existing
/// library, Remove/Delete act only on libraries you aren't in, and Delete never touches anything but
/// Aperture's own files.
/// </summary>
public class CatalogBarViewModelTests
{
    private sealed class FakeDialogs : ICatalogDialogs
    {
        public Queue<string?> Prompts { get; } = new();
        public Queue<string?> Folders { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public List<string> PromptInitials { get; } = [];
        public List<string> FolderInitialDirs { get; } = [];
        public List<string> ConfirmMessages { get; } = [];
        public List<(string Message, bool IsError)> Messages { get; } = [];

        public string? PromptText(string title, string label, string initial)
        {
            PromptInitials.Add(initial);
            return Prompts.Count > 0 ? Prompts.Dequeue() : null;
        }

        public string? PickFolder(string title, string initialDirectory)
        {
            FolderInitialDirs.Add(initialDirectory);
            return Folders.Count > 0 ? Folders.Dequeue() : null;
        }

        public bool Confirm(string title, string message)
        {
            ConfirmMessages.Add(message);
            return Confirms.Count > 0 && Confirms.Dequeue();
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

        public Fixture()
        {
            Service = CatalogService.Open(Outer.Combine("catalogs.json"), Outer.Combine("default"));
            Bar = new CatalogBarViewModel(Service, Dialogs, LibrariesDir);
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

    [Fact]
    public async Task Refresh_ShowsTheActiveName_AndTheOtherLibraries_MostRecentlyOpenedFirst()
    {
        await using var f = new Fixture();
        var older = f.AddLibrary("Older");
        var recent = f.AddLibrary("Recent");
        var defaultId = f.Service.ActiveCatalog!.Id;
        await f.Service.SwitchToAsync(older.Id);
        await f.Service.SwitchToAsync(recent.Id);
        await f.Service.SwitchToAsync(defaultId);

        f.Bar.Refresh();

        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Equal(["Recent", "Older"], f.Bar.OtherCatalogs.Select(c => c.Name));
        Assert.True(f.Bar.HasOthers);
    }

    [Fact]
    public async Task Refresh_WithOneLibrary_HasNoOthers()
    {
        await using var f = new Fixture();

        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Empty(f.Bar.OtherCatalogs);
        Assert.False(f.Bar.HasOthers);
    }

    [Fact]
    public async Task Switch_ChangesTheActiveLibrary_AndRefreshes()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");

        await f.Run(f.Bar.SwitchCommand, work.Id);

        Assert.Equal("Work", f.Bar.ActiveName);
        Assert.Equal(["Default"], f.Bar.OtherCatalogs.Select(c => c.Name));
    }

    // --- New library ---------------------------------------------------------------------------

    [Fact]
    public async Task NewLibrary_CreatesItsOwnFolder_InTheChosenLocation_AndSwitches()
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("Travel");
        f.Dialogs.Folders.Enqueue(f.Outer.Path);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Equal("Travel", f.Bar.ActiveName);
        Assert.True(File.Exists(Path.Combine(f.Outer.Path, "Travel", ApertureDatabase.MetadataFileName)));
        Assert.Equal([f.LibrariesDir], f.Dialogs.FolderInitialDirs); // suggests the libraries folder…
        Assert.True(Directory.Exists(f.LibrariesDir));                // …which exists so the picker can open it
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task NewLibrary_CancelledAtTheName_AsksNothingElse()
    {
        await using var f = new Fixture();

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Empty(f.Dialogs.FolderInitialDirs);
        Assert.Single(f.Service.Registry.List());
    }

    [Fact]
    public async Task NewLibrary_CancelledAtTheFolder_CreatesNothing()
    {
        await using var f = new Fixture();
        f.Dialogs.Prompts.Enqueue("Travel");

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Single(f.Service.Registry.List());
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
    public async Task NewLibrary_FolderAlreadyInUse_SaysWhy_AndLeavesItAlone()
    {
        await using var f = new Fixture();
        var photos = f.Outer.Combine("Photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "IMG_0001.jpg"), "a photo");
        f.Dialogs.Prompts.Enqueue("Photos");
        f.Dialogs.Folders.Enqueue(f.Outer.Path);

        await f.Run(f.Bar.NewLibraryCommand);

        Assert.Contains(photos, Assert.Single(f.Dialogs.Messages).Message);
        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Equal(["IMG_0001.jpg"], Directory.GetFiles(photos).Select(Path.GetFileName));
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

        await f.Run(f.Bar.RenameCommand);

        Assert.Equal(["Default"], f.Dialogs.PromptInitials); // prefilled with the current name
        Assert.Equal("Home", f.Bar.ActiveName);
    }

    [Fact]
    public async Task Rename_CancelledOrUnchanged_DoesNothing()
    {
        await using var f = new Fixture();
        await f.Run(f.Bar.RenameCommand); // cancelled
        f.Dialogs.Prompts.Enqueue("Default");
        await f.Run(f.Bar.RenameCommand); // unchanged

        Assert.Equal("Default", f.Bar.ActiveName);
        Assert.Empty(f.Dialogs.Messages);
    }

    [Fact]
    public async Task Rename_ToATakenName_SaysWhy()
    {
        await using var f = new Fixture();
        f.AddLibrary("Work");
        f.Dialogs.Prompts.Enqueue("work");

        await f.Run(f.Bar.RenameCommand);

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

        Assert.Empty(f.Bar.OtherCatalogs);
        Assert.True(File.Exists(Path.Combine(work.Dir, ApertureDatabase.MetadataFileName)));
    }

    [Fact]
    public async Task DeleteFiles_Declined_KeepsEverything()
    {
        await using var f = new Fixture();
        var work = f.AddLibrary("Work");
        f.Dialogs.Confirms.Enqueue(false);

        await f.Run(f.Bar.DeleteFilesCommand, work.Id);

        Assert.Single(f.Bar.OtherCatalogs);
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
        Assert.Empty(f.Bar.OtherCatalogs);
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
        f.Bar.RenameCommand.Execute(null); // ignored, so no prompt is shown
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
}
