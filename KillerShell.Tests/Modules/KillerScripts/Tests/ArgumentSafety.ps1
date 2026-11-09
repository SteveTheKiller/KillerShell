param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerScripts-arguments-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
# Wrappers come from the manifest that ships with the module, so the fixture borrows AMORT's name.
# The downloaded body is a harmless stand-in with AMORT's shape: a switch first, then a validated
# int with a default, so a stray positional value lands where it did in the real failure.
$global:KsArgumentFixture = @'
param(
    [switch]$DryRun,
    [ValidateRange(1, 365)][int]$RetainDays = 14,
    [Alias('Label')]$Tag = 'none',
    [Parameter(ValueFromRemainingArguments = $true)]$Rest
)
'DryRun=' + [bool]$DryRun + ' RetainDays=' + $RetainDays + ' Tag=' + $(if ($null -eq $Tag) { '<null>' } else { $Tag }) + ' Rest=' + (@($Rest) -join ',')
'@
function global:Invoke-WebRequest {
    [CmdletBinding()] param($Uri, [switch]$UseBasicParsing)
    if ($Uri -like '*descriptions.json') {
        return [pscustomobject]@{ Content = '{"AMORT.ps1":{"name":"Fixture","description":"Harmless"}}' }
    }
    [pscustomobject]@{ Content = $global:KsArgumentFixture }
}
$module = Import-Module $ModulePath -Force -PassThru -DisableNameChecking
# Override only the cache location; every script body comes from the fake download above.
& $module { param($root) $script:CacheDir = $root } $root
try {
    function Assert-Run([string]$Label, [string]$Expected, [scriptblock]$Run) {
        $actual = try { (& $Run) -join "`n" } catch { 'ERROR ' + $_.Exception.Message }
        if ($actual -cne $Expected) { throw "$Label returned '$actual', expected '$Expected'." }
    }
    Assert-Run 'A wrapper call with no arguments' 'DryRun=False RetainDays=14 Tag=none Rest=' { Invoke-AMORT }
    Assert-Run 'A direct call with no arguments' 'DryRun=False RetainDays=14 Tag=none Rest=' { Invoke-KillerScript -Name 'AMORT.ps1' }
    Assert-Run 'Named arguments' 'DryRun=True RetainDays=30 Tag=none Rest=' { Invoke-AMORT -RetainDays 30 -DryRun }
    Assert-Run 'Colon arguments' 'DryRun=False RetainDays=7 Tag=none Rest=' { Invoke-AMORT -RetainDays:7 -DryRun:$false }
    Assert-Run 'A prefix and an alias' 'DryRun=False RetainDays=9 Tag=x Rest=' { Invoke-AMORT -Ret 9 -Label x }
    Assert-Run 'A value that starts with a dash' 'DryRun=False RetainDays=14 Tag=-x Rest=' { Invoke-AMORT -Tag '-x' }
    Assert-Run 'An explicit null value' 'DryRun=False RetainDays=14 Tag=<null> Rest=' { Invoke-AMORT -Tag $null }
    Assert-Run 'Positional arguments' 'DryRun=False RetainDays=5 Tag=y Rest=' { Invoke-AMORT 5 y }
    Assert-Run 'Mixed named and positional arguments' 'DryRun=True RetainDays=21 Tag=z Rest=' { Invoke-AMORT -DryRun 21 -Tag z }
    Assert-Run 'Extra positional arguments' 'DryRun=False RetainDays=2 Tag=a Rest=b,c' { Invoke-AMORT 2 a b c }
    Assert-Run 'A direct call with named arguments' 'DryRun=True RetainDays=3 Tag=none Rest=' { Invoke-KillerScript -Name 'AMORT.ps1' -RetainDays 3 -DryRun }
    $failed = $false
    try { Invoke-AMORT -RetainDays 0 | Out-Null } catch { $failed = $_.Exception.Message -like '*RetainDays*' }
    if (-not $failed) { throw 'An out-of-range value was not refused by the script itself.' }
    Write-Output 'PASS wrapper and direct calls keep defaults, named, colon, alias, prefix, positional and null arguments'
} finally {
    Remove-Module KillerScripts -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\global:Invoke-WebRequest
    Remove-Variable KsArgumentFixture -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
