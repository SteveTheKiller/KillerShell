param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerPivot.psd1'),[string]$WorkerKey,[string]$GateRoot,[string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference='Stop'
if ($WorkerKey) {
    Import-Module $ModulePath -Force
    function global:Read-Host {
        param($Prompt)
        if ($Prompt -like '*UPN*') {return 'admin@example.invalid'}
        [IO.File]::WriteAllText((Join-Path $GateRoot "$WorkerKey.ready"),'ready')
        $deadline=[DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path (Join-Path $GateRoot 'release'))) {
            if ([DateTime]::UtcNow -gt $deadline) {throw 'Worker barrier timed out'}
            Start-Sleep -Milliseconds 20
        }
        return 'y'
    }
    try {
        Add-PivotTenant -Key $WorkerKey -Name $WorkerKey -TenantId 33333333-3333-3333-3333-333333333333
        [IO.File]::WriteAllText((Join-Path $GateRoot "$WorkerKey.result"),'saved')
    } catch {[IO.File]::WriteAllText((Join-Path $GateRoot "$WorkerKey.result"),"refused: $($_.Exception.Message)")}
    exit
}
$root=Join-Path $ScratchRoot ('KillerPivot-configuration-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$previousConfig=$env:PIVOT_CONFIG
$env:PIVOT_CONFIG=Join-Path $root 'tenants.json'
$module=Import-Module $ModulePath -Force -PassThru
$children=@()
try {
    foreach ($raw in '{"tenants":[', '{"schema":2,"tenants":[]}', '{"schema":"1","tenants":[]}', '{"schema":1,"tenants":{}}', '{"schema":1,"tenants":[{"key":"bad"}]}','') {
        [IO.File]::WriteAllText($env:PIVOT_CONFIG,$raw)
        $originalBytes=[Convert]::ToBase64String([IO.File]::ReadAllBytes($env:PIVOT_CONFIG))
        $failure=$null
        try { Add-PivotTenant -Key test -Upn admin@example.invalid -TenantId 11111111-1111-1111-1111-111111111111 -Name Fictional } catch {$failure=$_}
        if (-not $failure -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($env:PIVOT_CONFIG)) -cne $originalBytes) { throw 'Invalid configuration was overwritten' }
        Write-Output 'PASS invalid store preserved'
    }
    Remove-Item -LiteralPath $env:PIVOT_CONFIG
    Add-PivotTenant -Key first -Upn admin@example.invalid -TenantId 11111111-1111-1111-1111-111111111111 -Name First
    $first=[IO.File]::ReadAllText($env:PIVOT_CONFIG)
    [IO.File]::WriteAllText("$env:PIVOT_CONFIG.previous",'unrelated sibling')
    Add-PivotTenant -Key second -Upn admin@example.invalid -TenantId 22222222-2222-2222-2222-222222222222 -Name Second
    if (@(Get-PivotTenant).Count -ne 2) {throw 'Valid add lost tenant'}
    $backups=@(Get-ChildItem -LiteralPath $root | Where-Object {$_.Name -match '^tenants\.json\.previous\.[a-f0-9]{32}$'})
    if ($backups.Count -ne 1 -or [IO.File]::ReadAllText($backups[0].FullName) -cne $first) {throw 'Previous copy unavailable'}
    if ([IO.File]::ReadAllText("$env:PIVOT_CONFIG.previous") -cne 'unrelated sibling') {throw 'Unrelated backup sibling overwritten'}
    Remove-PivotTenant first
    if (@(Get-PivotTenant).Count -ne 1 -or (Get-PivotTenant).key -ne 'second') {throw 'Valid remove failed'}
    Write-Output 'PASS create add remove and recovery copy'
    Remove-PivotTenant second
    if (@(Get-PivotTenant).Count -ne 0) {throw 'Removing last tenant failed'}
    Add-PivotTenant -Key second -Upn admin@example.invalid -TenantId 22222222-2222-2222-2222-222222222222 -Name Second
    $beforeBytes=[Convert]::ToBase64String([IO.File]::ReadAllBytes($env:PIVOT_CONFIG))
    $held=[IO.File]::Open($env:PIVOT_CONFIG,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try {
        $failure=$null
        try {Add-PivotTenant -Key blocked -Upn admin@example.invalid -TenantId 11111111-1111-1111-1111-111111111111 -Name Blocked} catch {$failure=$_}
        if (-not $failure) {throw 'Unreadable store accepted a mutation'}
    } finally {$held.Dispose()}
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($env:PIVOT_CONFIG)) -cne $beforeBytes) {throw 'Denied read changed original bytes'}
    Write-Output 'PASS denied read preserved store'
    $held=[IO.File]::Open($env:PIVOT_CONFIG,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $failure=$null
        try {Add-PivotTenant -Key blocked -Upn admin@example.invalid -TenantId 11111111-1111-1111-1111-111111111111 -Name Blocked} catch {$failure=$_}
        if (-not $failure) {throw 'Locked publication unexpectedly succeeded'}
    } finally {$held.Dispose()}
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($env:PIVOT_CONFIG)) -cne $beforeBytes) {throw 'Interrupted publication changed original bytes'}
    Write-Output 'PASS interrupted publication preserved store'
    $children=@()
    foreach ($key in 'alpha','beta') {
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -ModulePath "{1}" -WorkerKey {2} -GateRoot "{3}"' -f $PSCommandPath,([IO.Path]::GetFullPath($ModulePath)),$key,$root
        $children += Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root "$key.stdout") -RedirectStandardError (Join-Path $root "$key.stderr")
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    while (@(Get-ChildItem -LiteralPath $root -Filter '*.ready').Count -ne 2) {
        if ([DateTime]::UtcNow -gt $deadline -or @($children | Where-Object {$_.HasExited}).Count) {
            $details=(Get-ChildItem -LiteralPath $root | Where-Object {$_.Extension -in '.stderr','.result'} | ForEach-Object {[IO.File]::ReadAllText($_.FullName)}) -join '; '
            throw "Concurrent workers did not reach read barrier: $details"
        }
        Start-Sleep -Milliseconds 20
    }
    [IO.File]::WriteAllText((Join-Path $root 'release'),'release')
    foreach ($child in $children) {if (-not $child.WaitForExit(45000)) {throw 'Concurrent worker did not finish'}}
    $saved=@(Get-ChildItem -LiteralPath $root -Filter '*.result' | Where-Object {[IO.File]::ReadAllText($_.FullName) -eq 'saved'})
    $refused=@(Get-ChildItem -LiteralPath $root -Filter '*.result' | Where-Object {[IO.File]::ReadAllText($_.FullName) -match '^refused:.*(configuration changed|being used by another process)'})
    if ($saved.Count -ne 1 -or $refused.Count -ne 1 -or @(Get-PivotTenant).Count -ne 2 -or -not (Get-PivotTenant second)) {throw 'Concurrent updates silently lost data or did not refuse conflict'}
    Write-Output 'PASS concurrent stale update refused'
} finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    Remove-Module $module -Force
    $env:PIVOT_CONFIG=$previousConfig
    Remove-Item -LiteralPath $root -Recurse -Force
}
