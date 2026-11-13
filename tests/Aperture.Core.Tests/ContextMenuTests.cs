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
}
