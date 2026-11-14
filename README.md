<p align="center">
  <a href="https://killershell.net"><img src="docs/wordmark.png" height="180" alt="KillerShell - one portable Windows exe for managing a computer and everything on it"></a>
</p>

Free and open-source Windows shell for power users. A file browser, terminal, text editor, search engine, and administration toolkit share one window, one tab strip, and one set of keys.

KillerShell is one portable EXE with no agent, account, telemetry, or separate runtime installer. Run it from anywhere, install it for your account, or install it machine-wide for every user.

Full how-tos live on the [help page](https://killershell.net/help.html); architecture, formats, security decisions, and implementation details are on the [technical page](https://killershell.net/technical.html).

## Features

- Browse folders in list, icon, or details view with tabs, favorites, a folder tree, live filesystem updates, and two panes arranged side by side or stacked
- Search names with wildcards or stream file contents with 2 to 16 workers, with multiple ANY/ALL terms, filters, chained searches, and HTML or CSV export
- Use saved terminal profiles for PowerShell, CMD, WSL, or custom commands, choose a default shell, and track its working folder live; edit the themed PowerShell or Bash prompt, or your PowerShell profile
- Browse installed WSL distributions, pin Linux folders, and open a terminal in the selected Linux directory
- Edit text with syntax highlighting, find, go to line, undo/redo, encoding and line-ending preservation, indentation controls, and a dedicated font setting
- Copy, move, rename, recycle, permanently delete, and drag files to or from Explorer, with asynchronous progress and Replace, Skip, or Keep both collision handling
- Browse and extract ZIP, TAR, TAR.GZ, TGZ, and GZ archives; edit ZIP contents without changing the original when a rewrite is canceled
- Inspect and manage processes and services, including live CPU and memory sorting, filtering, ownership, restart, and elevation where needed
- Monitor CPU, RAM, disks, networks, and GPUs with live graphs and per-core CPU history
- Use the built-in Event Viewer, Registry Editor, and Storage Analyzer without leaving the tab strip
- Keyboard-first operation using familiar Explorer keys plus F4 storage, F7 edit, F8 shell, F9 processes, F10 split, F11 performance, and F1 for the complete shortcut overlay
- Read-only JSON CLI queries for files, processes, services, event logs, registry keys, drives, and folder sizes; run `KillerShell --cli --help` for commands
- Thirteen themes, including a full 98SE recreation; eight accents on Dark, Light, Black, and 98SE give 41 looks
- Localized in 20 languages, with live switching and English fallback for incomplete translations
- Runs portable or self-installs per-user without UAC or machine-wide with UAC; `/silent` supports WinGet and managed deployment
- Local-only: no indexing service, cloud account, advertisements, or telemetry

## Command line

The CLI is read-only. Data commands print JSON; `--help` and `--version` print plain text. Run it as `KillerShell --cli <verb>`, or `killershell <verb>` from an installed copy.

| Verb | What it does |
| --- | --- |
| `search <folder> --name <pattern> [--content <text>] [--limit 1..500] [--csv <new file>] [--html <new file>]` | Find files by name or content |
| `list <folder> [--limit 1..500]` | List a folder |
| `info <path>` | Size, dates and attributes |
| `read <file> [--max-chars 1..65536]` | Read a text file |
| `processes [--limit 1..500] [--detail] [--filter <text>]` | Running processes; `--detail` adds parent, path, command line and start time |
| `services [--limit 1..500] [--detail] [--filter <text>]` | Services; `--detail` adds start mode, path, account and description |
| `events <Application\|System\|Security> [--limit 1..100] [--filter <text>] [--level Critical\|Error\|Warning\|Information\|Verbose]` | Newest events first |
| `registry <hive\key> [--limit 1..100]` | Subkeys and values |
| `drives` | Drives and free space |
| `hash <file>` | SHA-256 |
| `storage <folder> [--limit 1..500] [--max-seconds 1..3600] [--html <new file>]` | The largest folders and files under a folder; `--max-seconds` stops a long scan and the report says `"truncated":true` |

`--version`, `-v` and `/version` print the version. Files named by `--csv` and `--html` must not exist yet; an existing file is refused with exit code 2 and nothing is written.

Exit codes: `0` succeeded (`--help` included), `1` the command failed, `2` bad or missing arguments.

The app itself also takes these switches: `--new-window` (a window of its own), `--cwd <folder>`, `--shell <kind>`, `--terminal-profile <token>`, `--edit <file>`, and `--processes`, `--eventviewer`, `--performance`, `--storage`, `--registry` to open that tool in a tab.

## Screenshots

<table>
<tr>
<td width="50%"><img src="shell-landing/screenshots/02.png" alt="Stacked panes with invoice search results and the file menu above Pictures, with a photo previewed in the details panel"><br><sub>Stack two panes, each with its own tabs and view, and preview the selected file in the details panel.</sub></td>
<td width="50%"><img src="shell-landing/screenshots/04.png" alt="Processes beside Registry Editor in side-by-side panes"><br><sub>Open Processes, Services and Registry Editor as tabs, side by side with anything else.</sub></td>
</tr>
</table>

## Requirements

- Windows 10 or 11 (x64)
- .NET Framework 4.8, included with every supported Windows release

## Download

WinGet:

```powershell
winget install SteveTheKiller.KillerShell
```

Chocolatey:

```powershell
choco install killershell
```

- Prebuilt binary: <https://github.com/SteveTheKiller/KillerShell/releases/latest/download/KillerShell.exe>
- Source (GPL3 corresponding source for each release): <https://github.com/SteveTheKiller/KillerShell/releases/latest>

## Build from source

Open `KillerShell.sln` in Visual Studio 2022 and build, or run:

```powershell
dotnet publish KillerShell.csproj -c Release
```

The app targets .NET Framework 4.8. AvalonEdit is vendored under `third_party/AvalonEdit` and compiled directly into KillerShell, so the published app remains one portable executable. `release.ps1` also produces the versioned GPL3 source archive shipped with each release.

## Translations

UI strings live in `Strings/`, one XAML `ResourceDictionary` per locale. To add or improve a language, see [TRANSLATING.md](TRANSLATING.md). Missing keys fall back to English, so a partial translation is welcome.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

GPLv3. See [LICENSE](LICENSE). If you fork, modify, or redistribute KillerShell, your version must also be released under GPLv3 with source available.

Bundled components retain their own licenses:

- AvalonEdit - MIT ([license](third_party/AvalonEdit/LICENSE))
- `Fonts/KillerGlyphs.ttf`, a Terminess Nerd Font subset - SIL OFL 1.1 ([notice](Fonts/KillerGlyphs-NOTICE.txt), [license](Fonts/KillerGlyphs-OFL.txt))
- 98SE icons from [Chicago95](https://github.com/grassmunk/Chicago95) - GPL-3.0+/MIT ([attribution](Resources/icons/98/ATTRIBUTION.md))
