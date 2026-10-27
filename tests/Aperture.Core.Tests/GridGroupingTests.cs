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
            var requested = vm.BuildGeneration;

            vm.SetGridWidth(Across(vm, 5)); // five across: still one year

            Assert.Equal(5, vm.TilesPerRow);
            Assert.Equal(requested, vm.BuildGeneration); // no rebuild asked for (a request counts at once, before its debounce)
            Assert.Equal(["2020"], Sections(vm));
        });
    }

    [Fact]
    public void AZoomThatKeepsTheGrouping_LeavesTheViewAlone()
    {
        WithLibrary(MonthlyIn2020, vm =>
        {
            vm.SetGridWidth(Across(vm, 12));
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020"]), Wait));
            var (across, requested) = (vm.TilesPerRow, vm.BuildGeneration);

            vm.ZoomOut(); // more across: still one year

            Assert.True(vm.TilesPerRow > across, "the zoom was already at its smallest");
            Assert.Equal(requested, vm.BuildGeneration);
        });
    }

    [Fact]
    public void AViewWithoutDateSections_IgnoresTheWidth()
    {
        WithLibrary(MonthlyIn2020, vm =>
        {
            var applied = 0;
            vm.ViewApplied += () => applied++;
            vm.GroupByDate = false; // grouped by month (one across) until now
            Assert.True(Sta.PumpUntil(() => applied > 0, Wait));
            var requested = vm.BuildGeneration;

            vm.SetGridWidth(Across(vm, 12)); // would group by year, were the view grouped

            Assert.Equal(requested, vm.BuildGeneration);
        });
    }

    [Fact]
    public void ASectionsToggle_OutlastsTurningSectionsOffAndOn()
    {
        WithLibrary(FourAYear, vm =>
        {
            vm.SetGridWidth(Across(vm, 12));
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2024", "2023", "2022"]), Wait), string.Join(", ", Sections(vm)));
            Section(vm, "2023").IsExpanded = false;
            var applied = 0;
            vm.ViewApplied += () => applied++;

            vm.GroupByDate = false;
            Assert.True(Sta.PumpUntil(() => applied == 1, Wait));
            vm.GroupByDate = true;
            Assert.True(Sta.PumpUntil(() => applied == 2, Wait));

            Assert.Equal(["2024", "2023", "2022"], Sections(vm));
            Assert.False(Section(vm, "2023").IsExpanded);
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

    [Fact]
    public void ASectionLeftOpen_StaysOpenAsPhotosArriveAboveIt()
    {
        // A rebuild in the view's own grouping keeps every section as it is (round 23): 2019, open by default while 2020
        // holds 50 photos, stays open when 20 more arrive in 2020 and push it past the newest ~2 screens.
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            void Photo(int i, DateTime date)
            {
                var path = Path.Combine(photos.Path, $"p{i:000}.jpg");
                TestImages.Write(path, 32, 24);
                File.SetLastWriteTimeUtc(path, date.AddMinutes(i));
            }
            var in2020 = new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            var in2019 = new DateTime(2019, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 55; i++)
                Photo(i, i < 50 ? in2020 : in2019);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.Zero };
            try
            {
                Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
                Assert.True(Section(vm, "2019").IsExpanded); // 50 photos above it: within the newest ~2 screens

                for (var i = 55; i < 75; i++)
                    Photo(i, in2020);
                vm.StartBackgroundRefresh(); // finds the 20 new photos
                Assert.True(Sta.PumpUntil(() => Section(vm, "2020").Count == 70 && !vm.IsIndexing && !vm.IsLoadingView, TimeSpan.FromSeconds(30)));

                Assert.True(Section(vm, "2019").IsExpanded);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void ASectionCollapsedOnlyByDefault_IsntCarriedToAnotherFolder()
    {
        // Lens A (round 23). Folder A, by year: 2020's 70 photos open, 2019's 5 collapsed only because they come after the
        // newest ~2 screens. Folder B, by month. Folder C, only 2019: its one section opens, as a folder's newest does —
        // A's 2019 wasn't the user's toggle, so it isn't remembered for years.
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var a = new TempDir();
            using var b = new TempDir();
            using var c = new TempDir();
            void Photos(TempDir folder, IEnumerable<DateTime> dates)
            {
                var i = 0;
                foreach (var date in dates)
                {
                    var path = Path.Combine(folder.Path, $"p{i:000}.jpg");
                    TestImages.Write(path, 32, 24);
                    File.SetLastWriteTimeUtc(path, date.AddMinutes(i++));
                }
            }
            var in2020 = new DateTime(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            var in2019 = new DateTime(2019, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            Photos(a, Enumerable.Repeat(in2020, 70).Concat(Enumerable.Repeat(in2019, 5)));
            Photos(b, MonthlyIn2020);
            Photos(c, Enumerable.Repeat(in2019, 3));
            var library = new LibraryService(data.Path);
            foreach (var (folder, name) in new[] { (a, "A"), (b, "B"), (c, "C") })
                library.IndexRoot(library.AddRoot(folder.Path, name));
            var vm = new MainViewModel(library, new ThumbnailService(library));
            FolderNodeVm Node(string name) => vm.FolderTree.Single(n => n.Name == name);
            try
            {
                Assert.True(Sta.PumpUntil(() => vm.FolderTree.Count == 3 && Sections(vm).Count > 0, Wait));

                vm.NavigateToNode(Node("A"));
                Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
                Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default
                vm.NavigateToNode(Node("B"));
                Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12, Wait), string.Join(", ", Sections(vm)));
                vm.NavigateToNode(Node("C"));
                Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

                Assert.True(Section(vm, "2019").IsExpanded);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }
}
