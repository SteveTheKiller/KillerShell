# Read-only product validation. Only owned scratch fixtures are created or removed.
param(
    [string]$ModuleRoot = $PSScriptRoot,
    [string]$FixtureRoot = $PSScriptRoot,
    [string]$ScratchRoot = [IO.Path]::GetTempPath()
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerModules-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
$hostPath = (Get-Process -Id $PID).Path
try {
    $env:TEMP = $root
    $env:TMP = $root
    $cases = @(
        @('KillerPivot','DisconnectSafety'),
        @('KillerPivot','ConnectSafety'),
        @('KillerPivot','ConnectionIdentity'),
        @('KillerPivot','ConfigurationSafety'),
        @('KillerScripts','EncodingSafety'),
        @('KillerScripts','CacheSafety'),
        @('KillerScripts','SnapshotSafety'),
        @('KillerScripts','ConcurrentSafety')
    )
    foreach ($case in $cases) {
        $fixture = Join-Path $FixtureRoot ($case[0] + '\Tests\' + $case[1] + '.ps1')
        $module = Join-Path $ModuleRoot ($case[0] + '\' + $case[0] + '.psd1')
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$fixture,'-ModulePath',$module)
        if ($case[1] -ne 'DisconnectSafety') { $arguments += @('-ScratchRoot',$root) }
        & $hostPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "Module fixture failed: $($case -join '/') (exit $LASTEXITCODE)." }
    }
    Write-Output "PASS all 8 module fixtures on PowerShell $($PSVersionTable.PSVersion)"
} finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    Remove-Item -LiteralPath $root -Recurse -Force
}
