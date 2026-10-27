using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aperture.App;
using Aperture.App.Services;
using Aperture.App.ViewModels;
using Aperture.Core.Library;
using Aperture.Core.Tests.Support;
using Path = System.IO.Path;
using Shape = System.Windows.Shapes.Shape;
using ShapePath = System.Windows.Shapes.Path;

namespace Aperture.Core.Tests;

/// <summary>
/// The real <see cref="MainWindow"/>, shown (off screen), for what only a shown window does: its bindings resolve against a
/// real view model, its list of libraries is laid out, and its animations run (round 22 of the multiple-libraries review).
/// A binding the window can't make (two-way onto a read-only property) fails here as it would at launch — if it is made in
/// this scenario: a library with a folder, photos and date sections, and a second library in the list. Elements made only
/// in other states (the first-run panel, context menus, the preview pane's details) aren't reached.
/// </summary>
public class MainWindowShownTests
{
    private sealed class NoDialogs : ICatalogDialogs
    {
        public string? PromptText(string title, string label, string initial) => null;
        public string? PickFolder(string title, string initialDirectory) => null;
        public bool? AskIncludeSubfolders(string folder) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowMessage(string title, string message, bool isError) { }
    }

    private static T OffScreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    // A storyboard is driving it: the arc is turning.
    private static bool Turning(Shape spinner) => spinner.RenderTransform is Animatable { HasAnimatedProperties: true };

