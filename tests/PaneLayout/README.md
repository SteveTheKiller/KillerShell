# Pane layout regression

From the repository root, build the current app and run the isolated WPF fixture:

```powershell
dotnet build KillerShell.csproj --no-restore
powershell.exe -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File tests\PaneLayout\Run.ps1 -AssemblyPath "$PWD\bin\Debug\net48\KillerShell.exe"
```

The fixture uses demo state without showing a native window or launching a terminal process. It checks Malaise surface separation, both extreme pane ratios, sidebar widths, accessibility scale, frame and host margins, focus switching, single and stacked minima, queued resize callbacks, unchanged-layout scheduling, and interrupted open/close animations through the WPF dispatcher.

This verifies compiled layout and animation state. Native mouse resizing, monitor transitions, and the reported intermittent divider pixels still require visual confirmation.
