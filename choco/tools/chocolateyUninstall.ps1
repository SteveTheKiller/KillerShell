$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'KillerShell'
$installExe = Join-Path $installDir 'KillerShell.exe'

if (Test-Path $installExe) {
    $uninstaller = Start-Process -FilePath $installExe -ArgumentList '/uninstall-silent' -Wait -PassThru -WindowStyle Hidden
    if ($uninstaller.ExitCode -ne 0) { throw "KillerShell uninstall failed with exit code $($uninstaller.ExitCode)." }
}
