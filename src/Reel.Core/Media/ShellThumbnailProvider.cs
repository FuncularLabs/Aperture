using System.Runtime.InteropServices;
using SkiaSharp;

namespace Reel.Core.Media;

/// <summary>
/// Gets a thumbnail for a file from the Windows shell (<c>IShellItemImageFactory</c>) —
/// the same frame Explorer renders. Used for videos, and available as a fallback for
/// image formats SkiaSharp can't decode. Windows-only; returns null on any failure.
/// </summary>
public static class ShellThumbnailProvider
{
    /// <summary>
    /// Returns an upright <see cref="SKBitmap"/> for the file, fitted within
    /// <paramref name="maxEdge"/>, or null if the shell can't produce one.
    /// </summary>
    /// <param name="allowIconFallback">
    /// When true and no real frame thumbnail can be extracted, falls back to the
    /// file-type association icon rather than returning null.
    /// </param>
    public static SKBitmap? GetBitmap(string path, int maxEdge, bool allowIconFallback = true)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // Shell thumbnail extraction is far more reliable from an STA thread with
        // COM initialized, so run the whole thing there.
        return RunSta(() =>
        {
            // Prefer a genuine frame; only fall back to the icon if asked.
            var frame = TryGetImage(path, maxEdge, SIIGBF.ThumbnailOnly);
            if (frame is not null || !allowIconFallback)
                return frame;
            return TryGetImage(path, maxEdge, SIIGBF.ResizeToFit);
        });
    }

    private static SKBitmap? TryGetImage(string path, int maxEdge, SIIGBF flags)
    {
        IShellItemImageFactory? factory = null;
        var hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory is null)
                return null;

            var size = new SIZE { cx = maxEdge, cy = maxEdge };
            factory.GetImage(size, flags, out hBitmap);
            if (hBitmap == IntPtr.Zero)
                return null;

            return ConvertHBitmap(hBitmap);
        }
        catch
        {
            // THUMBNAILONLY throws when no frame can be produced — treated as "no preview".
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    private static SKBitmap? RunSta(Func<SKBitmap?> work)
    {
        SKBitmap? result = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch { result = null; }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static SKBitmap? ConvertHBitmap(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0)
            return null;

        int width = bm.bmWidth, height = bm.bmHeight;
        if (width <= 0 || height <= 0)
            return null;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negative => top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        var buffer = new byte[width * height * 4];
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            var scanned = GetDIBits(screenDc, hBitmap, 0, (uint)height, buffer, ref header, 0);
            if (scanned == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // Shell frames are opaque but often come back with a zeroed alpha channel;
        // force it so the bitmap isn't fully transparent.
        var opaque = true;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] != 0) { opaque = false; break; }
        }
        if (opaque)
        {
            for (var i = 3; i < buffer.Length; i += 4)
                buffer[i] = 255;
        }

        var bitmap = new SKBitmap();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            // Copy out of the pinned buffer so the SKBitmap owns its memory.
            var owned = bitmap.Copy();
            return owned;
        }
        finally
        {
            bitmap.Dispose();
            handle.Free();
        }
    }

    // --- interop ---

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint cLines,
        [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
