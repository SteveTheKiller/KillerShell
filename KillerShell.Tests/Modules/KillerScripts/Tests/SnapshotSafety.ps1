param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'),
    [string]$ScratchRoot = [IO.Path]::GetTempPath(),
    [string]$WorkerRoot
)
$ErrorActionPreference = 'Stop'
if ($WorkerRoot) {
    $module = Import-Module $ModulePath -Force -PassThru
    & $module { param($root) $script:CacheDir = $root } $WorkerRoot
    try {
        $actual = Invoke-KillerScript fixture -Offline -ScriptArgs @($WorkerRoot)
        [IO.File]::WriteAllText((Join-Path $WorkerRoot 'result'), $actual)
    } catch { [IO.File]::WriteAllText((Join-Path $WorkerRoot 'result'), $_.Exception.Message); exit 1 }
    exit
}
$root = Join-Path $ScratchRoot ('KillerScripts-snapshot-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$module = Import-Module $ModulePath -Force -PassThru
& $module { param($root) $script:CacheDir = $root } $root
$global:KsSnapshotBody = @'
param([string]$GateRoot)
if ($PSScriptRoot -ne $GateRoot) { throw 'The script root changed.' }
$before = [IO.File]::ReadAllText($PSCommandPath)
[IO.File]::WriteAllText((Join-Path $GateRoot 'ready'), 'ready')
$deadline = [DateTime]::UtcNow.AddSeconds(45)
while (-not [IO.File]::Exists((Join-Path $GateRoot 'release'))) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Refresh did not complete during script execution.' }
    Start-Sleep -Milliseconds 20
}
if ([IO.File]::ReadAllText($PSCommandPath) -cne $before) { throw 'The executing script changed during cache refresh.' }
'original snapshot'
'@
function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($Uri -like '*descriptions.json') { return [pscustomobject]@{ Content = '{"fixture.ps1":{"name":"Fixture"}}' } }
    [pscustomobject]@{ Content = $global:KsSnapshotBody }
}
$child = $null
try {
    Update-KillerScripts
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ModulePath "{1}" -WorkerRoot "{2}"' -f $PSCommandPath,([IO.Path]::GetFullPath($ModulePath)),$root
    $child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'worker.stdout') -RedirectStandardError (Join-Path $root 'worker.stderr')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not [IO.File]::Exists((Join-Path $root 'ready'))) {
        if ($child.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Script worker did not reach the execution barrier.' }
        Start-Sleep -Milliseconds 20
    }
    $global:KsSnapshotBody = "'complete replacement'"
    Update-KillerScripts
    [IO.File]::WriteAllText((Join-Path $root 'release'), 'release')
    if (-not $child.WaitForExit(15000)) { throw 'Script worker did not finish.' }
    $actual = [IO.File]::ReadAllText((Join-Path $root 'result'))
    if ($actual -cne 'original snapshot') { throw "Invocation did not retain its complete snapshot: $actual" }
    if ((Invoke-KillerScript fixture -Offline) -cne 'complete replacement') { throw 'Concurrent refresh did not publish.' }
    Write-Output 'PASS separate-process refresh completes during stable script execution and preserves script arguments/root'
} finally {
    if ($child) {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    Remove-Module $module -Force
    Remove-Item Function:\global:Invoke-WebRequest
    Remove-Variable KsSnapshotBody -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
