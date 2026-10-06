using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KillerShell.Services
{
    internal static class WslDistributions
    {
        private static readonly string[] PathPrefixes = [@"\\wsl.localhost\", @"\\wsl$\"];

        internal static IReadOnlyList<string> Installed()
        {
            var names = new List<string>();
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
                foreach (string key in root?.GetSubKeyNames() ?? [])
                {
                    using var distro = root!.OpenSubKey(key);
                    if (distro?.GetValue("DistributionName") is string name && ValidName(name)) names.Add(name);
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return [.. names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }

        internal static bool ValidName(string name) => name.Length > 0 && name.IndexOfAny(['\\', '/', '"', '\r', '\n', '\0']) < 0;
        internal static string Root(string distro) => @"\\wsl.localhost\" + distro;

        internal static bool TryParsePath(string path, out string distro, out string linuxPath)
        {
            distro = linuxPath = string.Empty;
            string normalized = path.Replace('/', '\\');
            string? prefix = PathPrefixes
                .FirstOrDefault(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix == null) return false;
            string rest = normalized[prefix.Length..];
            int slash = rest.IndexOf('\\');
            distro = slash < 0 ? rest : rest[..slash];
            if (!ValidName(distro)) return false;
            linuxPath = slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
            return true;
        }

        internal static string ToLinuxPath(string? path, string distro)
        {
            if (string.IsNullOrWhiteSpace(path)) return "~";
            if (path!.StartsWith("/", StringComparison.Ordinal) || path == "~") return path;
            if (TryParsePath(path, out string owner, out string linux))
                return string.Equals(owner, distro, StringComparison.OrdinalIgnoreCase) ? linux : "~";
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
                return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            return "~";
        }

        internal static string ToWindowsPath(string distro, string linuxPath)
            => Root(distro) + (linuxPath.StartsWith("/", StringComparison.Ordinal) ? linuxPath.Replace('/', '\\') : "");

        internal static string BashQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

        internal static bool PathsEqual(string? left, string? right)
        {
            if (left == null || right == null) return left == right;
            if (TryParsePath(left, out string leftDistro, out string leftLinux)
                && TryParsePath(right, out string rightDistro, out string rightLinux))
                return string.Equals(leftDistro, rightDistro, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(leftLinux.TrimEnd('/'), rightLinux.TrimEnd('/'), StringComparison.Ordinal);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        // CommandLineToArgvW quoting also preserves trailing backslashes and embedded quotes.
        internal static string QuoteArgument(string value)
        {
            var result = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
