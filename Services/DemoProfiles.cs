using System;
using System.Collections.Generic;

namespace KillerShell.Services
{
    internal static class DemoProfiles
    {
        internal const string PowerShellPath = @"C:\Users\Demo\Documents\PowerShell\Microsoft.PowerShell_profile.ps1";
        internal const string WindowsPowerShellPath = @"C:\Users\Demo\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1";

        internal static IReadOnlyList<(string Name, string Path)> All { get; } =
            new[] { ("PowerShell 7", PowerShellPath), ("Windows PowerShell", WindowsPowerShellPath) };

        internal static string? TextFor(string path)
        {
            bool modern = string.Equals(path, PowerShellPath, StringComparison.OrdinalIgnoreCase);
            if (!modern && !string.Equals(path, WindowsPowerShellPath, StringComparison.OrdinalIgnoreCase)) return null;
            return "# " + (modern ? "PowerShell 7" : "Windows PowerShell") + " profile for the demo workstation\r\n"
                + "# This sample is edited in memory and is never executed.\r\n\r\n"
                + "Set-PSReadLineOption -EditMode Windows\r\n"
                + (modern ? "Set-PSReadLineOption -PredictionSource History\r\n" : string.Empty)
                + "Set-Alias ll Get-ChildItem\r\n\r\n"
                + "function Get-WorkstationSummary {\r\n"
                + "    [pscustomobject]@{\r\n"
                + "        Computer = 'FAIRVIEW-WKS01'\r\n"
                + "        Site = 'Fairview'\r\n"
                + "        Role = 'Field technician'\r\n"
                + "    }\r\n"
                + "}\r\n\r\n"
                + "function prompt {\r\n"
                + "    'PS ' + $executionContext.SessionState.Path.CurrentLocation + '> '\r\n"
                + "}\r\n";
        }
    }
}
