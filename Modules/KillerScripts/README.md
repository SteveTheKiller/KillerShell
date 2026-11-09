# KillerScripts

Always-latest launcher for the [killer-scripts](https://github.com/SteveTheKiller/killer-scripts) repo. Install it once and every tool is a command in any PowerShell session (5.1 and 7).

Each command does exactly what the one-liners on the website do. It allows scripts for the current process, downloads the latest copy of the tool from GitHub to `%TEMP%`, and runs it. Everything you type after the command goes to the tool unchanged.

## Install

```powershell
Install-Module KillerScripts -Scope CurrentUser
```

KillerShell ships this module and loads its own copy first in every terminal tab, so nothing needs installing there.

## Use

```powershell
Get-KillerScript            # list every tool with its description
urt                         # run the Universal Rename Tool (latest from the repo)
Invoke-URT                  # same thing, long form
amort -DryRun               # options go straight to the tool, short forms like -D included
Invoke-KillerScript URT     # generic form, tab-completes the name
```

Because these install as module commands, PowerShell auto-loads them. Typing `urt` in a fresh session just works, with no `Import-Module` and nothing in your profile.

## Notes

- To turn a switch off, leave it out. PowerShell drops the colon form of a switch, such as `-DryRun:$false`, when it forwards arguments, while a colon value such as `-RetainDays:7` passes through.
- PRINT.ps1 uses the alias `KillerPrint`, since a plain `print` alias would shadow `print.exe`. Short aliases are also only created when the name is free, so nothing built in is ever shadowed and the same guard covers any future clash.
- When you add or rename a script in the killer-scripts repo, refresh `descriptions.json` and the export lists in `KillerScripts.psd1`, then bump the version. The module tests fail until the two lists match.
