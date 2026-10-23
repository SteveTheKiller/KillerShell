using System;
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
            // Packaged apps list their proxies here; a classic registration would be under Interface.
            using var packaged = Registry.ClassesRoot.OpenSubKey(@"PackagedCom\InterfaceIndex\" + TerminalHandoff3);
            using var classic = Registry.ClassesRoot.OpenSubKey(@"Interface\" + TerminalHandoff3);
            return packaged != null || classic != null
                ? null
                : "Needs Windows Terminal, whose package registers the ITerminalHandoff3 proxy.";
        }
        catch (Exception)
        {
            return "Could not read the ITerminalHandoff3 proxy registration.";
        }
    }
}
