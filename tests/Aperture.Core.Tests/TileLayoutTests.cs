using System.Windows.Controls;
using Aperture.App.Mvvm;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The tile size is set once on the grid and inherited by every tile inside it — so a tile never has to reach the
/// window's view model for it (Paul's trial, 2026-10-26: that made each library switch slower than the last).
/// </summary>
public class TileLayoutTests
{
    [Fact]
    public void ASizeSetOnTheGrid_ReachesEveryTileInsideIt()
    {
        Sta.Run(() =>
        {
            var tile = new Border();
            var row = new StackPanel { Children = { tile } };
            var grid = new Grid { Children = { row } };

            Assert.Equal(160.0, TileLayout.GetSize(tile)); // before anything is set

            TileLayout.SetSize(grid, 280);

            Assert.Equal(280.0, TileLayout.GetSize(tile));
            Assert.Equal(280.0, TileLayout.GetSize(row));
        });
    }
}
