using System.Security.AccessControl;
using System.Security.Principal;

namespace Aperture.Core.Tests.Support;

/// <summary>Denies (and restores) the current user's read access to a file — a file Aperture can see but not open.</summary>
public static class Acl
{
    private static FileSystemAccessRule DenyReadRule() =>
        new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);

    public static void DenyRead(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.AddAccessRule(DenyReadRule());
        file.SetAccessControl(security);
    }

    public static void Restore(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return;
        var security = file.GetAccessControl();
        security.RemoveAccessRule(DenyReadRule());
        file.SetAccessControl(security);
    }
}
