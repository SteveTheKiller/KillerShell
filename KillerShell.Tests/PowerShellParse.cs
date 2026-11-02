using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KillerShell.Tests;

/// <summary>Parses text with Windows PowerShell's own parser (Parser.ParseInput), the same parser a
/// typed command or a generated helper goes through, and reports what it read.</summary>
internal static class PowerShellParse
{
    internal sealed class Result
    {
        internal string? Error;
        internal List<string> Strings = new();
    }

    internal static Result Parse(string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "parse");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, text, new UTF8Encoding(true));
        try
        {
            string script = "$t=$null;$e=$null;"
                + "$ast=[System.Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText('" + file + "',[Text.Encoding]::UTF8),[ref]$t,[ref]$e);"
                + "if($e.Count -gt 0){[Console]::Out.WriteLine('E:'+$e[0].Message);exit 0};"
                + "foreach($n in $ast.FindAll({param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst]},$true)){"
                + "[Console]::Out.WriteLine('S:'+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($n.Value)))}";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60000));
            var result = new Result();
            foreach (string line in output.Split('\n'))
            {
                string row = line.Trim('\r');
                if (row.StartsWith("E:", StringComparison.Ordinal)) result.Error = row.Substring(2);
                else if (row.StartsWith("S:", StringComparison.Ordinal))
                    result.Strings.Add(Encoding.UTF8.GetString(Convert.FromBase64String(row.Substring(2))));
            }
            return result;
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
