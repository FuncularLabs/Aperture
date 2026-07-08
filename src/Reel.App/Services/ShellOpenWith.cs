using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Reel.App.Services;

/// <summary>
/// The Windows shell "Open with" list for a file: the apps registered for its
/// extension (via <c>SHAssocEnumHandlers</c>), each invokable, plus the OS
/// "choose another app" chooser. Windows-only; degrades to an empty list.
/// </summary>
public static class ShellOpenWith
{
    public sealed class Handler(string name, IntPtr assocHandler)
    {
        public string Name { get; } = name;

        /// <summary>Runs this handler on <paramref name="path"/> (via a shell data object).</summary>
        public void Invoke(string path)
        {
            if (!OperatingSystem.IsWindows())
                return;

            var handler = (IAssocHandler)Marshal.GetObjectForIUnknown(assocHandler);
            IShellItem? item = null;
            var dataObject = IntPtr.Zero;
            try
            {
                var iidItem = Guids.IShellItem;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out item);

                var bhid = Guids.BhidDataObject;
                var iidData = Guids.IDataObject;
                if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iidData, out dataObject) == 0 && dataObject != IntPtr.Zero)
                    handler.Invoke(dataObject);
            }
            catch { /* best effort */ }
            finally
            {
                if (dataObject != IntPtr.Zero)
                    Marshal.Release(dataObject);
                if (item is not null)
                    Marshal.ReleaseComObject(item);
                Marshal.ReleaseComObject(handler);
            }
        }
    }

    /// <summary>Recommended apps for the file's extension.</summary>
    public static List<Handler> GetHandlers(string path)
    {
        var results = new List<Handler>();
        if (!OperatingSystem.IsWindows())
            return results;

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
            return results;

        try
        {
            SHAssocEnumHandlers(ext, AssocFilter.Recommended, out var enumerator);
            while (enumerator.Next(1, out var handler, out var fetched) == 0 && fetched == 1)
            {
                if (handler.GetUIName(out var pName) == 0)
                {
                    var name = Marshal.PtrToStringUni(pName);
                    Marshal.FreeCoTaskMem(pName);
                    if (!string.IsNullOrWhiteSpace(name))
                        results.Add(new Handler(name!, Marshal.GetIUnknownForObject(handler)));
                }
                Marshal.ReleaseComObject(handler);
            }
            Marshal.ReleaseComObject(enumerator);
        }
        catch { /* enumeration failed — return whatever we have */ }

        return results;
    }

    /// <summary>The OS "How do you want to open this file?" chooser.</summary>
    public static void ChooseAnotherApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")
            {
                UseShellExecute = false,
            });
        }
        catch { }
    }

    // --- interop ---

    private enum AssocFilter { None = 0, Recommended = 1 }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHAssocEnumHandlers(string pszExtra, AssocFilter afFilter, out IEnumAssocHandlers ppEnumHandler);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static class Guids
    {
        public static Guid IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        public static Guid IDataObject = new("0000010e-0000-0000-c000-000000000046");
        public static Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    }

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler rgelt, out uint pceltFetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName(out IntPtr ppsz);
        [PreserveSig] int GetUIName(out IntPtr ppsz);
        [PreserveSig] int GetIconLocation(out IntPtr ppszPath, out int pIndex);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] int Invoke(IntPtr pdo);
        [PreserveSig] int CreateInvoker(IntPtr pdo, out IntPtr ppInvoker);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }
}
