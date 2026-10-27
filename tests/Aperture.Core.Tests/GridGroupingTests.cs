using System.Windows.Threading;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The date grouping follows the grid's width and zoom (Paul's second trial, 2026-10-27) — regrouping only when the
/// grouping changes, and keeping the user's section toggles across a regroup (round 22 of the review). A real view model
/// on a real library, its dispatcher pumped on an STA thread.
/// </summary>
public class GridGroupingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // One photo a month through 2020: twelve months while their rows are at least half full (one or two across), one year
    // from three across.
    private static readonly DateTime[] MonthlyIn2020 =
        [.. Enumerable.Range(1, 12).Select(month => new DateTime(2020, month, 15, 12, 0, 0, DateTimeKind.Utc))];

    // One photo in each of January, April, July and October of 2022, 2023 and 2024: twelve months one across (every
    // grouping fills its rows, and months come nearest twelve), three years twelve across (none fills half its rows; years
    // fill them best).
    private static readonly DateTime[] FourAYear =
        [.. new[] { 2024, 2023, 2022 }.SelectMany(year => new[] { 1, 4, 7, 10 }.Select(month => new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc)))];

    private static List<string> Sections(MainViewModel vm) =>
        vm.ItemsView?.Cast<object>().OfType<TileVm>().Select(t => t.Section?.Label ?? "").Distinct().ToList() ?? [];

    private static SectionVm Section(MainViewModel vm, string label) =>
        vm.ItemsView!.Cast<object>().OfType<TileVm>().Select(t => t.Section).First(s => s?.Label == label)!;

    // The width that holds this many tiles across at the current zoom (each tile's slot is its size plus 20 px).
    private static double Across(MainViewModel vm, int tiles) => tiles * (vm.TileSize + 20);

    // A library of one folder whose photos were taken on these dates, indexed, and a view model over it.
    private static void WithLibrary(DateTime[] dates, Action<MainViewModel> body, Action<MainViewModel>? beforeTheFirstView = null)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            for (var i = 0; i < dates.Length; i++)
            {
                var path = Path.Combine(photos.Path, $"p{i:00}.jpg");
                TestImages.Write(path, 32, 24);
                File.SetLastWriteTimeUtc(path, dates[i]); // no EXIF date: the file's is the photo's
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library));
            try
            {
                beforeTheFirstView?.Invoke(vm);
                Assert.True(Sta.PumpUntil(() => Sections(vm).Count > 0, Wait), "the first view never came");
                body(vm);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void TheGridsWidth_ChoosesTheGrouping()
    {
        WithLibrary(MonthlyIn2020, vm =>
        {
            Assert.Equal(1, vm.TilesPerRow); // no width reported yet
            Assert.Equal(12, Sections(vm).Count);

            vm.SetGridWidth(Across(vm, 12));

            Assert.Equal(12, vm.TilesPerRow);
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020"]), Wait), string.Join(", ", Sections(vm)));

            vm.SetGridWidth(Across(vm, 2) + 1);

            Assert.Equal(2, vm.TilesPerRow);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12, Wait), string.Join(", ", Sections(vm)));
        });
    }

    [Fact]
    public void AWidthThatKeepsTheGrouping_LeavesTheViewAlone()
    {
        WithLibrary(MonthlyIn2020, vm =>
        {
            vm.SetGridWidth(Across(vm, 12));
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020"]), Wait));
            var rebuilt = 0;
            vm.ViewApplied += () => rebuilt++;

            vm.SetGridWidth(Across(vm, 5)); // five across: still one year

            Assert.Equal(5, vm.TilesPerRow);
            Sta.PumpUntil(() => rebuilt > 0, TimeSpan.FromSeconds(1)); // well past the rebuild's debounce
            Assert.Equal(0, rebuilt);
            Assert.Equal(["2020"], Sections(vm));
        });
    }

    [Fact]
    public void ZoomingIntoAnotherGrouping_Regroups()
    {
        WithLibrary(MonthlyIn2020, vm =>
        {
            vm.SetGridWidth(Across(vm, 3) - 1); // two across at this zoom: months, half full
            Assert.Equal(2, vm.TilesPerRow);
            Assert.Equal(12, Sections(vm).Count);
            var size = vm.TileSize;

            vm.ZoomOut();

            Assert.True(vm.TileSize < size, "the zoom was already at its smallest");
            Assert.True(vm.TilesPerRow >= 3);
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020"]), Wait), string.Join(", ", Sections(vm)));
        });
    }

    [Fact]
    public void AViewBuiltForAnOlderWidth_RegroupsAsItIsApplied()
    {
        var first = true;
        WithLibrary(MonthlyIn2020,
            vm => Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020"]), Wait), string.Join(", ", Sections(vm))),
            beforeTheFirstView: vm => vm.ViewApplying += () =>
            {
                if (!first)
                    return;
                first = false;
                vm.SetGridWidth(Across(vm, 12)); // the width arrives while the startup view, built one across, is applied
            });
    }

    [Fact]
    public void ASectionsToggle_OutlastsARegroupAndBack()
    {
        WithLibrary(FourAYear, vm =>
        {
            vm.SetGridWidth(Across(vm, 12));
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2024", "2023", "2022"]), Wait), string.Join(", ", Sections(vm)));
            Section(vm, "2023").IsExpanded = false; // the user collapses 2023

            vm.SetGridWidth(Across(vm, 1)); // a narrow window: months
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12, Wait), string.Join(", ", Sections(vm)));
            vm.SetGridWidth(Across(vm, 12)); // wide again: years
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 3, Wait), string.Join(", ", Sections(vm)));

            Assert.False(Section(vm, "2023").IsExpanded);
            Assert.True(Section(vm, "2024").IsExpanded);
        });
    }
}
