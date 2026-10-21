param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerScripts-cache-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$module = Import-Module $ModulePath -Force -PassThru
& $module { param($root) $script:CacheDir = $root } $root
$global:KsCacheFixture = @{ Body = "'complete original'"; Fault = $false; ManifestFault = $false; Silent = $false; Manifest = '{"fixture.ps1":{"name":"Fixture","description":"Harmless"}}' }
function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($Uri -like '*descriptions.json') {
        return [pscustomobject]@{ Content = $global:KsCacheFixture.Manifest }
    }
    [pscustomobject]@{ Content = $global:KsCacheFixture.Body }
}
function global:Set-Content {
    [CmdletBinding()] param($LiteralPath, $Value, $Encoding, [switch]$NoNewline)
    if ($global:KsCacheFixture.Fault -and $Value -eq "'new complete script'") {
        [IO.File]::WriteAllText($LiteralPath, "'partial prefix'")
        if ($global:KsCacheFixture.Silent) { return }
        throw 'fictional disk write failed after truncation'
    }
    if ($global:KsCacheFixture.ManifestFault -and $Value -eq $global:KsCacheFixture.Manifest) {
        [IO.File]::WriteAllText($LiteralPath, '{"partial":')
        throw 'fictional manifest write failed after truncation'
    }
    Microsoft.PowerShell.Management\Set-Content -LiteralPath $LiteralPath -Value $Value -Encoding $Encoding -NoNewline:$NoNewline
}
try {
    $actual = Invoke-KillerScript fixture
    if ($actual -cne 'complete original') { throw 'Initial harmless download did not execute.' }
    $before = (Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash
    $global:KsCacheFixture.Body = "'incomplete replacement'; if ("
    $failure = $null
    try { $actual = Invoke-KillerScript fixture -WarningAction SilentlyContinue } catch { $failure = $_ }
    if ($failure -or $actual -cne 'complete original' -or (Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash -ne $before) {
        throw 'Invalid refresh destroyed or replaced the last valid script cache.'
    }
    Write-Output 'PASS invalid refresh preserves and executes the previous complete cache'
    $global:KsCacheFixture.Body = "'new complete script'"
    $global:KsCacheFixture.Fault = $true
    $actual = Invoke-KillerScript fixture -WarningAction SilentlyContinue
    if ($actual -cne 'complete original' -or (Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash -ne $before) {
        throw 'A refresh with a failing filesystem write did not preserve the completed cache.'
    }
    Write-Output 'PASS partial filesystem write preserves and executes the previous complete cache'
    $global:KsCacheFixture.Silent = $true
    if ((Invoke-KillerScript fixture -WarningAction SilentlyContinue) -cne 'complete original') { throw 'A silently truncated write executed partial content.' }
    $global:KsCacheFixture.Silent = $false
    Write-Output 'PASS incomplete write is detected even when the filesystem boundary returns success'
    Update-KillerScripts -WarningAction SilentlyContinue
    $actual = Invoke-KillerScript fixture -Offline
    if ($actual -cne 'complete original' -or (Get-FileHash -LiteralPath (Join-Path $root 'fixture.ps1')).Hash -ne $before) {
        throw 'Bulk refresh did not preserve the completed cache after a filesystem failure.'
    }
    Write-Output 'PASS bulk refresh preserves the previous complete cache after a write failure'
    $manifestBefore = (Get-FileHash -LiteralPath (Join-Path $root 'descriptions.json')).Hash
    $global:KsCacheFixture.Manifest = '{"other.ps1":{"name":"Other"}}'
    $global:KsCacheFixture.ManifestFault = $true
    $listed = @(Get-KillerScript)
    if ($listed.Count -ne 1 -or $listed[0].Script -ne 'fixture.ps1' -or
        (Get-FileHash -LiteralPath (Join-Path $root 'descriptions.json')).Hash -ne $manifestBefore) { throw 'Partial manifest write replaced the previous complete manifest.' }
    $global:KsCacheFixture.ManifestFault = $false
    Write-Output 'PASS partial manifest write preserves the previous complete manifest'
    $global:KsCacheFixture.Manifest = '{"incomplete":'
    $failure = $null
    try { $listed = @(Get-KillerScript) } catch { $failure = $_ }
    if ($failure -or $listed.Count -ne 1 -or $listed[0].Script -ne 'fixture.ps1' -or
        (Get-FileHash -LiteralPath (Join-Path $root 'descriptions.json')).Hash -ne $manifestBefore) {
        throw 'Invalid manifest refresh destroyed or replaced the previous complete manifest.'
    }
    Write-Output 'PASS invalid manifest refresh preserves the previous complete manifest'
    $global:KsCacheFixture.Fault = $false
    $global:KsCacheFixture.Body = "'valid refresh'"
    if ((Invoke-KillerScript fixture) -cne 'valid refresh') { throw 'A complete refresh did not replace the previous cache.' }
    Write-Output 'PASS complete refresh replaces the previous script cache'
    $global:KsCacheFixture.Body = "'incomplete'; if ("
    $failure = $null
    try { Invoke-KillerScript missing | Out-Null } catch { $failure = $_ }
    if (-not $failure -or (Test-Path -LiteralPath (Join-Path $root 'missing.ps1'))) { throw 'Invalid first download became an executable cache.' }
    Write-Output 'PASS invalid first download is refused without creating a cache'
} finally {
    Remove-Module $module -Force
    Remove-Item Function:\global:Invoke-WebRequest
    Remove-Item Function:\global:Set-Content
    Remove-Variable KsCacheFixture -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
