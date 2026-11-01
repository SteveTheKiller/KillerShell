using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KillerShell.Tests.Services;

/// <summary>Denies the current user one right on a test folder until disposed.</summary>
internal sealed class DenyAccess : IDisposable
{
    private readonly string _path;
    private readonly FileSystemAccessRule _rule;
    private bool _removed;

    private DenyAccess(string path, FileSystemAccessRule rule) { _path = path; _rule = rule; }

    internal static DenyAccess To(string path, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        var folder = new DirectoryInfo(path);
        var security = folder.GetAccessControl();
        security.AddAccessRule(rule);
        folder.SetAccessControl(security);
        return new DenyAccess(path, rule);
    }

    public void Dispose()
    {
        if (_removed) return;
        _removed = true;
        var folder = new DirectoryInfo(_path);
        var security = folder.GetAccessControl();
        security.RemoveAccessRule(_rule);
        folder.SetAccessControl(security);
    }
}
