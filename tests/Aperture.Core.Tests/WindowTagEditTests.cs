using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The window's own tag edits (the tag dialog, a tag's ✕) save under the tile's own path, never the row shown for it
/// (0.9.2 M4; round 29, R F-1): for "σ.jpg" and "ς.jpg", two files on NTFS that the window takes for one path, the row
/// shown on ς.jpg's tile is σ.jpg's, and saving through it would clear or overwrite another file's tags. A real view
/// model on a real library of real files; the dialog is the view model's seam, never a window.
/// </summary>
public class WindowTagEditTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class Fixture : IDisposable
    {
        public TempDir Data { get; } = new();
        public TempDir Photos { get; } = new();
        public LibraryService Library { get; }
        public MainViewModel Vm { get; }
        public string Sigma { get; }
        public string Final { get; }

        public Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Sigma = Photos.Combine("Photos", "σ.jpg");
            Final = Photos.Combine("Photos", "ς.jpg");
            TestImages.Write(Sigma, 32, 24);
            TestImages.Write(Final, 32, 24);
            Library = new LibraryService(Data.Path);
            Library.IndexRoot(Library.AddRoot(Photos.Combine("Photos"), "Photos"));
            Library.SaveAnnotation(Sigma, ["mine", "x"], "my note");
            Vm = new MainViewModel(Library, new ThumbnailService(Library));
            Assert.True(Sta.PumpUntil(() => Vm.LoadedItemCount is not null && !Vm.IsLoadingView && Tiles.Count == 2, Wait));
        }

        public List<TileVm> Tiles => Vm.ItemsView?.Cast<object>().OfType<TileVm>().ToList() ?? [];

        public TileVm FinalsTile => Tiles.Single(t => t.FullPath == Final);

        public void Dispose()
        {
            Vm.PrepareForClose();
            Library.ShutdownAsync().GetAwaiter().GetResult();
            Photos.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void TheDialogsClear_OnTheOtherFilesTile_LeavesThisFilesTags()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            var tile = f.FinalsTile;
            f.Vm.SetSelectedItems(new[] { tile });
            List<string> shown = [];
            f.Vm.ShowAnnotationDialog = dialog =>
            {
                shown = dialog.Tags.Select(c => c.Name).ToList(); // σ.jpg's, shown on ς.jpg's tile
                foreach (var chip in dialog.Tags.ToList())
                    dialog.RemoveTagCommand.Execute(chip);
                dialog.Note = "";
                return true;
            };

            f.Vm.EditAnnotationCommand.Execute(tile);

            Assert.Equal(["mine", "x"], shown);
            Assert.Equal(["mine", "x"], f.Library.GetAnnotation(f.Sigma).Tags);
            Assert.Equal("my note", f.Library.GetAnnotation(f.Sigma).Note);
        });
    }

    [Fact]
    public void ATagsCross_OnTheOtherFilesTile_LeavesThisFilesTags()
    {
        Sta.Run(() =>
        {
            using var f = new Fixture();
            f.Vm.SelectedItem = f.FinalsTile;

            f.Vm.RemovePreviewTagCommand.Execute("x");

            Assert.Equal(["mine", "x"], f.Library.GetAnnotation(f.Sigma).Tags);
            Assert.Equal("my note", f.Library.GetAnnotation(f.Sigma).Note);
            Assert.Equal(["mine"], f.Library.GetAnnotation(f.Final).Tags); // the tile's own path (M4's cost: the shown tags, changed)
        });
    }
}
