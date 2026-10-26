using System.Security.AccessControl;
using System.Security.Principal;

namespace Aperture.Core.Tests.Support;

/// <summary>
/// Denies (and restores) the current user's access to a file or folder — a file Aperture can see but not read,
/// or a folder it can't create files in.
/// </summary>
public static class Acl
{
    private static FileSystemAccessRule Deny(FileSystemRights rights) =>
        new(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);

    public static void DenyRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(Deny(FileSystemRights.ReadData));
        file.SetAccessControl(security);
    }

    public static void Restore(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return;
        var security = file.GetAccessControl();
        security.RemoveAccessRule(Deny(FileSystemRights.ReadData));
        file.SetAccessControl(security);
    }

    public static void DenyCreateFiles(string dir)
    {
        var folder = new DirectoryInfo(dir);
        var security = folder.GetAccessControl();
        security.AddAccessRule(Deny(FileSystemRights.CreateFiles));
        folder.SetAccessControl(security);
    }

    public static void RestoreCreateFiles(string dir)
    {
        var folder = new DirectoryInfo(dir);
        if (!folder.Exists)
            return;
        var security = folder.GetAccessControl();
        security.RemoveAccessRule(Deny(FileSystemRights.CreateFiles));
        folder.SetAccessControl(security);
    }

    public static void DenyListing(string dir)
    {
        var folder = new DirectoryInfo(dir);
        var security = folder.GetAccessControl();
        security.AddAccessRule(Deny(FileSystemRights.ListDirectory));
        folder.SetAccessControl(security);
    }

    public static void RestoreListing(string dir)
    {
        var folder = new DirectoryInfo(dir);
        if (!folder.Exists)
            return;
        var security = folder.GetAccessControl();
        security.RemoveAccessRule(Deny(FileSystemRights.ListDirectory));
        folder.SetAccessControl(security);
    }
}
