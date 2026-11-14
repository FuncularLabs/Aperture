using System.Windows;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Cut and Copy on the grid (0.9.2 X1; Paul: "Cut on multiple selections only operates on the first file in the
/// group"): every selected file when the tile acted on is part of the selection, that tile alone otherwise. A real view
/// model on a real library; the clipboard is the view model's seam, so the PC's own clipboard is never touched.
/// </summary>
public class ClipboardTargetsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; }
        public MainViewModel Vm { get; }
        public List<IDataObject> Clipboard { get; } = [];

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg", "d.jpg" })
                TestImages.Write(Photos.Combine(name), 32, 24);
            Library = new LibraryService(Data.Path);
            Library.IndexRoot(Library.AddRoot(Photos.Path, "Photos"));
            Vm = new MainViewModel(Library, new ThumbnailService(Library)) { SetClipboard = Clipboard.Add };
            Assert.True(Sta.PumpUntil(() => Vm.LoadedItemCount is not null && !Vm.IsLoadingView && Tiles.Count == 4, Wait));
        }

        public List<TileVm> Tiles => Vm.ItemsView?.Cast<object>().OfType<TileVm>().ToList() ?? [];

        public TileVm Tile(string name) => Tiles.Single(t => t.FileName == name);

        public void Select(params string[] names) => Vm.SetSelectedItems(names.Select(Tile).ToList());

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    private static string[] Names(IDataObject data) =>
        [.. ((DataObject)data).GetFileDropList().Cast<string>().Select(Path.GetFileName).Order()!];

    private static int Effect(IDataObject data) => ((MemoryStream)data.GetData("Preferred DropEffect")!).ToArray()[0];

    [Fact]
    public void Cut_WithSeveralSelected_PutsThemAll_AsAMove()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg", "c.jpg");

            f.Vm.CutFileCommand.Execute(f.Tile("b.jpg")); // right-clicked one of them

            var data = Assert.Single(f.Clipboard);
            Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], Names(data));
            Assert.Equal(2, Effect(data)); // Explorer's "move"
        });
    }

    [Fact]
    public void Copy_WithSeveralSelected_PutsThemAll_AsACopy()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "d.jpg");

            f.Vm.CopyFileCommand.Execute(f.Tile("a.jpg"));

            var data = Assert.Single(f.Clipboard);
            Assert.Equal(["a.jpg", "d.jpg"], Names(data));
            Assert.Equal(5, Effect(data)); // Explorer's "copy"
        });
    }

    [Fact]
    public void ATileOutsideTheSelection_IsPutAlone()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg");

            f.Vm.CutFileCommand.Execute(f.Tile("d.jpg"));

            Assert.Equal(["d.jpg"], Names(Assert.Single(f.Clipboard)));
        });
    }

    [Fact]
    public void TheKeys_ActOnTheSelection()
    {
        // Ctrl+C / Ctrl+X pass the grid's current tile (MainWindow.OnGridKeyDown), which is one of the selection.
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("b.jpg", "c.jpg");
            f.Vm.SelectedItem = f.Tile("c.jpg");

            f.Vm.CutFileCommand.Execute(f.Vm.SelectedTile);

            Assert.Equal(["b.jpg", "c.jpg"], Names(Assert.Single(f.Clipboard)));
        });
    }

    [Fact]
    public void QuickViewsCutAndCopy_AreTheItemShown_EvenInASelection()
    {
        // Quick view's (and the preview's) menu: the user is looking at one item, whatever else is selected.
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg", "c.jpg");

            f.Vm.CutShownFileCommand.Execute(f.Tile("b.jpg"));
            f.Vm.CopyShownFileCommand.Execute(f.Tile("c.jpg"));

            Assert.Equal(2, f.Clipboard.Count);
            Assert.Equal(["b.jpg"], Names(f.Clipboard[0]));
            Assert.Equal(2, Effect(f.Clipboard[0]));
            Assert.Equal(["c.jpg"], Names(f.Clipboard[1]));
            Assert.Equal(5, Effect(f.Clipboard[1]));
        });
    }

    [Fact]
    public void AFileNoLongerOnDisk_IsLeftOut()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg", "b.jpg", "c.jpg");
            File.Delete(f.Photos.Combine("b.jpg"));

            f.Vm.CutFileCommand.Execute(f.Tile("a.jpg"));

            Assert.Equal(["a.jpg", "c.jpg"], Names(Assert.Single(f.Clipboard)));
        });
    }

    [Fact]
    public void WithNoneLeft_TheClipboardIsLeftAlone()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Select("a.jpg");
            File.Delete(f.Photos.Combine("a.jpg"));

            f.Vm.CopyFileCommand.Execute(f.Tile("a.jpg"));

            Assert.Empty(f.Clipboard);
        });
    }
}