    [Fact]
    public void TheWindow_OpensOnALibrary_ShowingEachLibrarysCount()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var outer = new TempDir();
            using var photos = new TempDir();
            foreach (var file in new[] { "a.jpg", "b.jpg", "c.jpg" })
                TestImages.Write(Path.Combine(photos.Path, file), 64, 48);
            var service = CatalogService.Open(outer.Combine("catalogs.json"), outer.Combine("default"));
            var library = service.Current;
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            service.RecordItemCount(7); // when it was last left — while it's open, the list shows its live count instead
            var workDir = outer.Combine("Work");
            using (new LibraryService(workDir)) { }
            service.Registry.SetItemCount(service.Registry.Add("Work", workDir).Id, 42);
            var bar = new CatalogBarViewModel(service, new NoDialogs(), outer.Combine("Aperture Libraries"), outer.Combine("Pictures"));
            var vm = new MainViewModel(library, new ThumbnailService(library), bar);
            var window = OffScreen(new MainWindow { Width = 1200, Height = 800, DataContext = vm });
            try
            {
                List<string> Counts() => [.. Descendants(window).OfType<TextBlock>()
                    .Where(t => t.DataContext is LibraryListItem && t.IsVisible && t.Text.TrimStart().StartsWith('('))
                    .Select(t => $"{((LibraryListItem)t.DataContext).Name} {t.Text.Trim()}")
                    .Order()];

                var everything = (TextBlock)window.FindName("EverythingCount");

                window.Show(); // a binding the window can't make with this view model throws here, or as it's laid out
                Assert.Equal(["Work (42)"], Counts()); // the open library's items aren't read yet (that waits for this thread): no "(0)"
                Assert.Equal("", everything.Text);     // nor beside Everything

                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == 3 && !vm.IsLoadingView, TimeSpan.FromSeconds(10)));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300)); // layout and bindings settle

                Assert.Equal(["Default (3)", "Work (42)"], Counts());
                Assert.Equal("(3)", everything.Text.Trim());

                var spinner = Assert.Single(Descendants((DependencyObject)window.FindName("IndexingCard")).OfType<ShapePath>());
                Assert.Same(window.FindResource("IndexingSpinner"), spinner.Style);
                Assert.False(vm.IsIndexing);
                Assert.False(Turning(spinner), "the indexing card's spinner turns while the card is hidden");
            }
            finally
            {
                window.Close();
                vm.PrepareForClose();
                service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    // --- The width the date grouping is chosen for (round 23) ---

    private static ScrollViewer GridScroll(Window window) =>
        Descendants((ListBox)window.FindName("ItemsList")).OfType<ScrollViewer>().First();

    private static bool ScrollbarShown(Window window) => GridScroll(window).ComputedVerticalScrollBarVisibility == Visibility.Visible;

    private static List<SectionVm> Sections(MainViewModel vm) =>
        vm.ItemsView?.Cast<object>().OfType<TileVm>().Select(t => t.Section).OfType<SectionVm>().Distinct().ToList() ?? [];

    // Resizes the window until the grid's viewport would be `viewport` wide without a scrollbar. It steers by the scroll
    // viewer's own width, which a scrollbar coming or going doesn't change; `frame` is what the viewer keeps beside its
    // viewport when no scrollbar shows.
    private static void SizeTheViewport(Window window, double viewport, double frame)
    {
        for (var i = 0; i < 8 && Math.Abs(GridScroll(window).ActualWidth - frame - viewport) > 0.5; i++)
        {
            window.Width += viewport + frame - GridScroll(window).ActualWidth;
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(250));
        }
        Assert.True(Math.Abs(GridScroll(window).ActualWidth - frame - viewport) <= 0.5, $"the viewport wouldn't size to {viewport}");
    }

    // A real window, shown off screen, over a library of one folder with photos taken on these dates (each with its own
    // time), its tiles at `tileSize`.
    private static void WithWindow(IEnumerable<DateTime> dates, double tileSize, Action<MainViewModel, MainWindow> body)
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using var data = new TempDir();
            using var photos = new TempDir();
            var i = 0;
            foreach (var date in dates)
            {
                var path = Path.Combine(photos.Path, $"p{i:000}.jpg");
                TestImages.Write(path, 32, 24);
                File.SetLastWriteTimeUtc(path, date.AddMinutes(i++));
            }
            var library = new LibraryService(data.Path);
            library.IndexRoot(library.AddRoot(photos.Path, "Photos"));
            library.Settings.Update(s => s.NavWidth = 140); // the folder pane at its narrowest: a CI desktop is 1024 wide
            var vm = new MainViewModel(library, new ThumbnailService(library));
            while (vm.TileSize > tileSize)
                vm.ZoomOut();
            var window = OffScreen(new MainWindow { Width = 900, Height = 760, DataContext = vm });
            try
            {
                window.Show();
                Assert.True(Sta.PumpUntil(() => Sections(vm).Count > 0 && !vm.IsLoadingView, TimeSpan.FromSeconds(10)));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));
                body(vm, window);
            }
            finally
            {
                window.Close();
                vm.PrepareForClose();
                library.ShutdownAsync().GetAwaiter().GetResult();
            }
        });
    }

    // Two photos on the 15th of each month from January to June, in 2023 and 2024: twelve months of 2 up to four across
    // (each fills half a row), two years of 12 from five.
    private static readonly DateTime[] HalfYears =
        [.. new[] { 2024, 2023 }.SelectMany(year => Enumerable.Range(1, 6).SelectMany(month => Enumerable.Repeat(new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc), 2)))];

    // The same, January to March only: six months of 2 up to four across, two years of 6 from five.
    private static readonly DateTime[] QuarterYears =
        [.. new[] { 2024, 2023 }.SelectMany(year => Enumerable.Range(1, 3).SelectMany(month => Enumerable.Repeat(new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc), 2)))];

    [Fact]
    public void TheGroupingWidth_AllowsForTheScrollbar_ShownOrNot()
    {
        WithWindow(HalfYears.Take(3), 96, (vm, window) =>
        {
            Assert.False(ScrollbarShown(window)); // three tiles
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth;
            var slot = vm.TileSize + 20;

            SizeTheViewport(window, 4 * slot + 5, frame); // four slots and a few pixels: fewer than a scrollbar takes

            Assert.False(ScrollbarShown(window));
            Assert.Equal(3, vm.TilesPerRow); // four fit, but the grouping allows for a scrollbar
        });
    }

    [Fact]
    public void TheGroupingWidth_IsTheViewport_WhileTheScrollbarShows()
    {
        WithWindow(HalfYears, 96, (vm, window) =>
        {
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            var slot = vm.TileSize + 20;
            SizeTheViewport(window, 4 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 12 && ScrollbarShown(window), TimeSpan.FromSeconds(10)), "twelve months, expanded, need a scrollbar");

            SizeTheViewport(window, 5 * slot + 5 + SystemParameters.VerticalScrollBarWidth, frame); // beside the scrollbar: five slots and a few pixels

            Assert.True(Sta.PumpUntil(() => vm.TilesPerRow == 5, TimeSpan.FromSeconds(5)), $"{vm.TilesPerRow} across");
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            Assert.True(ScrollbarShown(window)); // by year now, still taller than the window
            Assert.Equal(5, vm.TilesPerRow);
        });
    }

    [Fact]
    public void ARegroupThatBringsInTheScrollbar_Settles()
    {
        // Lens A's reproduction (round 23): by month, all but the newest collapsed, so no scrollbar; the viewport a few
        // pixels past five slots. Were the width to follow the scrollbar: five across → years (taller than the viewport: a
        // scrollbar) → four across → the collapsed months (shorter: none) → five across…, rebuilding for good.
        WithWindow(QuarterYears, 96, (vm, window) =>
        {
            var slot = vm.TileSize + 20;
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

            // How tall the collapsed months are, and the years.
            SizeTheViewport(window, 4 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 6, TimeSpan.FromSeconds(10)), $"{Sections(vm).Count} sections at {vm.TilesPerRow} across");
            foreach (var month in Sections(vm).Skip(1))
                month.IsExpanded = false;
            Settle();
            var months = GridScroll(window).ExtentHeight;
            SizeTheViewport(window, 5 * slot + 20, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 2, TimeSpan.FromSeconds(10)), $"{Sections(vm).Count} sections at {vm.TilesPerRow} across");
            Settle();
            var years = GridScroll(window).ExtentHeight;

            // A viewport between the two: the years need a scrollbar, the collapsed months don't.
            window.Height += (months + years) / 2 - GridScroll(window).ViewportHeight;
            SizeTheViewport(window, 4 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 6, TimeSpan.FromSeconds(10)));
            Settle();
            Assert.InRange(GridScroll(window).ViewportHeight, months + 1, years - 1);
            Assert.False(ScrollbarShown(window));
            var applied = 0;
            vm.ViewApplied += () => applied++;

            SizeTheViewport(window, 5 * slot + 5, frame);
            Sta.PumpUntil(() => false, TimeSpan.FromSeconds(1)); // any regroup the resize asked for is done
            var before = applied;
            Sta.PumpUntil(() => false, TimeSpan.FromSeconds(2));

            Assert.Equal(0, applied - before);
            Assert.Equal(4, vm.TilesPerRow);
            Assert.Equal(6, Sections(vm).Count);
        });
    }

    [Fact]
    public void AHeightChangeThatBringsInTheScrollbar_AsksForNoBuild()
    {
        // Lens B's probe (round 24): the grid's SizeChanged came before its layout, with the old viewport beside the new
        // scrollbar — one scrollbar's width off, so a rebuild was asked for in a grouping that hadn't changed.
        WithWindow(QuarterYears, 96, (vm, window) =>
        {
            var slot = vm.TileSize + 20;
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            SizeTheViewport(window, 4 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 6, TimeSpan.FromSeconds(10)));
            foreach (var month in Sections(vm).Skip(1))
                month.IsExpanded = false;
            Settle();
            window.Height += GridScroll(window).ExtentHeight + 60 - GridScroll(window).ViewportHeight; // room to spare
            Settle();
            SizeTheViewport(window, 5 * slot + 5, frame); // months, four across (five less a scrollbar)
            Settle();
            Assert.False(ScrollbarShown(window));
            Assert.Equal((4, 6), (vm.TilesPerRow, Sections(vm).Count));
            var (requested, applied) = (vm.BuildGeneration, 0);
            vm.ViewApplied += () => applied++;

            window.Height -= 120; // shorter than the months: the scrollbar comes in
            Settle();
            Sta.PumpUntil(() => false, TimeSpan.FromSeconds(1));

            Assert.True(ScrollbarShown(window));
            Assert.Equal(4, vm.TilesPerRow);
            Assert.True(requested == vm.BuildGeneration && applied == 0, $"builds asked for: {vm.BuildGeneration - requested}, applied: {applied}");
        });
    }

    [Fact]
    public void AHeightChangeThatTakesTheScrollbarAway_AsksForNoBuild()
    {
        // Lens B's probe (round 24), the other way: years, five across beside a scrollbar; the window grows past them.
        WithWindow(QuarterYears, 96, (vm, window) =>
        {
            var slot = vm.TileSize + 20;
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            void Settle() => Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));
            SizeTheViewport(window, 5 * slot + 25, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 2, TimeSpan.FromSeconds(10)), $"{Sections(vm).Count} at {vm.TilesPerRow}");
            Settle();
            var years = GridScroll(window).ExtentHeight;
            window.Height += years - 80 - GridScroll(window).ViewportHeight;
            Settle();
            Assert.True(ScrollbarShown(window));
            Assert.Equal(5, vm.TilesPerRow);
            var (requested, applied) = (vm.BuildGeneration, 0);
            vm.ViewApplied += () => applied++;

            window.Height += 100; // taller than the years (by 20 px): the scrollbar goes; the window stays within a 768-high desktop
            Settle();
            Sta.PumpUntil(() => false, TimeSpan.FromSeconds(1));

            Assert.False(ScrollbarShown(window));
            Assert.Equal(5, vm.TilesPerRow);
            Assert.True(requested == vm.BuildGeneration && applied == 0, $"builds asked for: {vm.BuildGeneration - requested}, applied: {applied}");
        });
    }

    [Fact]
    public void TheKeyboardsColumns_FollowTheWindow_WithItsFirstSectionCollapsed()
    {
        // Lens A (round 24): keyboard navigation's column count came from the first section's wrap panel too, which keeps
        // its width once collapsed: Down went two rows.
        WithWindow(HalfYears, 96, (vm, window) =>
        {
            var slot = vm.TileSize + 20;
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            SizeTheViewport(window, 6 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => Sections(vm).Count == 2 && !vm.IsLoadingView, TimeSpan.FromSeconds(10)));
            Sections(vm)[0].IsExpanded = false;
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            SizeTheViewport(window, 5 * slot + 20, frame); // still years
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(500));

            var laidOut = Descendants((ListBox)window.FindName("ItemsList")).OfType<ListBoxItem>().Where(i => i.IsVisible)
                .Select(i => Math.Round(i.TranslatePoint(new Point(0, 0), window).X)).Distinct().Count();
            Assert.Equal(2, Sections(vm).Count);
            Assert.Equal(5, laidOut);
            Assert.Equal(laidOut, window.ColumnsPerRow());
        });
    }

    [Fact]
    public void TheGrouping_FollowsTheWindow_WithItsFirstSectionCollapsed()
    {
        // Lens A (round 23): the width came from the first section's wrap panel, which keeps its width once collapsed.
        WithWindow(HalfYears, 96, (vm, window) =>
        {
            var slot = vm.TileSize + 20;
            var frame = GridScroll(window).ActualWidth - GridScroll(window).ViewportWidth
                - (ScrollbarShown(window) ? SystemParameters.VerticalScrollBarWidth : 0);
            SizeTheViewport(window, 4 * slot + 40, frame);
            Assert.True(Sta.PumpUntil(() => vm.TilesPerRow == 4 && Sections(vm).Count == 12, TimeSpan.FromSeconds(10)));
            Sections(vm)[0].IsExpanded = false;
            Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            SizeTheViewport(window, 6 * slot + 40, frame);

            Assert.True(Sta.PumpUntil(() => vm.TilesPerRow == 6, TimeSpan.FromSeconds(5)), $"{vm.TilesPerRow} across");
        });
    }

    [Fact]
    public void TheIndexingSpinner_TurnsOnlyWhileItIsShown()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var main = new MainWindow();
            var spinner = new ShapePath
            {
                Style = (Style)main.FindResource("IndexingSpinner"),
                Data = Geometry.Parse("M 13,2 A 11,11 0 1 1 2,13"),
                RenderTransform = new RotateTransform(),
            };
            var card = new Border { Visibility = Visibility.Collapsed, Child = spinner };
            var host = OffScreen(new Window { Content = card, Width = 200, Height = 200 });
            try
            {
                host.Show();
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(200));
                Assert.False(Turning(spinner), "it turns while hidden");

                card.Visibility = Visibility.Visible;
                Assert.True(Sta.PumpUntil(() => Turning(spinner), TimeSpan.FromSeconds(5)), "it doesn't turn when shown");

                card.Visibility = Visibility.Collapsed;
                Assert.True(Sta.PumpUntil(() => !Turning(spinner), TimeSpan.FromSeconds(5)), "it keeps turning once hidden");
            }
            finally
            {
                host.Close();
                main.Close();
            }
        });
    }
}
