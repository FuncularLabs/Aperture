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
/// real view model — so a binding it can't make (two-way onto a read-only property) fails here as it would at launch —
/// its list of libraries is laid out, and its animations run (round 22 of the multiple-libraries review).
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
                window.Show(); // a binding the window can't make with this view model throws here, or as it's laid out
                Assert.True(Sta.PumpUntil(() => vm.TotalItemCount == 3 && !vm.IsLoadingView, TimeSpan.FromSeconds(10)));
                Sta.PumpUntil(() => false, TimeSpan.FromMilliseconds(300)); // layout and bindings settle

                var counts = Descendants(window).OfType<TextBlock>()
                    .Where(t => t.DataContext is LibraryListItem && t.IsVisible && t.Text.TrimStart().StartsWith('('))
                    .Select(t => $"{((LibraryListItem)t.DataContext).Name} {t.Text.Trim()}")
                    .Order()
                    .ToList();
                Assert.Equal(["Default (3)", "Work (42)"], counts);

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
