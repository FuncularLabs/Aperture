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
            var requested = vm.BuildGeneration;

            vm.SetGridWidth(Across(vm, 12));

            Assert.Equal(12, vm.TilesPerRow);
            Assert.Equal(requested + 1, vm.BuildGeneration); // counted as it's asked for (what the "no rebuild" tests rely on)
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

    // --- Lens B's probes (round 24): only the user's toggles go from one place to another ---

    private static readonly DateTime In2020 = new(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime In2019 = new(2019, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // 70 photos in 2020 and 5 in 2019 (collapsed by default: past the newest ~2 screens); 3 in 2019 (open: its newest).
    private static readonly DateTime[] OlderYearBelow = [.. Enumerable.Repeat(In2020, 70).Concat(Enumerable.Repeat(In2019, 5))];
    private static readonly DateTime[] OnlyThatYear = [.. Enumerable.Repeat(In2019, 3)];

    private static void Photos(string folder, IEnumerable<DateTime> dates, int start = 0)
    {
        var i = start;
        foreach (var date in dates)
        {
            var path = Path.Combine(folder, $"p{i:000}.jpg");
            TestImages.Write(path, 32, 24);
            File.SetLastWriteTimeUtc(path, date.AddMinutes(i++));
        }
    }

    // A library of these folders (by name), indexed, and a view model over it; the body can go to a folder and add photos.
    private static void WithFolders((string Name, DateTime[] Dates)[] folders, Action<MainViewModel, Action<string>, Func<string, string>> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            var dirs = folders.ToDictionary(f => f.Name, _ => new TempDir());
            try
            {
                var library = new LibraryService(data.Path);
                foreach (var (name, dates) in folders)
                {
                    Photos(dirs[name].Path, dates);
                    library.IndexRoot(library.AddRoot(dirs[name].Path, name));
                }
                var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.Zero };
                try
                {
                    Assert.True(Sta.PumpUntil(() => vm.FolderTree.Count == folders.Length && Sections(vm).Count > 0 && !vm.IsLoadingView, Wait));
                    // Goes to the folder and waits for its view: two folders can show the same sections.
                    void GoTo(string name)
                    {
                        var applied = 0;
                        void Count() => applied++;
                        vm.ViewApplied += Count;
                        vm.NavigateToNode(vm.FolderTree.Single(n => n.Name == name));
                        Assert.True(Sta.PumpUntil(() => applied > 0 && !vm.IsLoadingView, Wait), $"{name}'s view never came");
                        vm.ViewApplied -= Count;
                    }
                    body(vm, GoTo, name => dirs[name].Path);
                }
                finally
                {
                    vm.PrepareForClose();
                    library.ShutdownAsync().GetAwaiter().GetResult();
                }
            }
            finally
            {
                foreach (var dir in dirs.Values)
                    dir.Dispose();
            }
        });
    }

    [Fact]
    public void ASectionCollapsedOnlyByDefault_IsntCarriedStraightToAnotherFolder()
    {
        WithFolders([("A", OlderYearBelow), ("C", OnlyThatYear)], (vm, goTo, _) =>
        {
            goTo("A");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default
            Assert.False(Section(vm, "2019").Toggled);

            goTo("C");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.True(Section(vm, "2019").IsExpanded, "A's default-collapsed 2019 was carried to C");
        });
    }

    [Fact]
    public void AStateCarriedToAnotherFolder_DoesntBecomeAToggle()
    {
        // A → C (by year) → B (by month) → D (only 2019): D's 2019, its newest, opens.
        WithFolders([("A", OlderYearBelow), ("C", OnlyThatYear), ("B", MonthlyIn2020), ("D", OnlyThatYear)], (vm, goTo, _) =>
        {
            goTo("A");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            goTo("C");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));
            goTo("B");
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12, Wait), string.Join(", ", Sections(vm)));
            goTo("D");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.True(Section(vm, "2019").IsExpanded, "D's 2019 (its newest) collapsed by a state the user never set");
        });
    }

    [Fact]
    public void ASectionKeptOpenByARebuild_IsntRememberedAsAToggle()
    {
        // X: 2019 open by default; 20 photos arrive in 2020 and the rebuild keeps it open (not the user). Then B (by month),
        // then D (by year, 2019 past the newest ~2 screens): D's 2019 is collapsed, as its default.
        WithFolders([("X", [.. Enumerable.Repeat(In2020, 50).Concat(Enumerable.Repeat(In2019, 5))]), ("B", MonthlyIn2020), ("D", OlderYearBelow)],
            (vm, goTo, dir) =>
            {
                goTo("X");
                Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
                Assert.True(Section(vm, "2019").IsExpanded);
                Photos(dir("X"), Enumerable.Repeat(In2020, 20), start: 100);
                vm.StartBackgroundRefresh();
                Assert.True(Sta.PumpUntil(() => Section(vm, "2020").Count == 70 && !vm.IsIndexing && !vm.IsLoadingView, TimeSpan.FromSeconds(30)));
                Assert.True(Section(vm, "2019").IsExpanded); // kept open…
                Assert.False(Section(vm, "2019").Toggled);   // …but not by the user

                goTo("B");
                Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12, Wait), string.Join(", ", Sections(vm)));
                goTo("D");
                Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));

                Assert.False(Section(vm, "2019").IsExpanded, "D's 2019 opened by a state no user set");
            });
    }

    // --- Lens B's probes (round 25): what is "the view as it is", and a toggle going on ---

    [Fact]
    public void ASearchRefinedToAnother_GetsOnlyTheUsersToggles()
    {
        // Every search had the same place; a search refined to one showing only 2019 opened it collapsed by default.
        WithFolders([("A", OlderYearBelow)], (vm, goTo, _) =>
        {
            goTo("A");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            var applied = 0;
            vm.ViewApplied += () => applied++;
            vm.SearchInput = "p0"; // p000–p074: both years
            vm.CommitSearch();
            Assert.True(Sta.PumpUntil(() => applied > 0 && !vm.IsLoadingView && Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default

            applied = 0;
            vm.SearchInput = "p07"; // p070–p074: 2019 only
            vm.CommitSearch();
            Assert.True(Sta.PumpUntil(() => applied > 0 && !vm.IsLoadingView && Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.True(Section(vm, "2019").IsExpanded, "the first search's default-collapsed 2019 was carried to the second");
        });
    }

    [Fact]
    public void ThePicturesAndVideosFilter_GetsOnlyTheUsersToggles()
    {
        // 70 photos in 2020, 5 videos in 2019 (collapsed by default); videos only: 2019, now the newest, opens.
        WithFolders([("A", [.. Enumerable.Repeat(In2020, 70)])], (vm, goTo, dir) =>
        {
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(dir("A"), $"v{i}.mp4");
                File.WriteAllText(path, "x"); // indexed by its extension
                File.SetLastWriteTimeUtc(path, In2019.AddMinutes(i));
            }
            vm.StartBackgroundRefresh();
            Assert.True(Sta.PumpUntil(() => Sections(vm).Contains("2019") && !vm.IsIndexing && !vm.IsLoadingView, TimeSpan.FromSeconds(30)), string.Join(", ", Sections(vm)));
            goTo("A");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default
            var applied = 0;
            vm.ViewApplied += () => applied++;

            vm.ShowImages = false;
            Assert.True(Sta.PumpUntil(() => applied > 0 && !vm.IsLoadingView && Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.True(Section(vm, "2019").IsExpanded, "the default-collapsed 2019 was carried into the videos-only view");
        });
    }

    [Fact]
    public void AToggle_OutlastsARebuildInItsPlace_AndGoesToAnotherFolder()
    {
        WithFolders([("X", OnlyThatYear), ("C", OnlyThatYear)], (vm, goTo, dir) =>
        {
            goTo("X");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));
            Section(vm, "2019").IsExpanded = false; // the user's toggle
            Photos(dir("X"), Enumerable.Repeat(In2019, 2), start: 100);
            vm.StartBackgroundRefresh();
            Assert.True(Sta.PumpUntil(() => Section(vm, "2019").Count == 5 && !vm.IsIndexing && !vm.IsLoadingView, TimeSpan.FromSeconds(30)));
            Assert.True(Section(vm, "2019").Toggled); // still the user's, after the rebuild

            goTo("C");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.False(Section(vm, "2019").IsExpanded, "the user's toggle was lost by a rebuild in its own place");
        });
    }

    [Fact]
    public void AToggle_GoesOnThroughAFolderItWasCarriedTo()
    {
        WithFolders([("A", OnlyThatYear), ("C", OnlyThatYear), ("D", OnlyThatYear)], (vm, goTo, _) =>
        {
            goTo("A");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));
            Section(vm, "2019").IsExpanded = false; // the user's toggle
            goTo("C");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded);

            goTo("D");
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.False(Section(vm, "2019").IsExpanded, "the user's toggle stopped at the first folder it was carried to");
        });
    }

    // --- Round 26: the folders' checkboxes change what's shown too ---

    [Fact]
    public void UntickingAFolder_GetsOnlyTheUsersToggles()
    {
        // The reviewer's probe: Everything over N (70 photos, 2020) and O (5, 2019, collapsed by default); untick N and
        // 2019, now the newest, opens.
        WithFolders([("N", [.. Enumerable.Repeat(In2020, 70)]), ("O", [.. Enumerable.Repeat(In2019, 5)])], (vm, _, _) =>
        {
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2020", "2019"]), Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default
            var applied = 0;
            vm.ViewApplied += () => applied++;

            vm.Roots.Single(r => r.Alias == "N").IsIncluded = false;
            Assert.True(Sta.PumpUntil(() => applied > 0 && !vm.IsLoadingView && Sections(vm).SequenceEqual(["2019"]), Wait), string.Join(", ", Sections(vm)));

            Assert.True(Section(vm, "2019").IsExpanded, "the default-collapsed 2019 was carried into Everything without N");
        });
    }

    [Fact]
    public void EveryChangeTheUserMakesToWhatsShown_IsAnotherView()
    {
        // What the carry compares: each of these is another view (photos arriving, or a sort change, isn't).
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            using var more = new TempDir();
            Photos(photos.Path, [In2020]);
            Photos(Path.Combine(photos.Path, "Trip"), [In2019]);
            Photos(more.Path, [In2019]);
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            try
            {
                Assert.True(Sta.PumpUntil(() => Sections(vm).Count > 0 && vm.FolderTree.Count == 1, Wait));
                var root = vm.Roots.Single();
                var node = vm.FolderTree.Single();
                node.IsExpanded = true; // its subfolders load
                var trip = node.Children.Single(c => c.Name == "Trip");

                void IsAnotherView(string what, Action change)
                {
                    var before = vm.ViewKey;
                    change();
                    Assert.True(before != vm.ViewKey, $"{what} kept the view");
                }
                var kept = vm.ViewKey;
                vm.SortPreset = vm.SortLabels.First(label => label != vm.SortPreset); // a sort change keeps the view
                Assert.Equal(kept, vm.ViewKey);

                IsAnotherView("hiding videos", () => vm.ShowVideos = false);
                IsAnotherView("hiding pictures", () => vm.ShowImages = false);
                IsAnotherView("hiding a subfolder", () => trip.IsShown = false);
                IsAnotherView("leaving subfolders out", () => root.IsRecursive = false);
                IsAnotherView("not indexing videos", () => vm.IncludeVideos = false);
                IsAnotherView("adding a folder (which goes to it)", () => vm.AddFolder(more.Path, includeSubfolders: true));
                vm.NavigateHomeCommand.Execute(null); // Everything: the place stays as folders are unticked or removed
                IsAnotherView("unticking a folder", () => root.IsIncluded = false);
                IsAnotherView("removing a folder that isn't the one shown", () => vm.RemoveRootCommand.Execute(vm.Roots.Single(r => r.Path == more.Path)));
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    // --- Round 27: a change carried out by re-indexing is another view where it lands ---

    private static readonly DateTime In2021 = new(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime In2018 = new(2018, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static void Videos(string folder, IEnumerable<DateTime> dates)
    {
        Directory.CreateDirectory(folder);
        var i = 0;
        foreach (var date in dates)
        {
            var path = Path.Combine(folder, $"v{i:000}.mp4");
            File.WriteAllText(path, "x"); // indexed by its extension
            File.SetLastWriteTimeUtc(path, date.AddMinutes(i++));
        }
    }

    // A library of the folders the setup writes (indexed as they stand), a view model over it, and the body.
    private static void WithLibraryOf(Action<string> setup, string[] folders, Action<MainViewModel, LibraryService> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var outer = new TempDir();
            setup(outer.Path);
            var library = new LibraryService(data.Path);
            foreach (var name in folders)
                library.IndexRoot(library.AddRoot(Path.Combine(outer.Path, name), name));
            var vm = new MainViewModel(library, new ThumbnailService(library)) { StartupReconcileDelay = TimeSpan.FromHours(1) };
            try
            {
                body(vm, library);
            }
            finally
            {
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void NotIndexingVideos_InTwoFolders_IsAnotherViewWhereEachLands()
    {
        // The reviewer's probe. A: 5 photos (2019), 70 videos (2021); B: 70 videos (2020), and 400 photos not yet indexed
        // in a subfolder the user hid, so B's re-index ends well after A's. Without videos, Everything is 2019 alone: its
        // newest, open — not left collapsed by default by B's rebuild, the second.
        var outerPath = "";
        WithLibraryOf(outer =>
        {
            outerPath = outer;
            Photos(Path.Combine(outer, "A"), Enumerable.Repeat(In2019, 5));
            Videos(Path.Combine(outer, "A"), Enumerable.Repeat(In2021, 70));
            Videos(Path.Combine(outer, "B"), Enumerable.Repeat(In2020, 70));
        }, ["A", "B"], (vm, library) =>
        {
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2021", "2020", "2019"]) && !vm.IsLoadingView, Wait), string.Join(", ", Sections(vm)));
            Assert.False(Section(vm, "2019").IsExpanded); // collapsed by default
            var b = library.GetRoots().Single(r => r.Alias == "B");
            Photos(Path.Combine(outerPath, "B", "pad"), Enumerable.Repeat(In2018, 400));
            library.SetFolderShown(b.Id, "pad", false);

            vm.IncludeVideos = false;
            Assert.True(Sta.PumpUntil(() => Sections(vm).SequenceEqual(["2019"]) && !vm.IsIndexing && !vm.IsLoadingView, TimeSpan.FromSeconds(60)), string.Join(", ", Sections(vm)));
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

            Assert.True(Section(vm, "2019").IsExpanded, "not indexing videos left Everything's only section collapsed by default");
        });
    }

    [Fact]
    public void AReindexCarryingOutAChangeToWhatsShown_CountsAgainWhereItLands()
    {
        // Each re-index that carries out such a change ends with a rebuild that must be another view, whatever rebuilds
        // came between the change and its end: the count is taken again as each re-index lands.
        WithLibraryOf(outer =>
        {
            Photos(Path.Combine(outer, "A"), Enumerable.Repeat(In2019, 5));
            Photos(Path.Combine(outer, "A", "Trip"), Enumerable.Repeat(In2020, 3));
            Videos(Path.Combine(outer, "A"), Enumerable.Repeat(In2021, 2));
        }, ["A"], (vm, _) =>
        {
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count > 0 && !vm.IsLoadingView, Wait), string.Join(", ", Sections(vm)));

            void CountsAgainWhereItLands(string what, Action change)
            {
                change();
                var asMade = vm.ViewKey;
                Assert.True(Sta.PumpUntil(() => vm.ViewKey != asMade && !vm.IsIndexing, TimeSpan.FromSeconds(30)), $"{what}: not counted as its re-index landed");
            }
            CountsAgainWhereItLands("leaving subfolders out", () => vm.Roots.Single().IsRecursive = false);
            CountsAgainWhereItLands("not indexing videos", () => vm.IncludeVideos = false);
        });
    }
}
