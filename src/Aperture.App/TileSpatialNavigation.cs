namespace Aperture.App;

/// <summary>
/// Explorer-style 2D pick over a wrapping, date-sectioned tile grid.
/// Operates on layout rectangles so Up/Down / PageUp/PageDown follow
/// visual X (the column on screen) rather than ListBox item index or a
/// stale <c>index % columns</c> guess.
/// </summary>
internal static class TileSpatialNavigation
{
    internal readonly record struct Cell(
        int Index,
        int Row,
        int Col,
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;
    }

    /// <summary>
    /// How many tiles a wrap panel actually fits:
    /// <c>floor(availableWidth / slotWidth)</c>.
    /// <paramref name="availableWidth"/> is the panel/viewport width the wrap
    /// used (scrollbar already excluded). Do not subtract padding again.
    /// <paramref name="slotWidth"/> is the arranged child size including margin.
    /// </summary>
    public static int FitColumns(double availableWidth, double slotWidth)
    {
        slotWidth = Math.Max(1, slotWidth);
        availableWidth = Math.Max(slotWidth, availableWidth);
        return Math.Max(1, (int)Math.Floor(availableWidth / slotWidth));
    }

    /// <summary>
    /// Visual column count. A stale width-based estimate (scrollbar subtracted
    /// twice, padding fudge, leftover tile-size guess) can come in one low;
    /// never prefer that over columns already seen on screen.
    /// </summary>
    public static int ResolveColumnCount(int columnsFromLayoutX, int fittedFromWidth) =>
        Math.Max(1, Math.Max(columnsFromLayoutX, fittedFromWidth));

