param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerScripts-encoding-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$module = Import-Module $ModulePath -Force -PassThru
# Override only the cache location and the external download boundary.
& $module { param($root) $script:CacheDir = $root } $root
$global:KsEncodingFixture = @{ Offline = $false; Name = ('caf' + [char]0x00E9 + '.ps1') }
function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($global:KsEncodingFixture.Offline) { throw 'fictional offline download' }
    if ($Uri -like '*descriptions.json') {
        return [pscustomobject]@{ Content = ('{"' + $global:KsEncodingFixture.Name + '":{"name":"Fixture","description":"Harmless"}}') }
    }
    [pscustomobject]@{ Content = ('# Comment: ' + [char]0x00E9 + "`r`n'caf" + [char]0x00E9 + "'") }
}
try {
    $expected = 'caf' + [char]0x00E9
    $actual = Invoke-KillerScript -Name $global:KsEncodingFixture.Name
    if ($actual -cne $expected) { throw 'Online execution changed a non-ASCII literal.' }
    $actual = Invoke-KillerScript -Name $global:KsEncodingFixture.Name -Offline
    if ($actual -cne $expected) { throw 'Offline execution changed a non-ASCII literal.' }
    $global:KsEncodingFixture.Offline = $true
    $actual = Invoke-KillerScript -Name $global:KsEncodingFixture.Name -WarningAction SilentlyContinue
    if ($actual -cne $expected) { throw 'Failed-download fallback changed a non-ASCII literal.' }
    Write-Output 'PASS normal fetch, offline execution, and failed-download fallback preserve Unicode'
    $global:KsEncodingFixture.Offline = $false
    Update-KillerScripts
    $actual = Invoke-KillerScript -Name $global:KsEncodingFixture.Name -Offline
    if ($actual -cne $expected) { throw 'Bulk refresh changed a non-ASCII literal.' }
    Write-Output 'PASS bulk refresh preserves Unicode comments, filenames, and output'
} finally {
    Remove-Module $module -Force
    Remove-Item Function:\global:Invoke-WebRequest
    Remove-Variable KsEncodingFixture -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
