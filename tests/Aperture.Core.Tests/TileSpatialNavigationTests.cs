using Aperture.App;

namespace Aperture.Core.Tests;

/// <summary>
/// Explorer-style spatial pick: same visual column, page = one viewport.
/// Live WPF (realized containers, scroll offset, Tab focus) is a manual path.
/// </summary>
public class TileSpatialNavigationTests
{
    // 4-column wrap, two date sections: 6 + 8 tiles. Header is a gap, not a cell.
    //
    //   [A0 A1 A2 A3]
    //   [A4 A5      ]
    //   ---- header ----
    //   [B0 B1 B2 B3]
    //   [B4 B5 B6 B7]
    private static IReadOnlyList<TileSpatialNavigation.Cell> TwoSections() =>
        TileSpatialNavigation.LayoutWrap([6, 8], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 50);

    [Fact]
    public void Layout_AssignsColumnsAndSkipsHeaders()
    {
        var cells = TwoSections();
        Assert.Equal(14, cells.Count);
        Assert.Equal(1, cells[5].Col);          // A5
        Assert.Equal(0, cells[6].Col);          // B0 — new section, col 0
        Assert.True(cells[6].Y > cells[5].Y);   // header gap between sections
        Assert.Equal(cells[1].X, cells[7].X);   // A1 and B1 share a column
    }

    [Fact]
    public void Down_SameColumn_NextRow()
    {
        var cells = TwoSections();
        // A1 (row 0, col 1) → A5 (row 1, col 1)
        Assert.Equal(5, TileSpatialNavigation.PickVertical(cells, 1, +1));
    }

    [Fact]
    public void Up_SameColumn_PreviousRow()
    {
        var cells = TwoSections();
        Assert.Equal(1, TileSpatialNavigation.PickVertical(cells, 5, -1));
    }

    [Fact]
    public void Down_CrossesSectionHeader_SameColumn()
    {
        var cells = TwoSections();
        // A5 (col 1) → B1 (col 1), not a header and not B0
        Assert.Equal(7, TileSpatialNavigation.PickVertical(cells, 5, +1));
    }

    [Fact]
    public void Down_EmptyCell_PrefersSameColumnInNextSection()
    {
        var cells = TwoSections();
        // A3 (col 3) — next row of A is short. Nearest in that column is B3.
        Assert.Equal(9, TileSpatialNavigation.PickVertical(cells, 3, +1));
    }

