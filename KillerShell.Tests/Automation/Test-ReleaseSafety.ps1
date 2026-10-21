param([string]$ReleasePath = (Join-Path $PSScriptRoot '..\..\release.ps1'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleasePath))
# Replace build/preflight preparation with fixture values. Keep the actual parameter
# policy, helper functions and every publication branch, including their exit paths.
$preparation = $source.IndexOf('# --- 1. Read version')
$publication = $source.IndexOf('if ($DryRun) {', $source.IndexOf("if (`$themeCount -gt 0)"))
if ($preparation -lt 0 -or $publication -lt 0) { throw 'Release fixture boundaries changed.' }
$fixture = @'
$PSScriptRoot = 'C:\fixture'
$Version = '1.4.1'
$Tag = 'v1.4.1'
$defaultBranch = 'main'
$local = 'BASE'
$exe = 'fixture.exe'
$exeHash = 'ABC123'
$srcZip = 'fixture-src.zip'
$sumsFile = 'fixture-sums.txt'
$exeMB = '1 MB'
$srcZipMB = '1 MB'
'@
$scriptText = $source.Substring(0, $preparation) + $fixture + "`n" + $source.Substring($publication)
# Adapt process exits to exceptions at the fixture's host boundary.
$scriptText = $scriptText -replace '\bexit ([01])', 'throw "FIXTURE_EXIT_$1"'
$cases = @(
    @{ Name = 'unsigned'; Args = @{ SkipSign = $true }; Expected = 1; Calls = 0 },
    @{ Name = 'unsigned-dry'; Args = @{ SkipSign = $true; DryRun = $true; Choco = $true }; Expected = 0; Calls = 0 },
    @{ Name = 'invalid-signature'; Args = @{}; Expected = 1; Calls = 0; Invalid = $true },
    @{ Name = 'signed'; Args = @{ Choco = $true; ChocoApiKey = 'fixture' }; Expected = 0; Calls = 6 },
    @{ Name = 'changed-after-site'; Args = @{}; Expected = 1; Calls = 1; Tamper = $true }
)
foreach ($case in $cases) {
    $global:releaseFixtureCalls = [Collections.Generic.List[string]]::new()
    $global:releaseFixtureInvalid = [bool]$case.Invalid
    $global:releaseFixtureTamper = [bool]$case.Tamper
    $global:releaseFixtureWrites = 0
    # Execute in a child PowerShell pipeline so script exit cannot terminate the runner.
    $pipeline = [PowerShell]::Create()
    try {
        $pipeline.Runspace.SessionStateProxy.SetVariable('releaseFixtureCalls', $global:releaseFixtureCalls)
        $pipeline.Runspace.SessionStateProxy.SetVariable('releaseFixtureInvalid', $global:releaseFixtureInvalid)
        $pipeline.Runspace.SessionStateProxy.SetVariable('releaseFixtureTamper', $global:releaseFixtureTamper)
        $null = $pipeline.AddScript(@'
param($text, $arguments)
function Set-Location { param($Path) }
function Get-AuthenticodeSignature { param($LiteralPath) [pscustomobject]@{ Status = $(if ($global:releaseFixtureInvalid) { 'NotSigned' } else { 'Valid' }) } }
function Get-FileHash { param($LiteralPath, $Algorithm) [pscustomobject]@{ Hash = 'ABC123' } }
function git {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'status') { if ($args.Count -gt 2) { return ' M shell-landing/index.html' }; return }
    if ($args[0] -eq 'rev-parse') {
        if ($args[1] -eq 'HEAD^{tree}') { return 'TREE' }
        if ($args[1] -eq 'HEAD^') { return 'BASE' }
        return $(if ($global:releaseFixtureCommitted) { 'RELEASE' } else { 'BASE' })
    }
    if ($args[0] -eq 'write-tree') { return 'TREE' }
    if ($args[0] -eq 'diff') { return 'README.md' }
    if ($args[0] -eq 'commit') { $global:releaseFixtureCommitted = $true }
    if ($args[0] -eq 'push') {
        $global:releaseFixtureCalls.Add('git push')
        if ($global:releaseFixtureTamper) { $global:releaseFixtureInvalid = $true }
    }
    if ($args[0] -eq 'tag') { $global:releaseFixtureCalls.Add('git tag') }
    $global:LASTEXITCODE = 0
}
function gh {
    if ($args[0] -ne 'release' -or $args[1] -ne 'view') { $global:releaseFixtureCalls.Add('gh ' + ($args -join ' ')) }
    $global:LASTEXITCODE = 0
}
function choco { if ($args[0] -eq 'push') { $global:releaseFixtureCalls.Add('choco push') }; $global:LASTEXITCODE = 0 }
function Get-Content { param($Path, [switch]$Raw) if ($Raw) { return 'REPLACE_VERSION REPLACE_HASH' }; @('## [1.4.1]', 'fixture notes') }
function Set-Content { param($Path, $Value, $Encoding, [switch]$NoNewline) $global:releaseFixtureWrites++ }
function Test-Path { param($Path) return $true }
function Push-Location { param($Path) }
function Pop-Location { }
try { & ([scriptblock]::Create($text)) @arguments; $global:fixtureExit = 0 }
catch {
    if ($_.Exception.Message -match '^FIXTURE_EXIT_([01])$') { $global:fixtureExit = [int]$Matches[1] }
    else { throw }
}
'@).AddArgument($scriptText).AddArgument($case.Args)
        $null = $pipeline.Invoke()
        $exit = $pipeline.Runspace.SessionStateProxy.GetVariable('fixtureExit')
        if ([int]$exit -ne $case.Expected) { throw "$($case.Name): expected exit $($case.Expected), got $exit" }
        if ($pipeline.Streams.Error.Count) { throw ($pipeline.Streams.Error | Out-String) }
        $calls = $pipeline.Runspace.SessionStateProxy.GetVariable('releaseFixtureCalls')
        if ($calls.Count -ne $case.Calls) { throw "$($case.Name): expected $($case.Calls) publication calls, got $($calls.Count): $calls" }
        Write-Host "PASS $($case.Name): $($calls.Count) publication calls"
    }
    finally { $pipeline.Dispose() }
}
