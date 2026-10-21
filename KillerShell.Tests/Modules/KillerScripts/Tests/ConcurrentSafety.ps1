param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'),
    [string]$ScratchRoot = [IO.Path]::GetTempPath(),
    [string]$WorkerRoot,
    [string]$WorkerKey
)
$ErrorActionPreference = 'Stop'
$root = if ($WorkerRoot) { $WorkerRoot } else { Join-Path $ScratchRoot ('KillerScripts-concurrent-' + [guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($root) | Out-Null
$module = Import-Module $ModulePath -Force -PassThru
& $module { param($root) $script:CacheDir = $root } $root
$global:KsConcurrentFixture = @{ Root = $root; Key = $WorkerKey; Body = "'original completed'" }
function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($Uri -like '*descriptions.json') { return [pscustomobject]@{ Content = '{"fixture.ps1":{"name":"Fixture"}}' } }
    $state = $global:KsConcurrentFixture
    if ($state.Key -in 'alpha','beta') {
        [IO.File]::WriteAllText((Join-Path $state.Root ($state.Key + '.ready')), 'ready')
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while (-not [IO.File]::Exists((Join-Path $state.Root 'release-writers'))) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Download barrier timed out.' }
            Start-Sleep -Milliseconds 20
        }
    }
    [pscustomobject]@{ Content = $state.Body }
}
function global:Set-Content {
    [CmdletBinding()] param($LiteralPath, $Value, $Encoding, [switch]$NoNewline)
    $state = $global:KsConcurrentFixture
    if ($state.Key -eq 'interrupted' -and $Value -eq "'interrupted completed'") {
        [IO.File]::WriteAllText($LiteralPath, "'partial prefix'")
        [IO.File]::WriteAllText((Join-Path $state.Root 'partial.ready'), 'ready')
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }
        throw 'The interruption fixture was not stopped.'
    }
    Microsoft.PowerShell.Management\Set-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding -NoNewline:$NoNewline
}
if ($WorkerRoot) {
    $global:KsConcurrentFixture.Body = "'$WorkerKey completed'"
    try {
        $actual = Invoke-KillerScript fixture
        [IO.File]::WriteAllText((Join-Path $root ($WorkerKey + '.result')), $actual)
    } catch { [IO.File]::WriteAllText((Join-Path $root ($WorkerKey + '.result')), $_.Exception.Message); exit 1 }
    exit
}
$children = @()
function Start-FixtureWorker([string]$Key) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ModulePath "{1}" -WorkerRoot "{2}" -WorkerKey "{3}"' -f $PSCommandPath,([IO.Path]::GetFullPath($ModulePath)),$root,$Key
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root "$Key.stdout") -RedirectStandardError (Join-Path $root "$Key.stderr")
}
function Wait-FixtureFile([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not [IO.File]::Exists((Join-Path $root $Name))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Fixture barrier timed out: $Name" }
        Start-Sleep -Milliseconds 20
    }
}
try {
    if ((Invoke-KillerScript fixture) -cne 'original completed') { throw 'Initial cache seed failed.' }
    $before = (Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash
    $interrupted = Start-FixtureWorker interrupted
    $children += $interrupted
    Wait-FixtureFile partial.ready
    if ((Invoke-KillerScript fixture -Offline) -cne 'original completed') { throw 'Reader executed partial content during staging.' }
    $interrupted.Kill()
    $interrupted.WaitForExit()
    if ((Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash -ne $before -or
        (Invoke-KillerScript fixture -Offline) -cne 'original completed') { throw 'Interrupted staging damaged the previous cache.' }
    Write-Output 'PASS readers retain the complete cache during a partial stage and after its writer is terminated'
    foreach ($key in 'alpha','beta') { $children += Start-FixtureWorker $key }
    Wait-FixtureFile alpha.ready
    Wait-FixtureFile beta.ready
    [IO.File]::WriteAllText((Join-Path $root 'release-writers'), 'release')
    foreach ($child in $children) { if (-not $child.WaitForExit(15000)) { throw 'Concurrent worker did not finish.' } }
    foreach ($key in 'alpha','beta') {
        $actual = [IO.File]::ReadAllText((Join-Path $root "$key.result"))
        if ($actual -notin 'alpha completed','beta completed') { throw "Concurrent invocation executed an incomplete result: $actual" }
    }
    if ((Invoke-KillerScript fixture -Offline) -notin 'alpha completed','beta completed') { throw 'Concurrent publication left an incomplete offline cache.' }
    Write-Output 'PASS simultaneous invocations execute complete versions and preserve a complete offline cache'
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    Remove-Module $module -Force
    Remove-Item Function:\global:Invoke-WebRequest,Function:\global:Set-Content
    Remove-Variable KsConcurrentFixture -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
