using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aperture.App;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The right-click menus' new items, read from the window's own XAML (0.9.2 C6, M1): a folder's Online-only files
/// submenu and a tile's Move to…, each bound to the command it says.
/// </summary>
public class ContextMenuTests
{
    private static string? PathOf(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBinding(target, property)?.Path?.Path;

    [Fact]
    public void TheFolderMenu_HasOnlineOnlyFiles_WithTheFourAnswers_TheOneInForceChecked()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var tree = (TreeView)window.FindName("FolderTreeView");
            var row = (DependencyObject)tree.ItemTemplate.LoadContent();
            var menu = ((FrameworkElement)row).ContextMenu ?? throw new Xunit.Sdk.XunitException("no folder menu");

            var onlineOnly = Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Header as string == "Online-only files");
            var answers = onlineOnly.Items.OfType<MenuItem>().ToList();

            Assert.Equal(["Ask each time", "Skip", "Index in the background", "Index now"], answers.Select(i => i.Header as string));
            Assert.Equal(["Ask", "Skip", "Background", "Now"], answers.Select(i => i.CommandParameter as string));
            Assert.All(answers, i => Assert.Equal("Root.SetOnlineOnlyCommand", PathOf(i, MenuItem.CommandProperty)));
            Assert.Equal(
                ["Root.OnlineOnlyAsks", "Root.OnlineOnlySkips", "Root.OnlineOnlyInBackground", "Root.OnlineOnlyNow"],
                answers.Select(i => PathOf(i, MenuItem.IsCheckedProperty)));
            window.Close();
        });
    }

    [Fact]
    public void TheTileMenu_HasMoveTo_AfterCut()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var menu = ((ListBox)window.FindName("ItemsList")).ContextMenu!;
            var items = menu.Items.OfType<MenuItem>().ToList();

            var cut = items.FindIndex(i => i.Header as string == "Cut");
            var move = items.FindIndex(i => i.Header as string == "Move to…");
            Assert.Equal(cut + 1, move);
            Assert.Equal("Data.MoveToCommand", PathOf(items[move], MenuItem.CommandProperty));
            Assert.Equal("Data.CutFileCommand", PathOf(items[cut], MenuItem.CommandProperty)); // the selection's
            window.Close();
        });
    }

    [Fact]
    public void QuickViewsMenu_CopiesAndCutsTheItemShown_AndHasNoMoveTo()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var menu = (ContextMenu)window.FindResource("PreviewContextMenu");
            var items = menu.Items.OfType<MenuItem>().ToList();

            Assert.Equal("Data.CopyShownFileCommand", PathOf(items.Single(i => i.Header as string == "Copy"), MenuItem.CommandProperty));
            Assert.Equal("Data.CutShownFileCommand", PathOf(items.Single(i => i.Header as string == "Cut"), MenuItem.CommandProperty));
            Assert.DoesNotContain(items, i => i.Header as string == "Move to…");
            // No keys shown: Ctrl+C / Ctrl+X act on the grid's selection, not the item shown (round 1, A-N4).
            Assert.All(items.Where(i => i.Header as string is "Copy" or "Cut"), i => Assert.True(string.IsNullOrEmpty(i.InputGestureText)));
            window.Close();
        });
    }
}
