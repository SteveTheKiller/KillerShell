using System;
using System.Collections.Generic;
using System.Linq;
using KillerShell.Models;

// --demo / /demo: fabricated tabs and results for marketing screenshots, so captures
// never leak real file names or folder structures. Also hides the install badge, and
// makes the About card render its signed state (publisher, thumbprint and the AKA line -
// see About.cs) so a capture from an unsigned local build matches the released one.
// Partial of MainWindow (KillerScan's DemoMode.cs pattern).
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        public static bool DemoMode;

        private static readonly Random DemoRng = new(1337);   // same data every run

        private void GenerateDemoData()
        {
            var placeholders = _tabs.ToList();   // the blank startup tab(s)

            // ── Tab 1: classic name search - a year of invoices ──────────────────
            var t1 = CreateTab();
            t1.Title    = "~\\Documents";
            t1.RootPath = @"C:\Users\steve\Documents";
            t1.Groups[0].Terms[0].Pattern = "invoice";
            t1.QueryLabel = "name: invoice";
            string inv = @"C:\Users\steve\Documents\Invoices";
            for (int m = 0; m < 12; m++)
            {
                var date = new DateTime(2025, 7, 1).AddMonths(m).AddDays(DemoRng.Next(3, 25));
                AddDemoResult(t1, inv, $"invoice_{date:yyyy-MM}.pdf", 40 + DemoRng.Next(220), date);
            }
            AddDemoResult(t1, @"C:\Users\steve\Documents", "invoice_template.docx", 28, new DateTime(2025, 9, 3));
            AddDemoResult(t1, @"C:\Users\steve\Documents\Archive", "old invoices.zip", 4096 + DemoRng.Next(2048), new DateTime(2024, 12, 30));
            FinishDemoTab(t1, 8412, 1.87);

            // ── Tab 2: content search with a filter row showing ──────────────────
            var t2 = CreateTab();
            t2.Title    = "~\\code";
            t2.RootPath = @"C:\Users\steve\code";
            t2.Groups[0].Terms[0].Mode    = SearchTerm.SearchMode.Content;
            t2.Groups[0].Terms[0].Pattern = "TODO";
            t2.Filters.Add(new SearchFilter { FieldIndex = SearchFilter.FieldExt, Text = "ps1" });
            t2.QueryLabel = "content: TODO  |  extension is ps1";
            string scripts = @"C:\Users\steve\code\killer-scripts";
            AddDemoResult(t2, scripts, "Backup-Nightly.ps1", 12, new DateTime(2026, 5, 14), new List<LineMatch>
            {
                // These two line numbers are not arbitrary: tab 6 opens this same file in the
                // editor, and DemoScript() puts those comments on exactly these lines. A capture
                // with the search results and the open document side by side would otherwise
                // show them disagreeing about where the hits are.
                new() { LineNumber = 44,  LineText = "# TODO: skip locked files instead of retrying forever" },
                new() { LineNumber = 110, LineText = "# TODO: email the report when the share is unreachable" },
            });
            AddDemoResult(t2, scripts, "Deploy-Agent.ps1", 9, new DateTime(2026, 6, 2), new List<LineMatch>
            {
                new() { LineNumber = 77, LineText = "# TODO: pull the tenant list from the API" },
            });
            AddDemoResult(t2, scripts, "Get-StaleProfiles.ps1", 6, new DateTime(2026, 3, 21), new List<LineMatch>
            {
                new() { LineNumber = 14, LineText = "# TODO: exclude service accounts" },
                new() { LineNumber = 31, LineText = "# TODO: make the age threshold a parameter" },
            });
            AddDemoResult(t2, @"C:\Users\steve\code\homelab", "Rotate-Certs.ps1", 8, new DateTime(2026, 1, 9), new List<LineMatch>
            {
                new() { LineNumber = 5, LineText = "# TODO: wire up the renewal webhook" },
            });
            if (t2.Results.Count > 0) t2.Results[0].IsExpanded = true;   // show off line matches
            FinishDemoTab(t2, 23907, 4.32);

            // ── Tab 3: a piped search - drill into tab 1's results ───────────────
            var t3 = CreateTab();
            t3.Title     = "~\\Documents > invoice";
            t3.RootPath  = t1.RootPath;
            t3.PipeFiles = t1.Results.Select(r => r.FilePath).ToList();
            t3.PipeArgs  = [t1.Results.Count.ToString("N0"), t1.Title, "name: invoice"];
            t3.PipeLabel = string.Format(Loc("Str_Pipe_Scope"), t3.PipeArgs);
            t3.Groups[0].Terms[0].Pattern = "2026";
            t3.QueryLabel = "name: 2026";
            foreach (var r in t1.Results.Where(r => r.FileName.Contains("2026")))
                AddDemoResult(t3, r.Directory, r.FileName, (int)(r.SizeBytes / 1024), r.Modified);
            FinishDemoTab(t3, t1.Results.Count, 0.02);

            // ── Tab 4: a browsed folder, for the icon view ───────────────────────
            // The rows come from the same fabricated machine the tree and This PC are reading
            // (DemoFileSystem.cs), so a capture with the sidebar open agrees with the listing.
            // Documents is the folder chosen for it: four subfolders and a dozen different
            // extensions, so no two tiles in the icon view draw the same glyph.
            //
            // No NavigateTo call - that would record history, retitle the pane, repoint the tree
            // and fire the watcher against a tab that is not even active yet. The listing is
            // built straight onto the tab instead, mirroring what NavigateTo does to one.
            var t4 = CreateTab();
            string docs = @"C:\Users\steve\Documents";
            t4.Title         = "Documents";
            t4.IsBrowsing    = true;
            t4.CurrentFolder = docs;
            t4.RootPath      = docs;
            t4.History.Add(docs);
            t4.HistoryIndex  = 0;
            foreach (var e in DemoFs.Children(docs))
                t4.Results.Add(new SearchResult
                {
                    FilePath    = System.IO.Path.Combine(docs, e.Name),
                    FileName    = e.Name,
                    Directory   = docs,
                    IsDirectory = e.IsDir,
                    SizeBytes   = e.IsDir ? 0 : e.Size,
                    Modified    = e.Modified,
                    Seq         = t4.Results.Count,
                });
            ApplySort(t4);     // Results.cs - folders-first is added there while browsing
            ApplyFilter(t4);
            SetTabStatusKey(t4, "Str_Status_Listed", t4.Results.Count.ToString("N0"));

            // ── Tab 5: a shell, canned ───────────────────────────────────────────
            CreateDemoTerminalTab(@"C:\Users\steve\code\killer-scripts",   // TerminalTabs.cs
                                  DemoTerminalSession());

            // ── Tab 6: a document, so the highlighting is in the set ─────────────
            // Backup-Nightly.ps1 rather than any other file: the content search on tab 2 already
            // shows two TODO hits inside it and the shell on tab 5 has it modified in git status,
            // so opening THAT file is what a reader would do next. A .ps1 also earns its place -
            // PowerShell is the language a field tech reads, and its highlighting is the one
            // worth photographing.
            CreateEditorTab(@"C:\Users\steve\code\killer-scripts\Backup-Nightly.ps1",
                            DemoScript());                                 // EditorTabs.cs

            foreach (var old in placeholders) _tabs.Remove(old);
            UpdateTabBar();
            ActivateTab(t1);
        }

        private void AddDemoResult(SearchTab t, string folder, string name, int sizeKb,
                                   DateTime modified, List<LineMatch>? lines = null)
        {
            var term = t.Groups[0].Terms[0];
            var r = new SearchResult
            {
                FileName  = name,
                Directory = folder,
                FilePath  = System.IO.Path.Combine(folder, name),
                SizeBytes = sizeKb * 1024L + DemoRng.Next(1024),
                Modified  = modified,
                Seq       = t.Results.Count,
            };
            r.Matches.Add(new TermMatch { Term = term, Lines = lines ?? new List<LineMatch>() });
            if (term.MatchCount < 0) term.MatchCount = 0;
            term.MatchCount += lines is { Count: > 0 } ? lines.Count : 1;
            t.Results.Add(r);
        }

        private void FinishDemoTab(SearchTab t, int scanned, double seconds)
        {
            t.ScannedCount  = scanned;
            t.StatusKey     = "Str_Status_Done";
            t.StatusArgs    = [seconds.ToString("0.00"), t.Results.Count];
            t.StatsLabel    = string.Format(Loc("Str_Count_Matches"), t.Results.Count.ToString("N0"));
            t.ScannedLabel  = string.Format(Loc("Str_Status_Scanned"), scanned.ToString("N0"));
            t.StatusMessage = string.Format(Loc("Str_Status_Done"),
                seconds.ToString("0.00"), t.Results.Count);
        }

        // The canned shell session, fed straight to the parser by StartDemo
        // (Terminal\TerminalControl.cs) with no process behind it.
        //
        // Written as real ANSI because that is the whole point: the shipped prompt is one of the
        // things worth photographing, and it only looks like itself in color. The palette below
        // is the fallback palette in Terminal\KillerPrompt.ps1, and the glyphs are the ones that
        // script draws with - the powerline separator, the branch mark, the chevron you type at,
        // the dirty sign and the ahead arrow. The typed lines are colored the way PSReadLine
        // colors them, command name apart from its parameters.
        //
        // Escapes are built from codepoints, the same convention TerminalControl uses for its
        // own: a literal escape byte in a source file is invisible in an editor, cannot be
        // grepped, and does not survive an encoding round trip.
        //
        // Exactly 25 rows, which is the height of a terminal buffer before the control has been
        // laid out - so nothing has scrolled off the top by the time the tab is first drawn.
        private static string DemoTerminalSession()
        {
            string esc    = ((char)0x1B).ToString();
            string reset  = esc + "[0m";
            string accent = esc + "[38;2;232;72;90m";     // ACCENT #e8485a
            string fg     = esc + "[38;2;255;253;232m";   // FG     #fffde8
            string dim    = esc + "[38;2;226;181;138m";   // DIM    #e2b58a
            string ok     = esc + "[38;2;92;184;92m";     // OK     #5cb85c
            string warn   = esc + "[38;2;232;180;92m";    // WARN   #e8b45c
            string onAcc  = esc + "[48;2;232;72;90m";     // the accent block behind the path

            string sep   = ((char)0xE0B0).ToString();     // powerline right arrow
            string mark  = ((char)0xE0A0).ToString();     // git branch
            string chev  = ((char)0x276F).ToString();     // heavy right angle
            string dirty = ((char)0x00B1).ToString();
            string up    = ((char)0x2191).ToString();

            // Two rows, the way the prompt function writes them: the accent block, the taper and
            // the branch on one, the mark you type at on the next, so typed text always starts at
            // the same column. The branch is drawn in WARN rather than OK because the tree is
            // dirty, which is the rule in the script.
            string prompt = onAcc + fg + @" ~\code\killer-scripts " + reset + accent + sep + reset
                          + " " + warn + mark + " main" + reset + warn + " " + dirty + reset
                          + dim + " " + up + "2" + reset + "\r\n"
                          + accent + chev + " " + reset;

            var s = new System.Text.StringBuilder();

            s.Append(prompt);
            s.Append(warn + "git" + reset + " status " + dim + "-sb" + reset + "\r\n");
            s.Append("## " + ok + "main" + reset + "..." + accent + "origin/main" + reset
                   + " [ahead 2]\r\n");
            s.Append(warn + " M" + reset + " Backup-Nightly.ps1\r\n");
            s.Append(accent + "??" + reset + " Test-BackupRestore.ps1\r\n");
            s.Append("\r\n");

            // Get-KillerScript is real: it ships inside the exe as an embedded module
            // (Modules\KillerScripts) and is published into every shell KillerShell opens, so a
            // reader who types it after seeing this gets the same table back. Fabricating a
            // command that does not exist would be the one lie in an otherwise honest capture.
            s.Append(prompt);
            s.Append(warn + "Get-KillerScript" + reset + " " + dim + "|" + reset + " "
                   + warn + "Format-Table" + reset + " Script, Name " + dim + "-AutoSize" + reset
                   + "\r\n");
            s.Append("\r\n");
            // Padded to the widest script name, the way Format-Table -AutoSize would.
            s.Append("Script                    Name\r\n");
            s.Append("------                    ----\r\n");
            s.Append("Clear-DiskSpace.ps1       Reclaim disk on a full system drive\r\n");
            s.Append("Get-StaleProfiles.ps1     List profiles nobody has signed into\r\n");
            s.Append("Repair-WindowsUpdate.ps1  Reset the Windows Update stack\r\n");
            s.Append("Test-Connectivity.ps1     Check DNS, gateway and internet in one pass\r\n");
            s.Append("\r\n");

            // The three files this lists, and their sizes, are the three the fabricated Logs
            // folder holds (DemoFileSystem.cs). A shell and a listing side by side that disagreed
            // about the same folder is exactly the kind of detail a screenshot gets caught on.
            s.Append(prompt);
            s.Append(warn + "Get-ChildItem" + reset + @" ~\Logs\agent-*.log " + dim + "|" + reset
                   + " " + warn + "Select-Object" + reset + " Name, Length\r\n");
            s.Append("\r\n");
            s.Append("Name                 Length\r\n");
            s.Append("----                 ------\r\n");
            s.Append("agent-2026-06-27.log 274432\r\n");
            s.Append("agent-2026-06-28.log 298496\r\n");
            s.Append("agent-2026-06-29.log 311296\r\n");
            s.Append("\r\n");

            // Ends on a fresh prompt, so the cursor is sitting where a reader expects it.
            s.Append(prompt);
            return s.ToString();
        }

        /// <summary>
        /// A plausible body for a fabricated file, chosen by extension, so F7 on anything in the
        /// demo results opens a document that looks like what its name promises.
        /// </summary>
        /// <remarks>
        /// Without this, Edit in --demo could only ever have shown an empty buffer: there is no
        /// file behind any of these paths. Each body is picked to light up its own highlighter,
        /// which is the reason to open one of these in a screenshot at all.
        /// </remarks>
        private static string DemoTextFor(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".ps1":
                case ".psm1":
                case ".psd1":
                    return DemoScript();
                case ".log":
                case ".err":
                case ".out":
                case ".trace":
                    return DemoLog();
                case ".reg":
                    return DemoReg();
                default:
                    // Something honest rather than a fake body for a type we have not written
                    // one for. A blank tab would read as a bug in the editor.
                    return "This file is part of KillerShell's --demo data." + "\r\n"
                         + "It has no contents on disk - the whole machine is invented." + "\r\n";
            }
        }

        private static string DemoLog()
        {
            var s = new System.Text.StringBuilder();
            void L(string t) => s.Append(t).Append("\r\n");
            L("2026-06-29 22:00:01 INFO   agent starting, version 4.11.2");
            L("2026-06-29 22:00:01 INFO   tenant 8f2c1d4a-77b0-4e19-9c3e-5a1f0b6d2e88");
            L("2026-06-29 22:00:03 INFO   inventory sweep started");
            L("2026-06-29 22:00:19 INFO   142 devices enumerated");
            L("2026-06-29 22:01:04 WARN   WMI query timed out on WKS-0421, retrying");
            L("2026-06-29 22:01:34 WARN   WMI query timed out on WKS-0421, retrying");
            L("2026-06-29 22:02:04 ERROR  WKS-0421 unreachable after 3 attempts");
            L("2026-06-29 22:02:05 INFO   patch scan started");
            L("2026-06-29 22:06:41 INFO   patch scan complete, 38 devices missing updates");
            L("2026-06-29 22:06:42 INFO   uploading results to https://rmm.example.net/ingest");
            L("2026-06-29 22:06:58 ERROR  upload failed: 0x80072EE2 (timeout)");
            L("2026-06-29 22:07:28 INFO   retry 1 of 3");
            L("2026-06-29 22:07:44 INFO   upload succeeded, 0x00000000");
            L("2026-06-29 22:07:44 INFO   next run 2026-06-30 22:00:00");
            L("2026-06-29 22:07:44 INFO   agent idle");
            return s.ToString();
        }

        private static string DemoReg()
        {
            var s = new System.Text.StringBuilder();
            void L(string t) => s.Append(t).Append("\r\n");
            L("Windows Registry Editor Version 5.00");
            L("");
            L("; Field default: show file extensions and hidden files on a new build.");
            L("");
            L(@"[HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced]");
            L("\"HideFileExt\"=dword:00000000");
            L("\"Hidden\"=dword:00000001");
            L("\"ShowSuperHidden\"=dword:00000000");
            L("");
            L("; Removing the stub the old agent left behind.");
            L(@"[-HKEY_LOCAL_MACHINE\SOFTWARE\OldVendor\Agent]");
            L("");
            L(@"[HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools\Deploy]");
            L("\"LastRun\"=\"2026-06-29\"");
            L("\"Channel\"=\"stable\"");
            L("\"Retries\"=dword:00000003");
            L("\"Legacy\"=-");
            return s.ToString();
        }

        // The document tab's contents: Backup-Nightly.ps1 as the fabricated machine has it.
        //
        // Written to exercise the highlighter rather than to be a good backup script - comments,
        // here-strings, single and double quoted strings, variables, splatting, cmdlet names,
        // parameters, operators, numbers and a try/catch all appear in the first screenful,
        // which is the part that gets photographed.
        //
        // The two TODO lines are deliberately on lines 42 and 118, because tab 2's content
        // search reports hits at exactly those line numbers in exactly this file. A reader who
        // opens the file after reading the search results would otherwise catch them disagreeing.
        private static string DemoScript()
        {
            var s = new System.Text.StringBuilder();
            void L(string line = "") => s.Append(line).Append("\r\n");

            L("<#");
            L(".SYNOPSIS");
            L("    Nightly backup of the client's working shares to the NAS.");
            L(".DESCRIPTION");
            L("    Runs from Task Scheduler at 23:00. Mirrors each share with robocopy, keeps");
            L("    fourteen days of logs, and writes a one line summary the RMM can alert on.");
            L(".NOTES");
            L("    Steve the Killer  -  killertools.net");
            L("#>");
            L("[CmdletBinding()]");
            L("param(");
            L("    [Parameter(Mandatory)]");
            L("    [string]$Destination,");
            L("");
            L("    [int]$KeepDays = 14,");
            L("");
            L("    [switch]$WhatIf");
            L(")");
            L("");
            L("Set-StrictMode -Version Latest");
            L("$ErrorActionPreference = 'Stop'");
            L("");
            L("$LogRoot = Join-Path $env:ProgramData 'KillerScripts\\backup'");
            L("$Stamp   = Get-Date -Format 'yyyy-MM-dd'");
            L("$LogFile = Join-Path $LogRoot \"backup-$Stamp.log\"");
            L("");
            L("$Shares = @(");
            L("    'D:\\Shares\\Accounts'");
            L("    'D:\\Shares\\Projects'");
            L("    'D:\\Shares\\Scans'");
            L(")");
            L("");
            L("function Write-Log {");
            L("    param([string]$Message, [string]$Level = 'INFO')");
            L("");
            L("    $line = '{0}  {1,-5}  {2}' -f (Get-Date -Format 's'), $Level, $Message");
            L("    Add-Content -LiteralPath $LogFile -Value $line -Encoding utf8");
            L("    Write-Verbose $line");
            L("}");
            L("");
            L("function Invoke-Mirror {");
            L("    param([string]$Source, [string]$Target)");
            L("");
            L("    # TODO: skip locked files instead of retrying forever");                 // line 42
            L("    $args = @(");
            L("        $Source, $Target");
            L("        '/MIR', '/FFT', '/Z', '/NP'");
            L("        '/R:2', '/W:5'");
            L("        '/LOG+:' + $LogFile");
            L("    )");
            L("");
            L("    if ($WhatIf) {");
            L("        Write-Log \"WHATIF robocopy $Source -> $Target\"");
            L("        return 0");
            L("    }");
            L("");
            L("    $null = & robocopy.exe @args");
            L("    return $LASTEXITCODE");
            L("}");
            L("");
            L("function Remove-OldLogs {");
            L("    $cutoff = (Get-Date).AddDays(-$KeepDays)");
            L("    Get-ChildItem -LiteralPath $LogRoot -Filter 'backup-*.log' |");
            L("        Where-Object { $_.LastWriteTime -lt $cutoff } |");
            L("        Remove-Item -Force");
            L("}");
            L("");
            L("# ---------------------------------------------------------------");
            L("#  Main");
            L("# ---------------------------------------------------------------");
            L("");
            L("if (-not (Test-Path -LiteralPath $LogRoot)) {");
            L("    $null = New-Item -ItemType Directory -Path $LogRoot -Force");
            L("}");
            L("");
            L("if (-not (Test-Path -LiteralPath $Destination)) {");
            L("    throw \"Destination $Destination is not reachable - is the NAS awake?\"");
            L("}");
            L("");
            L("Write-Log \"Starting nightly backup to $Destination\"");
            L("");
            L("$failed  = 0");
            L("$started = Get-Date");
            L("");
            L("foreach ($share in $Shares) {");
            L("    $leaf   = Split-Path $share -Leaf");
            L("    $target = Join-Path $Destination $leaf");
            L("");
            L("    try {");
            L("        $code = Invoke-Mirror -Source $share -Target $target");
            L("");
            L("        # robocopy uses 0-7 for success, 8 and up for a real failure");
            L("        if ($code -ge 8) {");
            L("            $failed++");
            L("            Write-Log \"$leaf failed with exit code $code\" 'ERROR'");
            L("        }");
            L("        else {");
            L("            Write-Log \"$leaf mirrored (exit $code)\"");
            L("        }");
            L("    }");
            L("    catch {");
            L("        $failed++");
            L("        Write-Log $_.Exception.Message 'ERROR'");
            L("    }");
            L("}");
            L("");
            L("Remove-OldLogs");
            L("");
            L("$elapsed = (Get-Date) - $started");
            L("# TODO: email the report when the share is unreachable");                   // line 118
            L("$summary = 'Backup finished in {0:hh\\:mm\\:ss}, {1} share(s) failed' -f $elapsed, $failed");
            L("");
            L("Write-Log $summary");
            L("Write-Output $summary");
            L("");
            L("exit $failed");

            return s.ToString();
        }
    }
}

