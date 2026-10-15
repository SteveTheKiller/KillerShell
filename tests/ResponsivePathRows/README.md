# Responsive pane path rows

Build the app, then run the compiled WPF fixture without displaying a native window or launching a terminal process:

```powershell
dotnet build KillerShell.csproj --no-restore
powershell.exe -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File tests\ResponsivePathRows\Run.ps1 -AssemblyPath "$PWD\bin\Debug\net48\KillerShell.exe" -CaptureDirectory "$env:TEMP\KillerShell-ResponsivePathRows"
```

The fixture checks single, dual and stacked panes; file and terminal paths; compact and wide geometry; repeated threshold crossings and hysteresis; three themes, three locales and four accessibility scales; original command instances; address edit text, selection and logical focus; nested flyout anchor references; and terminal bar restoration. It saves offscreen PNG renders using fabricated paths.

No native window is shown. Physical keyboard focus, actual dropdown placement, pointer interaction and Escape delivery require interactive verification. The fixture checks logical focus and anchor references without claiming those native behaviors.
