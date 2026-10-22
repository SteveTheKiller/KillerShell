# Guest-only public-command validation. Creates and removes only owned scratch fixtures.
param(
    [Parameter(Mandatory = $true)][string]$ModulePath,
    [string]$ScratchRoot = [IO.Path]::GetTempPath(),
    [string]$WorkerRoot
)
$ErrorActionPreference = 'Stop'
if ([Environment]::MachineName -ieq 'SHREDDER' -or
    [string]::IsNullOrWhiteSpace($env:KILLERSHELL_DISPOSABLE_VM) -or
    $env:KILLERSHELL_DISPOSABLE_VM -ine [Environment]::MachineName) {
    throw 'This fixture requires an acknowledged disposable Hyper-V guest.'
}
$bios = Get-ItemProperty -LiteralPath 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
if ($bios.SystemManufacturer -ine 'Microsoft Corporation' -or $bios.SystemProductName -ine 'Virtual Machine') {
    throw 'This fixture requires Hyper-V guest firmware identity.'
}

$fixtureBody = @'
param([switch]$Detached)
if ($PSScriptRoot -cne $env:KS_LIFETIME_CACHE_ROOT) { throw 'The script root changed.' }
if ($Detached) {
    'original detached version'
    return
}
$bootstrap = @"
`$ErrorActionPreference = 'Stop'
`$ready = [Threading.EventWaitHandle]::OpenExisting(`$env:KS_LIFETIME_EVENT_PREFIX + '-ready')
`$release = [Threading.EventWaitHandle]::OpenExisting(`$env:KS_LIFETIME_EVENT_PREFIX + '-release')
`$finished = [Threading.EventWaitHandle]::OpenExisting(`$env:KS_LIFETIME_EVENT_PREFIX + '-finished')
try {
    [IO.File]::WriteAllText((Join-Path `$env:KS_LIFETIME_CACHE_ROOT 'child.pid'), [string]`$PID)
    `$ready.Set() | Out-Null
    if (-not `$release.WaitOne(30000)) { throw 'The detached loader was not released.' }
    `$snapshot = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__SNAPSHOT__'))
    [IO.File]::WriteAllText((Join-Path `$env:KS_LIFETIME_CACHE_ROOT 'loaded-content.ps1'), [IO.File]::ReadAllText(`$snapshot))
    `$actual = & `$snapshot -Detached
    [IO.File]::WriteAllText((Join-Path `$env:KS_LIFETIME_CACHE_ROOT 'result'), [string]`$actual)
} catch {
    [IO.File]::WriteAllText((Join-Path `$env:KS_LIFETIME_CACHE_ROOT 'failure'), `$_.Exception.ToString())
    exit 1
} finally {
    `$finished.Set() | Out-Null
    `$ready.Dispose()
    `$release.Dispose()
    `$finished.Dispose()
}
"@
$bootstrap = $bootstrap.Replace('__SNAPSHOT__', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($PSCommandPath)))
$arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
$child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $env:KS_LIFETIME_CACHE_ROOT 'child.stdout') -RedirectStandardError (Join-Path $env:KS_LIFETIME_CACHE_ROOT 'child.stderr')
$child.Dispose()
if ($env:KS_LIFETIME_COMPLETION_MODE -eq 'exit') { exit 0 }
return
'@

function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($Uri -eq 'https://raw.githubusercontent.com/SteveTheKiller/killer-scripts/main/descriptions.json') {
        return [pscustomobject]@{ Content = '{"fixture.ps1":{"name":"Fixture"}}' }
    }
    if ($Uri -ne 'https://raw.githubusercontent.com/SteveTheKiller/killer-scripts/main/fixture.ps1') {
        throw "Unexpected fixture download: $Uri"
    }
    [pscustomobject]@{ Content = $global:KsLifetimeBody }
}

if ($WorkerRoot) {
    $module = Import-Module $ModulePath -Force -PassThru
    & $module { param($root) $script:CacheDir = $root } $WorkerRoot
    $global:KsLifetimeBody = $fixtureBody
    Invoke-KillerScript fixture
    exit 0
}

$scratchBoundary = [IO.Path]::GetFullPath($ScratchRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$root = [IO.Path]::GetFullPath((Join-Path $scratchBoundary ('detached-lifetime-' + [guid]::NewGuid().ToString('N'))))
[IO.Directory]::CreateDirectory($root) | Out-Null
$module = Import-Module $ModulePath -Force -PassThru
$savedEnvironment = @{}
foreach ($name in 'KS_LIFETIME_CACHE_ROOT','KS_LIFETIME_EVENT_PREFIX','KS_LIFETIME_COMPLETION_MODE') {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$fixtureFailure = $null
try {
    foreach ($mode in 'return','exit') {
        $caseRoot = Join-Path $root $mode
        [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        & $module { param($path) $script:CacheDir = $path } $caseRoot
        $env:KS_LIFETIME_CACHE_ROOT = $caseRoot
        $env:KS_LIFETIME_EVENT_PREFIX = 'Local\KillerScripts-' + [guid]::NewGuid().ToString('N')
        $env:KS_LIFETIME_COMPLETION_MODE = $mode
        $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $env:KS_LIFETIME_EVENT_PREFIX + '-ready')
        $release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $env:KS_LIFETIME_EVENT_PREFIX + '-release')
        $finished = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $env:KS_LIFETIME_EVENT_PREFIX + '-finished')
        $parent = $null
        $child = $null
        $caseFailure = $null
        try {
            $sentinel = Join-Path $caseRoot '.ks-run-unowned.ps1'
            [IO.File]::WriteAllText($sentinel, 'unrelated snapshot-shaped file')
            $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -ModulePath "{1}" -WorkerRoot "{2}"' -f $PSCommandPath,([IO.Path]::GetFullPath($ModulePath)),$caseRoot
            $parent = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $caseRoot 'parent.stdout') -RedirectStandardError (Join-Path $caseRoot 'parent.stderr')
            $null = $parent.Handle
            if (-not $ready.WaitOne(30000)) { throw "The $mode child did not reach its loading barrier." }
            $child = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText((Join-Path $caseRoot 'child.pid')))
            $null = $child.Handle
            if (-not $parent.WaitForExit(15000)) { throw "The original $mode invocation did not finish." }
            if ($null -eq $parent.ExitCode -or $parent.ExitCode -ne 0) { throw "The original $mode invocation failed (exit $($parent.ExitCode)): $([IO.File]::ReadAllText((Join-Path $caseRoot 'parent.stderr')))" }
            $global:KsLifetimeBody = "'replacement version'"
            Update-KillerScripts
            if ((Invoke-KillerScript fixture -Offline) -cne 'replacement version') { throw 'The newer version did not become the current cache.' }
            $release.Set() | Out-Null
            if (-not $finished.WaitOne(30000)) { throw "The detached $mode child did not finish." }
            if (-not $child.WaitForExit(15000)) { throw "The detached $mode child did not exit." }
            $failure = Join-Path $caseRoot 'failure'
            if ([IO.File]::Exists($failure)) { throw "Detached load after $mode failed: $([IO.File]::ReadAllText($failure))" }
            if ($child.ExitCode -ne 0) { throw "The detached $mode child failed: $([IO.File]::ReadAllText((Join-Path $caseRoot 'child.stderr')))" }
            if ([IO.File]::ReadAllText((Join-Path $caseRoot 'result')) -cne 'original detached version') { throw 'The detached child executed the wrong version.' }
            if ([IO.File]::ReadAllText((Join-Path $caseRoot 'loaded-content.ps1')) -cne $fixtureBody) { throw 'The detached child did not load the complete original bytes.' }
            if ([IO.File]::ReadAllText($sentinel) -cne 'unrelated snapshot-shaped file') { throw 'An unowned snapshot-shaped file was changed.' }
            Write-Output "PASS detached child after $mode loads the complete original version after cache refresh"
        } catch {
            $caseFailure = $_
            throw
        } finally {
            $cleanupErrors = @()
            foreach ($process in @($child, $parent)) {
                if ($process) {
                    try {
                        if (-not $process.HasExited) {
                            $process.Kill()
                            if (-not $process.WaitForExit(5000)) { throw 'An owned fixture process did not exit during cleanup.' }
                        }
                    } catch { $cleanupErrors += $_.Exception.Message }
                    finally { $process.Dispose() }
                }
            }
            $ready.Dispose()
            $release.Dispose()
            $finished.Dispose()
            if ($cleanupErrors.Count) {
                if ($caseFailure) { Write-Warning ('Additional process cleanup failure: ' + ($cleanupErrors -join '; ')) }
                else { throw ($cleanupErrors -join '; ') }
            }
        }
    }
    # Keep the cache directory long enough to expose added filename overhead on PS5.1.
    $longLeafLength = 210 - $root.Length - 1
    if ($longLeafLength -lt 1 -or $longLeafLength -gt 255) { throw 'The fixture scratch root cannot contain the required 210-character cache directory.' }
    $longRoot = Join-Path $root ('p' * $longLeafLength)
    [IO.Directory]::CreateDirectory($longRoot) | Out-Null
    if ($longRoot.Length -ne 210) { throw 'The long cache directory fixture must contain exactly 210 characters.' }
    & $module { param($path) $script:CacheDir = $path } $longRoot
    $global:KsLifetimeBody = '$PSScriptRoot'
    Write-Output 'CHECK version publication with a 210-character cache directory'
    if ((Invoke-KillerScript fixture) -cne $longRoot) { throw 'Online invocation from the long cache directory changed its script root.' }
    if ((Invoke-KillerScript fixture -Offline) -cne $longRoot) { throw 'Offline invocation from the long cache directory changed its script root.' }
    Write-Output 'PASS online and offline invocation from a 210-character cache directory'

    $controlRoot = Join-Path $root 'version-controls'
    [IO.Directory]::CreateDirectory($controlRoot) | Out-Null
    & $module { param($path) $script:CacheDir = $path } $controlRoot
    $global:KsLifetimeBody = '$PSCommandPath'
    $firstVersionPath = Invoke-KillerScript fixture
    $secondVersionPath = Invoke-KillerScript fixture -Offline
    if ($firstVersionPath -cne $secondVersionPath) { throw 'Repeated use of one version created another snapshot path.' }
    if (-not [IO.File]::Exists($firstVersionPath)) { throw 'The completed version path was not retained.' }
    [IO.File]::WriteAllText($firstVersionPath, "'unowned replacement content'")
    $refusal = $null
    try { Invoke-KillerScript fixture -Offline | Out-Null } catch { $refusal = $_ }
    if (-not $refusal) { throw 'A replaced version snapshot was accepted.' }
    if ([IO.File]::ReadAllText($firstVersionPath) -cne "'unowned replacement content'") { throw 'A pre-existing version snapshot was overwritten or deleted.' }
    Write-Output 'PASS repeated version reuse and refusal without changing a substituted snapshot'
    Write-Output "PASS detached script lifetime on PowerShell $($PSVersionTable.PSVersion)"
} catch {
    $fixtureFailure = $_
    throw
} finally {
    Remove-Module $module -Force
    Remove-Item Function:\global:Invoke-WebRequest
    Remove-Variable KsLifetimeBody -Scope Global -ErrorAction SilentlyContinue
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    try {
        $cleanupTarget = (Get-Item -LiteralPath $root).FullName
        if ($cleanupTarget -ine $root -or -not $cleanupTarget.StartsWith($scratchBoundary, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The cleanup target must remain inside the owned scratch boundary.'
        }
        Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
    } catch {
        if ($fixtureFailure) { Write-Warning ('Additional scratch cleanup failure: ' + $_.Exception.Message) }
        else { throw }
    }
}
