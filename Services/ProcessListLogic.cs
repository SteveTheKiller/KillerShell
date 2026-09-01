using System;
using KillerShell.Models;

namespace KillerShell.Services
{
    internal static class ProcessListLogic
    {
        internal static bool Matches(ProcessInfo process, string query)
            => query.Length == 0
               || Contains(process.Name, query)
               || Contains(process.Path, query)
               || Contains(process.User, query);

        internal static bool Matches(ServiceInfo service, string query)
            => query.Length == 0
               || Contains(service.Name, query)
               || Contains(service.DisplayName, query)
               || Contains(service.Path, query)
               || Contains(service.LogOnAs, query);

        internal static string FriendlyStartMode(string raw) => raw switch
        {
            "Auto" => "Automatic",
            "Manual" => "Manual",
            "Disabled" => "Disabled",
            "Boot" => "Boot",
            "System" => "System",
            _ => raw,
        };

        internal static string ExtractExePath(string pathName)
        {
            if (string.IsNullOrEmpty(pathName)) return string.Empty;
            string value = pathName.Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                return end > 0 ? value[1..end] : value.Trim('"');
            }

            int space = value.IndexOf(' ');
            return space > 0 ? value[..space] : value;
        }

        internal static string ExtractArguments(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return string.Empty;

            string rest = commandLine;
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = rest.IndexOf('"', 1);
                rest = end > 0 ? rest[(end + 1)..] : string.Empty;
            }
            else
            {
                int space = rest.IndexOf(' ');
                rest = space > 0 ? rest[(space + 1)..] : string.Empty;
            }

            return rest.Trim();
        }

        private static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
