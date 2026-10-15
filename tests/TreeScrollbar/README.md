# Tree scrollbar regression check

Build the current app, then run in a fresh Windows PowerShell STA process:

```powershell
dotnet build KillerShell.csproj --no-restore
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests\TreeScrollbar\Run.ps1 -AssemblyPath "$PWD\bin\Debug\net48\KillerShell.exe" -CaptureDirectory "$env:TEMP\KillerShell-TreeScrollbar"
```

The check lays out the actual window content without showing a native window. It covers wide tree overflow, both scrollbar orientations, the full idle thumb hit area, simulated hover and drag/leave states, pixel equality of scrollbar regions with content fades enabled and disabled, paging commands, drag direction, resizing and scrollbar appearance/disappearance. Malaise and Light expand from 4 to 8 px inside the unchanged 12 px hit track. 98SE retains its 16 px beveled thumb and no glow.

PNG captures are offscreen WPF renders. Read-only hover and drag dependency properties are set explicitly for trigger verification. This does not verify native pointer capture, mouse delivery or physical keyboard navigation; those require an interactive check.