    [Fact]
    public void Down_EmptyCell_FallsBackToNearestInRow_WhenColumnEnds()
    {
        var cells = TileSpatialNavigation.LayoutWrap([6], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        // A3 (col 3) — no further col-3 tile. Nearest on the short row is A5 (col 1).
        Assert.Equal(5, TileSpatialNavigation.PickVertical(cells, 3, +1));
    }

    [Fact]
    public void Down_IsNotLinearIndexPlusOne()
    {
        var cells = TileSpatialNavigation.LayoutWrap([12], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        // Index 5 is row 1 col 1 → row 2 col 1 is index 9, not 6.
        Assert.Equal(9, TileSpatialNavigation.PickVertical(cells, 5, +1));
        Assert.NotEqual(6, TileSpatialNavigation.PickVertical(cells, 5, +1));
    }

    [Fact]
    public void Vertical_StaysPut_AtEndOfColumn()
    {
        var cells = TwoSections();
        Assert.Null(TileSpatialNavigation.PickVertical(cells, 13, +1)); // last tile
        Assert.Null(TileSpatialNavigation.PickVertical(cells, 0, -1));  // first tile
    }

    [Fact]
    public void PageDown_OneViewport_SameColumn_SameViewportY()
    {
        var cells = TwoSections();
        // A1 at Y=50 (after header), center 100. Viewport 0..200.
        // New viewport 200..400; same screen Y → target center 300.
        // Same column: A1 (100), A5 (200), B1 (350), B5 (450). Closest to 300 is B1.
        var pick = TileSpatialNavigation.PickPage(cells, currentIndex: 1, viewportTop: 0, viewportHeight: 200, sign: +1);
        Assert.Equal(7, pick);
    }

    [Fact]
    public void PageDown_IsNotSkipNItems()
    {
        var cells = TwoSections();
        // Skip-8-items from A1 would land on B3 (index 9). Same-column page lands on B1 (7).
        var pick = TileSpatialNavigation.PickPage(cells, currentIndex: 1, viewportTop: 0, viewportHeight: 200, sign: +1);
        Assert.NotEqual(1 + 8, pick);
        Assert.Equal(7, pick);
    }

    [Fact]
    public void PageDown_DoesNotLandOnAHeader()
    {
        var cells = TwoSections();
        var pick = TileSpatialNavigation.PickPage(cells, currentIndex: 1, viewportTop: 0, viewportHeight: 200, sign: +1);
        Assert.NotNull(pick);
        Assert.InRange(pick.Value, 0, cells.Count - 1);
        // Every returned index is a tile cell — headers were never laid out.
        Assert.Equal(cells[pick.Value].Col, cells[1].Col);
    }

    [Fact]
    public void PageUp_MirrorsPageDown()
    {
        var cells = TwoSections();
        // B1 center 350 in a viewport starting at 200. PageUp → viewport 0,
        // target center 150. A1 (100) and A5 (200) tie; prefer the page
        // direction (up) so we actually move a viewport, landing on A1.
        var pick = TileSpatialNavigation.PickPage(cells, currentIndex: 7, viewportTop: 200, viewportHeight: 200, sign: -1);
        Assert.Equal(1, pick);
    }

    [Fact]
    public void Page_AtEnd_StaysInColumn()
    {
        var cells = TwoSections();
        Assert.Null(TileSpatialNavigation.PickPage(cells, 13, viewportTop: 300, viewportHeight: 200, sign: +1));
    }

    [Fact]
    public void PickAtColumn_SameColumnClosestY()
    {
        var cells = TwoSections();
        // Column X of col 1 is 150. Target Y = 350 (B1 center).
        Assert.Equal(7, TileSpatialNavigation.PickAtColumn(cells, columnX: 150, targetY: 350));
    }

    [Fact]
    public void PickAtColumn_EmptyColumn_NearestInRow()
    {
        // Realized short last row only (the page after a scroll). Col 3 is empty
        // on this page, so land on the nearest tile in the row (index 1).
        var row = TileSpatialNavigation.LayoutWrap([2], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        Assert.Equal(1, TileSpatialNavigation.PickAtColumn(row, columnX: 350, targetY: 50));
    }

    [Fact]
    public void CollapsedSection_ConsumesHeaderGap_ButNoCells()
    {
        var cells = TileSpatialNavigation.LayoutWrap([4, 0, 4], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 40);
        Assert.Equal(8, cells.Count);
        // First tile of the third section sits below two headers + one tile row.
        Assert.Equal(40 + 100 + 40 + 40, cells[4].Y);
        // Down from last tile of section 0 (index 1, col 1) crosses the collapsed bucket to col 1 of section 2.
        Assert.Equal(5, TileSpatialNavigation.PickVertical(cells, 1, +1));
    }

    // --- 0.8.5: visual X, not a stale column count (Paul's recording) ---

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Down_FromColumn0_StaysInColumn0_NotColumn1(int columns)
    {
        var cells = TileSpatialNavigation.LayoutWrap([columns * 3], columns, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        var pick = TileSpatialNavigation.PickVertical(cells, 0, +1);
        Assert.Equal(columns, pick);                 // next row, same visual column
        Assert.NotEqual(columns + 1, pick);          // must not slide into column 1
        Assert.Equal(cells[0].X, cells[pick!.Value].X);
    }

    [Fact]
    public void Up_FourColumnWrap_DoesNotUseStaleThree()
    {
        // Paul's 4-column zoom: Need a Hint at visual row 3, col 3 (1-based)
        // is index 10. Up must stay in that column (index 6), not land on
        // index 7 — Bar Chart, one tile right. 0.8.4 subtracted 3 because
        // (ActualWidth-12)/slot ignored that the viewport already excluded
        // the scrollbar, so calculatedColumns = 3.
        const int visualColumns = 4;
        const int wrongColumns = 3;
        var visual = TileSpatialNavigation.LayoutWrap(
            [visualColumns * 5], visualColumns, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        var staleLabels = TileSpatialNavigation.WithIndexColumns(visual, wrongColumns);

        const int needAHint = 10; // row 2, col 2 (0-based) on a 4-col wrap
        const int barChartDiagonal = needAHint - wrongColumns; // 7 — today's math
        const int sameColumnUp = needAHint - visualColumns;    // 6

        Assert.Equal(200, visual[needAHint].X);
        Assert.Equal(visual[needAHint].X, visual[sameColumnUp].X);
        Assert.NotEqual(visual[needAHint].X, visual[barChartDiagonal].X);

        // Col/Row look like a 3-column wrap (the 0.8.4 picker). X is still 4-col.
        Assert.Equal(1, staleLabels[needAHint].Col);
        Assert.Equal(7, StepByColumnCount(needAHint, -wrongColumns));

        var pick = TileSpatialNavigation.PickVertical(staleLabels, needAHint, -1);
        Assert.Equal(sameColumnUp, pick);
        Assert.NotEqual(barChartDiagonal, pick);
        Assert.Equal(visual[needAHint].CenterX, visual[pick!.Value].CenterX);
    }

    [Fact]
    public void Up_SixColumnWrap_DoesNotSkipARow()
    {
        // 6-column zoom: green Wordle at row 6, col 3 (1-based) is index 32.
        // One Up must be the tile immediately above (index 26), not a 12-item
        // jump that skips the in-between row.
        var cells = TileSpatialNavigation.LayoutWrap([42], columns: 6, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        const int green = 5 * 6 + 2; // row 5, col 2
        Assert.Equal(26, TileSpatialNavigation.PickVertical(cells, green, -1));
        Assert.Equal(20, TileSpatialNavigation.PickVertical(cells, 26, -1));
        Assert.NotEqual(20, TileSpatialNavigation.PickVertical(cells, green, -1));
        Assert.Equal(cells[green].X, cells[26].X);
    }

    [Fact]
    public void Up_SixColumnWrap_StaleFiveStillUsesVisualX()
    {
        var visual = TileSpatialNavigation.LayoutWrap([42], columns: 6, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        var stale = TileSpatialNavigation.WithIndexColumns(visual, staleColumns: 5);
        const int green = 32;
        var pick = TileSpatialNavigation.PickVertical(stale, green, -1);
        Assert.Equal(26, pick);
        Assert.NotEqual(green - 5, pick);
        Assert.Equal(visual[green].X, visual[pick!.Value].X);
    }

    [Fact]
    public void ResolveColumnCount_PrefersVisualFour_OverStaleFittedThree()
    {
        // Viewport already excludes the scrollbar; subtracting padding again
        // (or using TileSize+20) yields 3. The wrap fitted 4.
        Assert.Equal(4, TileSpatialNavigation.FitColumns(availableWidth: 860, slotWidth: 215));
        Assert.Equal(3, TileSpatialNavigation.FitColumns(availableWidth: 860 - 12, slotWidth: 215 + 20));
        Assert.Equal(4, TileSpatialNavigation.ResolveColumnCount(columnsFromLayoutX: 4, fittedFromWidth: 3));
        Assert.Equal(6, TileSpatialNavigation.ResolveColumnCount(columnsFromLayoutX: 5, fittedFromWidth: 6));
    }

    [Fact]
    public void DistinctColumnXs_CountsVisualColumns_NotFlatIndex()
    {
        var visual = TileSpatialNavigation.LayoutWrap([12], columns: 4, tileWidth: 100, tileHeight: 100, headerHeight: 0);
        var xs = TileSpatialNavigation.DistinctColumnXs(visual.Select(c => c.X).ToList(), slop: 50);
        Assert.Equal(4, xs.Count);
        Assert.Equal(4, TileSpatialNavigation.ResolveColumnCount(xs.Count, fittedFromWidth: 3));
    }

    [Fact]
    public void Down_DateSectioned_SameVisualX_AtTwoColumnCounts()
    {
        foreach (var columns in new[] { 4, 6 })
        {
            var cells = TileSpatialNavigation.LayoutWrap(
                [columns + 2, columns * 2], columns, tileWidth: 100, tileHeight: 100, headerHeight: 50);
            var pick = TileSpatialNavigation.PickVertical(cells, 0, +1);
            Assert.Equal(columns, pick);
            Assert.Equal(cells[0].X, cells[pick!.Value].X);
            Assert.NotEqual(columns + 1, pick);
        }
    }

    private static int StepByColumnCount(int index, int delta) => index + delta;
}
