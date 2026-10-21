param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerPivot.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerPivot-connect-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$previousConfig = $env:PIVOT_CONFIG
$env:PIVOT_CONFIG = Join-Path $root 'tenants.json'
[IO.File]::WriteAllText($env:PIVOT_CONFIG, '{"schema":1,"tenants":[{"key":"test","name":"Fictional","upn":"admin@example.invalid","tenantId":"11111111-1111-1111-1111-111111111111"}]}')
$module = Import-Module $ModulePath -Force -PassThru
try {
    & $module {
        function script:Get-Module { param([switch]$ListAvailable,[string]$Name) [pscustomobject]@{Name=$Name} }
        function script:Connect-ExchangeOnline {
            [CmdletBinding()] param([string]$UserPrincipalName,[bool]$ShowBanner,[switch]$DisableWAM)
            $script:Connections = @([pscustomobject]@{State='Connected';TenantID=$script:Tenant;ConnectionId='new'})
            if ($script:Mode -eq 'connect-error') { throw 'connection failed after creation' }
        }
        function script:Get-ConnectionInformation {
            [CmdletBinding()] param()
            if ($script:Mode -eq 'metadata-error') { throw 'metadata failed' }
            if ($script:Mode -ne 'empty') { $script:Connections }
        }
        function script:Disconnect-ExchangeOnline {
            [CmdletBinding(SupportsShouldProcess)] param([string[]]$ConnectionId)
            if ($script:Connections.Count -and $script:Mode -eq 'teardown-error') { throw 'teardown failed' }
            $script:Connections = @()
        }
        function script:Connect-MgGraph {
            [CmdletBinding()] param([string]$TenantId,[string[]]$Scopes,[switch]$NoWelcome)
            $script:GraphActive = $true
            if ($script:Mode -eq 'graph-connect-error') { throw 'Graph connection failed after creation' }
        }
        function script:Get-MgContext {
            [CmdletBinding()] param()
            if (-not $script:GraphActive) { return }
            if ($script:Mode -eq 'graph-metadata-error') { throw 'Graph metadata failed' }
            [pscustomobject]@{TenantId='22222222-2222-2222-2222-222222222222'}
        }
        function script:Disconnect-MgGraph {
            [CmdletBinding()] param()
            if ($script:Mode -eq 'graph-teardown-error') { throw 'Graph teardown failed' }
            $script:GraphActive = $false
        }
    }
    foreach ($case in 'empty','metadata-error','missing-tenant','wrong-tenant','connect-error','teardown-error','success','graph-metadata-error','graph-connect-error','graph-teardown-error') {
        & $module {
            param($case)
            $script:Mode=$case; $script:Connections=@()
            $script:GraphActive=$false
            $script:Tenant = if ($case -eq 'success' -or $case -like 'graph-*') {'11111111-1111-1111-1111-111111111111'} elseif ($case -eq 'missing-tenant') {$null} else {'22222222-2222-2222-2222-222222222222'}
        } $case
        $failure = $null
        try { Connect-PivotTenant test -Graph:($case -like 'graph-*') } catch { $failure = $_.Exception.Message }
        $active = & $module { $script:Connections.Count }
        if ($case -eq 'success') {
            if ($failure -or $active -ne 1) { throw "success rejected: $failure" }
        } elseif ($case -eq 'teardown-error') {
            if ($failure -notmatch 'teardown failed') { throw "teardown failure hidden: $failure" }
        } elseif ($case -eq 'graph-teardown-error') {
            if ($failure -notmatch 'Graph teardown failed' -or $active -ne 0) { throw "Graph teardown failure hidden or Exchange survived: $failure" }
        } elseif (-not $failure -or $active -ne 0) { throw "$case left $active unvalidated sessions: $failure" }
        if ($case -like 'graph-*' -and $case -ne 'graph-teardown-error' -and (& $module { $script:GraphActive })) { throw "$case left Graph active" }
        Write-Output "PASS $case"
    }
} finally {
    Remove-Module $module -Force
    $env:PIVOT_CONFIG = $previousConfig
    Remove-Item -LiteralPath $root -Recurse -Force
}
