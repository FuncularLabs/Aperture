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

            Assert.True(Sta.PumpUntil(() => vm.QuickLookImage?.PixelHeight == 16, Wait), "the next photo wasn't shown");
            Settle();

            Assert.DoesNotContain(shown, image => image?.PixelWidth == 1200); // the first photo's full load, dropped
            Assert.Equal((32, 16), (vm.QuickLookImage!.PixelWidth, vm.QuickLookImage.PixelHeight));
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
