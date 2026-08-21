namespace Aperture.App;

/// <summary>
/// Explorer-style 2D pick over a wrapping, date-sectioned tile grid.
/// Operates on layout rectangles so Up/Down / PageUp/PageDown follow
/// visual columns rather than ListBox item index.
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
    /// Wrap tiles into a column grid. Each section starts on a new row after an
    /// optional header gap (headers are not cells — crossing them is fine).
    /// A zero-count section still consumes <paramref name="headerHeight"/> so
    /// Page distance matches a collapsed date bucket.
    /// </summary>
    public static IReadOnlyList<Cell> LayoutWrap(
        IReadOnlyList<int> sectionCounts,
        int columns,
        double tileWidth,
        double tileHeight,
        double headerHeight)
    {
        ArgumentNullException.ThrowIfNull(sectionCounts);
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

                cells.Add(new Cell(index, row, col, col * tileWidth, y, tileWidth, tileHeight));
                index++;
            }

            row++;
            y += tileHeight;
        }

        return cells;
    }

    /// <summary>
    /// One visual row up (<paramref name="sign"/> = -1) or down (+1) in the same
    /// column. Empty cell (short last row, end of a section): nearest tile in
    /// that column, then nearest in the adjacent row.
    /// </summary>
    public static int? PickVertical(IReadOnlyList<Cell> cells, int currentIndex, int sign)
    {
        if (!TryCurrent(cells, currentIndex, out var cur) || sign == 0)
            return null;

        sign = Math.Sign(sign);
        var targetRow = cur.Row + sign;
        var adjacent = cells.Where(c => c.Row == targetRow).ToList();

        if (CellInColumn(adjacent, cur.Col) is { } sameColOnRow)
            return sameColOnRow.Index;

        var further = cells
            .Where(c => c.Col == cur.Col && c.Row * sign > cur.Row * sign)
            .OrderBy(c => Math.Abs(c.Row - cur.Row))
            .ThenBy(c => c.Index)
            .FirstOrDefault();
        if (further.Width > 0)
            return further.Index;

        if (adjacent.Count > 0)
            return adjacent.MinBy(c => (Math.Abs(c.Col - cur.Col), c.Index)).Index;

        return null;
    }

    /// <summary>
    /// After moving the viewport by one page, pick the tile in the same column
    /// closest to the same vertical position within the viewport. Not "skip N
    /// items" and not a jump onto a section header.
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
            .Where(c => c.Col == cur.Col && c.Index != cur.Index)
            .Where(c => sign > 0 ? c.CenterY > cur.CenterY : c.CenterY < cur.CenterY)
            .ToList();
        if (sameCol.Count > 0)
            return ClosestToY(sameCol, targetY, sign).Index;

        var others = cells
            .Where(c => c.Index != cur.Index)
            .Where(c => sign > 0 ? c.CenterY > cur.CenterY : c.CenterY < cur.CenterY)
            .ToList();
        if (others.Count == 0)
            return null;

        var rowY = others.MinBy(c => Math.Abs(c.CenterY - targetY)).CenterY;
        var slop = Math.Max(cur.Height * 0.5, 1);
        var row = others.Where(c => Math.Abs(c.CenterY - rowY) <= slop).ToList();
        return row.MinBy(c => (Math.Abs(c.Col - cur.Col), c.Index)).Index;
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

    private static Cell? CellInColumn(List<Cell> row, int col)
    {
        foreach (var c in row)
        {
            if (c.Col == col)
                return c;
        }

        return null;
    }

    private static Cell ClosestToY(List<Cell> cells, double targetY, int sign) =>
        cells.MinBy(c =>
        {
            var d = Math.Abs(c.CenterY - targetY);
            // Ties: prefer the cell further in the page direction so we actually move a page.
            var bias = sign > 0 ? -c.Y : c.Y;
            return (d, bias, c.Index);
        });
}