    /// <summary>
    /// Cluster left edges into visual columns. Tiles in the same wrap column
    /// share an X; a short last row just contributes fewer clusters.
    /// </summary>
    public static IReadOnlyList<double> DistinctColumnXs(IReadOnlyList<double> xs, double slop)
    {
        if (xs is null || xs.Count == 0)
            return [];

        slop = Math.Max(1, slop);
        var sorted = xs.OrderBy(x => x).ToList();
        var groups = new List<List<double>> { [sorted[0]] };
        for (var i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i] - groups[^1][0]) > slop)
                groups.Add([sorted[i]]);
            else
                groups[^1].Add(sorted[i]);
        }

        return groups.Select(g => g.Average()).ToList();
    }

    /// <summary>
    /// Uniform wrap origins (start + between + end leftover), matching
    /// VirtualizingWrapPanel <c>SpacingMode.Uniform</c>.
    /// </summary>
    public static IReadOnlyList<double> UniformColumnXs(
        double availableWidth, double slotWidth, double tileWidth)
    {
        var columns = FitColumns(availableWidth, slotWidth);
        slotWidth = Math.Max(1, slotWidth);
        tileWidth = Math.Max(1, tileWidth);
        var unused = Math.Max(0, availableWidth - columns * slotWidth);
        var outer = unused / (columns + 1);
        var xs = new double[columns];
        for (var i = 0; i < columns; i++)
            xs[i] = outer + i * (slotWidth + outer);
        return xs;
    }

    /// <summary>
    /// Relabel Row/Col as <c>index % staleColumns</c> while keeping visual X/Y.
    /// That is the 0.8.4 path when the window under-counted columns (scrollbar /
    /// padding). Pickers that still use Col/Row walk diagonally; X-based pick
    /// must not.
    /// </summary>
    public static IReadOnlyList<Cell> WithIndexColumns(IReadOnlyList<Cell> cells, int staleColumns)
    {
        ArgumentNullException.ThrowIfNull(cells);
        staleColumns = Math.Max(1, staleColumns);
        var relabeled = new Cell[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            relabeled[i] = c with { Col = c.Index % staleColumns, Row = c.Index / staleColumns };
        }
        return relabeled;
    }

    /// <summary>
    /// Copy measured X/Y/size onto the matching model cells. Unrealized tiles
    /// keep the wrap model so virtualization cannot invent a wrong column.
    /// </summary>
    public static IReadOnlyList<Cell> OverlayMeasured(
        IReadOnlyList<Cell> model, IReadOnlyDictionary<int, Cell> measured)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (measured is null || measured.Count == 0)
            return model;

        var next = new Cell[model.Count];
        for (var i = 0; i < model.Count; i++)
        {
            var c = model[i];
            next[i] = measured.TryGetValue(c.Index, out var m)
                ? c with { X = m.X, Y = m.Y, Width = m.Width, Height = m.Height }
                : c;
        }
        return next;
    }

    /// <summary>
    /// Wrap tiles into a column grid. Each section starts on a new row after an
    /// optional header gap (headers are not cells — crossing them is fine).
    /// A zero-count section still consumes <paramref name="headerHeight"/> so
    /// Page distance matches a collapsed date bucket.
    /// <para>
    /// When <paramref name="columnXs"/> is set, X comes from those origins
    /// (actual wrap / Uniform spacing) instead of <c>col * tileWidth</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Cell> LayoutWrap(
        IReadOnlyList<int> sectionCounts,
        int columns,
        double tileWidth,
        double tileHeight,
        double headerHeight,
        IReadOnlyList<double>? columnXs = null)
    {
        ArgumentNullException.ThrowIfNull(sectionCounts);
        if (columnXs is { Count: > 0 })
            columns = columnXs.Count;
        columns = Math.Max(1, columns);
        tileWidth = Math.Max(1, tileWidth);
        tileHeight = Math.Max(1, tileHeight);
        headerHeight = Math.Max(0, headerHeight);

        var cells = new List<Cell>();
        var index = 0;
        var row = 0;
        var y = 0.0;

        foreach (var count in sectionCounts)
        {
            y += headerHeight;
            if (count <= 0)
                continue;

            for (var i = 0; i < count; i++)
            {
                var col = i % columns;
                if (i > 0 && col == 0)
                {
                    row++;
                    y += tileHeight;
                }

                var x = columnXs is { Count: > 0 } ? columnXs[col] : col * tileWidth;
                cells.Add(new Cell(index, row, col, x, y, tileWidth, tileHeight));
                index++;
            }

            row++;
            y += tileHeight;
        }

        return cells;
    }

    /// <summary>
    /// One visual row up (<paramref name="sign"/> = -1) or down (+1) in the same
    /// <em>screen</em> column (layout X), not <c>index % columns</c>. Empty cell
    /// (short last row, end of a section): nearest tile in that column, then
    /// nearest in the adjacent row.
    /// </summary>
    public static int? PickVertical(IReadOnlyList<Cell> cells, int currentIndex, int sign)
    {
        if (!TryCurrent(cells, currentIndex, out var cur) || sign == 0)
            return null;

        sign = Math.Sign(sign);
        var inColumn = cells
            .Where(c => c.Index != cur.Index && SameColumnX(c, cur) && FurtherY(c, cur, sign))
            .ToList();
        if (inColumn.Count > 0)
            return inColumn.MinBy(c => (Math.Abs(c.CenterY - cur.CenterY), c.Index)).Index;

        var others = cells
            .Where(c => c.Index != cur.Index && FurtherY(c, cur, sign))
            .ToList();
        if (others.Count == 0)
            return null;

        var nextY = others.MinBy(c => Math.Abs(c.CenterY - cur.CenterY)).CenterY;
        var rowSlop = Math.Max(cur.Height * 0.5, 1);
        var adjacent = others.Where(c => Math.Abs(c.CenterY - nextY) <= rowSlop).ToList();
        return adjacent.MinBy(c => (Math.Abs(c.CenterX - cur.CenterX), c.Index)).Index;
    }

    /// <summary>
    /// After moving the viewport by one page, pick the tile in the same visual
    /// column closest to the same vertical position within the viewport. Not
    /// "skip N items" and not a jump onto a section header.
    /// </summary>
    public static int? PickPage(
        IReadOnlyList<Cell> cells,
        int currentIndex,
        double viewportTop,
        double viewportHeight,
        int sign)
    {
        if (!TryCurrent(cells, currentIndex, out var cur) || sign == 0)
            return null;

        sign = Math.Sign(sign);
        viewportHeight = Math.Max(1, viewportHeight);
        var yInViewport = cur.CenterY - viewportTop;
        var newViewportTop = viewportTop + sign * viewportHeight;
        var targetY = newViewportTop + yInViewport;

        var sameCol = cells
            .Where(c => c.Index != cur.Index && SameColumnX(c, cur) && FurtherY(c, cur, sign))
            .ToList();
        if (sameCol.Count > 0)
            return ClosestToY(sameCol, targetY, sign).Index;

        var others = cells
            .Where(c => c.Index != cur.Index && FurtherY(c, cur, sign))
            .ToList();
        if (others.Count == 0)
            return null;

        var rowY = others.MinBy(c => Math.Abs(c.CenterY - targetY)).CenterY;
        var slop = Math.Max(cur.Height * 0.5, 1);
        var row = others.Where(c => Math.Abs(c.CenterY - rowY) <= slop).ToList();
        return row.MinBy(c => (Math.Abs(c.CenterX - cur.CenterX), c.Index)).Index;
    }

    /// <summary>
    /// Among laid-out (or realized) cells, pick the same-column cell closest to
    /// <paramref name="targetY"/>; if that column is empty, the nearest cell on
    /// the row closest to that Y. Used after a one-viewport scroll to land on
    /// the same screen slot.
    /// </summary>
    public static int? PickAtColumn(IReadOnlyList<Cell> cells, double columnX, double targetY)
    {
        if (cells is null || cells.Count == 0)
            return null;

        var tileW = Math.Max(cells[0].Width, 1);
        var sameCol = cells.Where(c => Math.Abs(c.CenterX - columnX) <= tileW * 0.5).ToList();
        if (sameCol.Count > 0)
            return sameCol.MinBy(c => (Math.Abs(c.CenterY - targetY), c.Index)).Index;

        var rowY = cells.MinBy(c => Math.Abs(c.CenterY - targetY)).CenterY;
        var slop = Math.Max(cells[0].Height * 0.5, 1);
        var row = cells.Where(c => Math.Abs(c.CenterY - rowY) <= slop).ToList();
        return row.MinBy(c => (Math.Abs(c.CenterX - columnX), c.Index)).Index;
    }

    private static bool TryCurrent(IReadOnlyList<Cell> cells, int currentIndex, out Cell cur)
    {
        if (cells is null || currentIndex < 0 || currentIndex >= cells.Count)
        {
            cur = default;
            return false;
        }

        cur = cells[currentIndex];
        return true;
    }

    private static bool SameColumnX(Cell a, Cell b)
    {
        var slop = Math.Max(Math.Max(a.Width, b.Width) * 0.5, 1);
        return Math.Abs(a.CenterX - b.CenterX) <= slop;
    }

    private static bool FurtherY(Cell candidate, Cell current, int sign) =>
        sign > 0
            ? candidate.CenterY > current.CenterY + 1
            : candidate.CenterY < current.CenterY - 1;

    private static Cell ClosestToY(List<Cell> cells, double targetY, int sign) =>
        cells.MinBy(c =>
        {
            var d = Math.Abs(c.CenterY - targetY);
            // Ties: prefer the cell further in the page direction so we actually move a page.
            var bias = sign > 0 ? -c.Y : c.Y;
            return (d, bias, c.Index);
        });
}
