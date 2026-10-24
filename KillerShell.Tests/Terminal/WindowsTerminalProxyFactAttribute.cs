using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// A test that crosses COM with ITerminalHandoff3, which only Windows Terminal's package
/// registers a proxy for. Without Windows Terminal there is nothing to test, not a failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsTerminalProxyFactAttribute : FactAttribute
{
    public WindowsTerminalProxyFactAttribute() => Skip = WindowsTerminalProxy.GetSkipReason();
}

internal static class WindowsTerminalProxy
{
    private const string TerminalHandoff3 = "{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}";

    internal static string? GetSkipReason()
    {
        try
        {
            using var classic = Registry.ClassesRoot.OpenSubKey(@"Interface\" + TerminalHandoff3);
            if (classic != null) return null;

            // Packaged apps list their proxies here, one subkey per package full name. The list is
            // machine-wide, but the proxy DLL under WindowsApps opens only for an account the
            // package is registered for: an account that has never signed in at the desktop (a
            // scheduled-task test user) gets E_ACCESSDENIED from the call instead.
            using var packaged = Registry.ClassesRoot.OpenSubKey(@"PackagedCom\InterfaceIndex\" + TerminalHandoff3);
            if (packaged == null) return "Needs Windows Terminal, whose package registers the ITerminalHandoff3 proxy.";
            foreach (string fullName in packaged.GetSubKeyNames())
                if (RegisteredForThisAccount(fullName)) return null;
            return "Windows Terminal is installed but not registered for this account; Windows registers it at the account's first desktop sign-in.";
        }
        catch (Exception)
        {
            return "Could not read the ITerminalHandoff3 proxy registration.";
        }
    }

    private static bool RegisteredForThisAccount(string packageFullName)
    {
        uint length = 0;
        if (PackageFamilyNameFromFullName(packageFullName, ref length, null) != ERROR_INSUFFICIENT_BUFFER) return false;
        var family = new StringBuilder((int)length);
        if (PackageFamilyNameFromFullName(packageFullName, ref length, family) != 0) return false;

        // Counts only the current user's packages of that family.
        uint count = 0, buffer = 0;
        int rc = GetPackagesByPackageFamily(family.ToString(), ref count, IntPtr.Zero, ref buffer, IntPtr.Zero);
        return rc == ERROR_INSUFFICIENT_BUFFER || (rc == 0 && count > 0);
    }

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(string packageFullName, ref uint packageFamilyNameLength,
                                                            StringBuilder? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, IntPtr packageFullNames,
                                                         ref uint bufferLength, IntPtr buffer);
}
