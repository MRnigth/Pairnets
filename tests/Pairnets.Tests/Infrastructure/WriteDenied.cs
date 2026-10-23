using System.Security.AccessControl;
using System.Security.Principal;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// Makes a folder one this account may not save in, as C:\Program Files is for a person without admin rights, until
/// disposed. Null when the account can save there anyway (root on Linux), so the test has nothing to check.
/// </summary>
public sealed class WriteDenied : IDisposable
{
    private readonly string _folder;
    private readonly FileSystemAccessRule? _rule;
    private readonly UnixFileMode _mode;

    private WriteDenied(string folder)
    {
        _folder = folder;
        if (OperatingSystem.IsWindows())
        {
            _rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(_rule);
            info.SetAccessControl(acl);
        }
        else
        {
            _mode = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    public static WriteDenied? On(string folder)
    {
        var denied = new WriteDenied(folder);
        var check = Path.Combine(folder, "write-check");
        try
        {
            File.WriteAllText(check, "x");
        }
        catch (UnauthorizedAccessException)
        {
            return denied;
        }
        File.Delete(check);
        denied.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(_folder);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(_rule!);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_folder, _mode);
        }
    }
}
