namespace Aperture.App;

/// <summary>
/// Where a library or folder dragged within its list lands: a slot among the rows (0 above the first, the row count
/// below the last), the dragged row itself included. Pure, beside <see cref="ZoomPan"/>, so it's tested without a
/// window.
/// </summary>
public static class DropIndex
{
    /// <summary>
    /// The slot for a pointer at <paramref name="y"/> over rows at the given tops and heights (in list order, in the
    /// same frame as <paramref name="y"/>): before a row if the pointer is above its middle, otherwise after it.
    /// </summary>
    public static int At(double y, IReadOnlyList<(double Top, double Height)> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            if (y < rows[i].Top + rows[i].Height / 2)
                return i; // above this row's middle (in a gap above it, too): before it
        return rows.Count;
    }
}
