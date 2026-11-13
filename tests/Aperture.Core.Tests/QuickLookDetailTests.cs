using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;
using Path = System.IO.Path;

namespace Aperture.Core.Tests;

/// <summary>
/// Quick view shows a photo decoded at up to <c>QuickLookDecodeEdge</c> pixels (1600; 32 here); zooming in past that
/// asks for the photo in full (Paul, 2026-11-09: zoom as Photos does). Once per photo, only for an image the decode
/// size cut down, and never over a photo the user has moved on to.
/// </summary>
public class QuickLookDetailTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // A library of these photos (newest first: quick view opens on the first), its view model decoding at 32 px, quick
    // view open on the first photo, its image shown.
    private static void WithQuickLook((int Width, int Height)[] photos, Action<MainViewModel> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            for (var i = 0; i < photos.Length; i++)
            {
                var path = Path.Combine(folder.Path, $"p{i}.jpg");
                TestImages.Write(path, photos[i].Width, photos[i].Height);
                File.SetLastWriteTimeUtc(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-i));
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { QuickLookDecodeEdge = 32 };
            try
            {
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == photos.Length && !vm.IsLoadingView, Wait));
                vm.OpenQuickLookCommand.Execute(null);
                Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait), "quick view showed nothing");
                body(vm);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    private static List<BitmapSource?> ImagesShown(MainViewModel vm)
    {
        var shown = new List<BitmapSource?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.QuickLookImage))
                shown.Add(vm.QuickLookImage);
        };
        return shown;
    }

    private static void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

    [Fact]
    public void TheDecodeSize_IsSixteenHundredPixels_ByDefault()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            var library = new LibraryService(data.Path);
            var vm = new MainViewModel(library, new ThumbnailService(library));
            try
            {
                Assert.Equal(1600, vm.QuickLookDecodeEdge);
                Assert.Equal(8192, vm.QuickLookDetailEdge);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void APhotoCutDownToTheDecodeSize_IsLoadedInFull_Once()
    {
        WithQuickLook([(64, 48)], vm =>
        {
            Assert.Equal(32, vm.QuickLookImage!.PixelWidth); // cut down to the decode size
            var shown = ImagesShown(vm);

            vm.LoadQuickLookDetail();
            vm.LoadQuickLookDetail(); // the zoom goes on; the photo is loaded once

            Assert.True(Sta.PumpUntil(() => shown.Count > 0, Wait), "the photo wasn't loaded in full");
            Settle();
            var full = Assert.Single(shown);
            Assert.Equal((64, 48), (full!.PixelWidth, full.PixelHeight));
        });
    }

    [Fact]
    public void APhotoLoadedInFull_IsCappedAtTheDetailSize()
    {
        WithQuickLook([(64, 48)], vm =>
        {
            vm.QuickLookDetailEdge = 48; // 8192 in Aperture

            vm.LoadQuickLookDetail();

            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth != 32, Wait), "the photo wasn't loaded in full");
            Assert.Equal((48, 36), (vm.QuickLookImage!.PixelWidth, vm.QuickLookImage.PixelHeight));
        });
    }

    [Fact]
    public void ComingBackToAPhoto_LoadsItInFullAgain()
    {
        WithQuickLook([(64, 48), (80, 40)], vm =>
        {
            vm.LoadQuickLookDetail();
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth == 64, Wait));

            vm.QuickLookNextCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth == 32 && vm.QuickLookImage.PixelHeight == 16, Wait));
            vm.QuickLookPrevCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth == 32 && vm.QuickLookImage.PixelHeight == 24, Wait));

            vm.LoadQuickLookDetail(); // shown cut down again, so zooming in loads it again

            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth == 64, Wait), "the photo wasn't loaded in full again");
        });
    }

    [Fact]
    public void APhotoSmallerThanTheDecodeSize_IsAllThereIs_AndIsntLoadedAgain()
    {
        WithQuickLook([(16, 12)], vm =>
        {
            Assert.Equal(16, vm.QuickLookImage!.PixelWidth);
            var shown = ImagesShown(vm);

            vm.LoadQuickLookDetail();
            Settle();

            Assert.Empty(shown);
        });
    }

    [Fact]
    public void AFullLoadFinishingAfterTheUserMovedOn_IsDropped()
    {
        WithQuickLook([(1200, 900), (80, 40)], vm =>
        {
            vm.LoadQuickLookDetail();
            var shown = ImagesShown(vm);
            vm.QuickLookNextCommand.Execute(null); // before the full load has come back

            // Both loads come back, in either order: the next photo's, and the first photo's full load (shown, or not).
            Assert.True(Sta.PumpUntil(() => shown.Any(image => image?.PixelHeight == 16), Wait), "the next photo wasn't shown");
            Settle();

            Assert.DoesNotContain(shown, image => image?.PixelWidth == 1200); // the first photo's full load, dropped
            Assert.Equal((32, 16), (vm.QuickLookImage!.PixelWidth, vm.QuickLookImage.PixelHeight));
        });
    }

    // --- Round Z1: the photo shown, not its place in the list (the view can be rebuilt while quick view is open) ---

    // A newer photo, a different shape (100 × 20), arrives in the folder and the view is rebuilt — as an index run, the
    // folder watcher or the launch's reconcile does — while quick view stays open.
    private static void ANewerPhotoArrives(MainViewModel vm, LibraryService library, string folder, int count)
    {
        var path = Path.Combine(folder, "n.jpg");
        TestImages.Write(path, 100, 20);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        library.IndexRoot(library.GetRoots().Single());
        vm.RefreshView();
        // The view itself, not only the rows: the rows are read first and the view is built after them (REL-3).
        Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == count && !vm.IsLoadingView && Shows(vm, "n.jpg"), Wait),
            "the view wasn't rebuilt");
        Settle();
    }

    private static bool Shows(MainViewModel vm, string fileName) =>
        vm.ItemsView?.Cast<object>().OfType<TileVm>().Any(tile => tile.FileName == fileName) == true;

    private static void WithLibrary(int photos, Action<MainViewModel, LibraryService, string> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var folder = new TempDir();
            for (var i = 0; i < photos; i++)
            {
                var path = Path.Combine(folder.Path, $"p{i}.jpg");
                TestImages.Write(path, 64, 48);
                File.SetLastWriteTimeUtc(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(-i));
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(folder.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { QuickLookDecodeEdge = 32 };
            try
            {
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == photos && !vm.IsLoadingView, Wait));
                body(vm, library, folder.Path);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void AZoomAfterTheViewIsRebuilt_LoadsThePhotoShownInFull()
    {
        // The reviewer's probe: the full load took the photo at quick view's old place in the rebuilt list — the newcomer
        // — and showed it under the shown photo's caption.
        WithLibrary(2, (vm, library, folder) =>
        {
            vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait));
            var caption = vm.QuickLookCaption;
            ANewerPhotoArrives(vm, library, folder, 3);

            vm.LoadQuickLookDetail();

            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth != 32, Wait), "nothing was loaded in full");
            Settle();
            Assert.Equal((64, 48), (vm.QuickLookImage!.PixelWidth, vm.QuickLookImage.PixelHeight)); // p0, not the newcomer
            Assert.Equal(caption, vm.QuickLookCaption);
        });
    }

    [Fact]
    public void AFullLoadFromBeforeQuickViewClosed_IsDropped_EvenAtTheSamePlace()
    {
        // The reviewer's probe, made certain: p0's full load is held until quick view has been closed, the view rebuilt,
        // quick view opened again at the same place (now the newcomer) and the newcomer loaded in full.
        WithLibrary(1, (vm, library, folder) =>
        {
            using var hold = new ManualResetEventSlim(false);
            var decode = vm.DecodeQuickLook;
            var (held, returned) = (0, 0);
            vm.DecodeQuickLook = (path, edge) =>
            {
                if (edge != vm.QuickLookDetailEdge || Interlocked.Increment(ref held) != 1)
                    return decode(path, edge);
                hold.Wait(Wait); // the first full load (p0's)
                var image = decode(path, edge);
                Interlocked.Increment(ref returned);
                return image;
            };
            vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait));
            vm.LoadQuickLookDetail(); // p0 in full, held
            vm.CloseQuickLookCommand.Execute(null);
            ANewerPhotoArrives(vm, library, folder, 2);
            vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait));
            Assert.Equal(32, vm.QuickLookImage!.PixelWidth);
            Assert.InRange(vm.QuickLookImage.PixelHeight, 6, 7); // the newcomer cut down, not p0 (32 × 24)
            vm.LoadQuickLookDetail();
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelWidth == 100, Wait), "the newcomer wasn't loaded in full");
            var shown = ImagesShown(vm);

            hold.Set();
            Assert.True(Sta.PumpUntil(() => Volatile.Read(ref returned) == 1, Wait), "p0's full load didn't come back");
            Settle();

            Assert.Empty(shown); // p0's full load, dropped
            Assert.Equal((100, 20), (vm.QuickLookImage!.PixelWidth, vm.QuickLookImage.PixelHeight));
        });
    }

    [Fact]
    public void ACutDownLoadFinishingAfterTheUserMovedOn_IsDropped()
    {
        // Round Z2: paging on quickly, an earlier photo's slower load mustn't land under a later photo's caption.
        WithLibrary(2, (vm, _, _) =>
        {
            using var hold = new ManualResetEventSlim(false);
            var decode = vm.DecodeQuickLook;
            var (held, returned) = (0, 0);
            vm.DecodeQuickLook = (path, edge) =>
            {
                if (Path.GetFileName(path) != "p0.jpg" || Interlocked.Increment(ref held) != 1)
                    return decode(path, edge);
                hold.Wait(Wait); // p0, cut down
                var image = decode(path, edge);
                Interlocked.Increment(ref returned);
                return image;
            };
            vm.OpenQuickLookCommand.Execute(null); // p0, held
            vm.QuickLookNextCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait), "p1 wasn't shown");
            Assert.StartsWith("p1.jpg", vm.QuickLookCaption);
            var p1 = vm.QuickLookImage;
            var shown = ImagesShown(vm);

            hold.Set();
            Assert.True(Sta.PumpUntil(() => Volatile.Read(ref returned) == 1, Wait), "p0's load didn't come back");
            Settle();

            Assert.Empty(shown); // p0's load, dropped
            Assert.Same(p1, vm.QuickLookImage);
        });
    }

    [Fact]
    public void AFailedFullLoad_LeavesThePhotoShown()
    {
        // The file gone, or its share offline, by the time the user zooms: the photo stays as shown (round Z1).
        WithLibrary(1, (vm, _, _) =>
        {
            var decode = vm.DecodeQuickLook;
            var asked = 0;
            vm.DecodeQuickLook = (path, edge) =>
            {
                if (edge != vm.QuickLookDetailEdge)
                    return decode(path, edge);
                Interlocked.Increment(ref asked);
                return null;
            };
            vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait));
            var image = vm.QuickLookImage;
            var shown = ImagesShown(vm);

            vm.LoadQuickLookDetail();

            Assert.True(Sta.PumpUntil(() => Volatile.Read(ref asked) == 1, Wait), "no full load was asked for");
            Settle();
            Assert.Empty(shown);
            Assert.Same(image, vm.QuickLookImage);
        });
    }

    [Fact]
    public void ALoadFinishingAfterQuickViewClosed_IsDropped()
    {
        // Closing ends the visit: neither a photo still loading nor its full load shows once quick view is closed.
        WithLibrary(1, (vm, _, _) =>
        {
            vm.OpenQuickLookCommand.Execute(null);
            vm.CloseQuickLookCommand.Execute(null); // before the photo has loaded
            Settle();
            Assert.Null(vm.QuickLookImage);

            vm.OpenQuickLookCommand.Execute(null);
            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage is not null, Wait));
            vm.LoadQuickLookDetail();
            vm.CloseQuickLookCommand.Execute(null); // before the full load has come back
            Settle();
            Assert.Null(vm.QuickLookImage);
        });
    }

    [Fact]
    public void AtTheLastPhoto_NextLeavesItAsItIs()
    {
        WithQuickLook([(64, 48)], vm =>
        {
            var image = vm.QuickLookImage;
            var shown = ImagesShown(vm);

            vm.QuickLookNextCommand.Execute(null);
            vm.QuickLookPrevCommand.Execute(null);
            Settle();

            Assert.Empty(shown); // not loaded again (a zoomed photo stays zoomed)
            Assert.Same(image, vm.QuickLookImage);
        });
    }
}
