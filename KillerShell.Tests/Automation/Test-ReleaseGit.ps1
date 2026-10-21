param([string]$ReleasePath = (Join-Path $PSScriptRoot '..\..\release.ps1'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleasePath))
$start = $source.IndexOf('if ($DryRun) {', $source.IndexOf('if ($themeCount -gt 0)'))
$end = $source.IndexOf('# --- 8. Release notes')
if ($start -lt 0 -or $end -le $start) { throw 'Git fixture boundaries changed.' }
$publication = $source.Substring($start, $end - $start)
$tagStart = $source.IndexOf('# --- 9. Tag and push')
$tagEnd = $source.IndexOf('# --- 10. GitHub release')
$tagPublication = $source.Substring($tagStart, $tagEnd - $tagStart)
$gitExe = (Get-Command git.exe).Source
$cases = @('rejected-commit', 'stage-failure', 'tag-failure', 'concurrent-source', 'concurrent-staged-source', 'success', 'push-failure')
foreach ($case in $cases) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('ks-release-git-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $state = @{ Pushes = 0; Tags = 0; Rejected = $false }
    Push-Location $root
    try {
        & $gitExe init --quiet
        & $gitExe config user.name 'Steve the Killer'
        & $gitExe config user.email 'steve@thekiller.net'
        [IO.File]::WriteAllText((Join-Path $root 'README.md'), 'old metadata')
        [IO.Directory]::CreateDirectory((Join-Path $root 'shell-landing')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'shell-landing\index.html'), 'old page')
        & $gitExe add .
        & $gitExe commit --quiet -m 'fixture baseline'
        if ($LASTEXITCODE) { throw 'Cannot prepare disposable Git fixture.' }
        $local = (& $gitExe rev-parse HEAD).Trim()
        $releaseCommit = $local
        if ($case -ne 'tag-failure') { [IO.File]::WriteAllText((Join-Path $root 'README.md'), 'release metadata') }
        if ($case -eq 'concurrent-source') { [IO.File]::WriteAllText((Join-Path $root 'concurrent.cs'), 'unreviewed source') }
        if ($case -eq 'concurrent-staged-source') {
            [IO.File]::WriteAllText((Join-Path $root 'concurrent.cs'), 'unreviewed staged source')
            & $gitExe add concurrent.cs
        }
        if ($case -eq 'rejected-commit') {
            [IO.File]::WriteAllText((Join-Path $root '.git\hooks\pre-commit'), "#!/bin/sh`nexit 1`n")
        }
        & {
            $DryRun = $false
            $Version = '1.4.1'
            $Tag = 'v1.4.1'
            $defaultBranch = 'main'
            function Fail { param($Message) throw "GIT_REJECTED: $Message" }
            function Step { param($Message) }
            function Assert-PublishableArtifact { }
            function git {
                if ($args[0] -eq 'tag') { $state.Tags++ }
                if (($case -eq 'stage-failure' -and $args[0] -eq 'add') -or
                    ($case -eq 'tag-failure' -and $args[0] -eq 'tag')) { $global:LASTEXITCODE = 1; return }
                if ($args[0] -eq 'push') { $state.Pushes++; $global:LASTEXITCODE = $(if ($case -eq 'push-failure') { 1 } else { 0 }); return }
                & $gitExe @args
                $global:LASTEXITCODE = $LASTEXITCODE
            }
            try { & ([scriptblock]::Create($(if ($case -eq 'tag-failure') { $tagPublication } else { $publication }))) }
            catch {
                if ($_.Exception.Message -notlike 'GIT_REJECTED:*') { throw }
                $state.Rejected = $true
            }
        }
        $expectedPushes = $(if ($case -in @('success', 'push-failure')) { 1 } else { 0 })
        if ($state.Rejected -ne ($case -ne 'success') -or $state.Pushes -ne $expectedPushes) {
            throw "${case}: rejected metadata commit reached publication ($($state.Pushes) pushes)."
        }
        if ($case -eq 'tag-failure' -and $state.Tags -ne 1) { throw 'The tag failure fixture did not reach tag creation.' }
        Write-Host "PASS $case"
    } finally {
        Pop-Location
        $owned = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not [IO.Path]::GetFullPath($root).StartsWith($owned, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped its temporary root.' }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
$global:LASTEXITCODE = 0
