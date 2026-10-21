param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerPivot.psd1'))
$ErrorActionPreference = 'Stop'

# Only fictional provider commands are installed. No cloud module is loaded.
$global:PivotDisconnectFixture = @{ Exchange = $true; Graph = $true; Mode = 'exchange-error'; Calls = @() }
function global:Disconnect-ExchangeOnline {
    [CmdletBinding(SupportsShouldProcess)] param()
    $global:PivotDisconnectFixture.Calls += 'Exchange'
    if ($global:PivotDisconnectFixture.Mode -in 'exchange-error','both-errors') { Write-Error 'fictional Exchange teardown failed'; return }
    if ($global:PivotDisconnectFixture.Mode -eq 'exchange-survives') { return }
    $global:PivotDisconnectFixture.Exchange = $false
}
function global:Get-ConnectionInformation {
    [CmdletBinding()] param()
    if ($global:PivotDisconnectFixture.Mode -eq 'exchange-query-error') { Write-Error 'fictional Exchange query failed'; return }
    if ($global:PivotDisconnectFixture.Exchange) { [pscustomobject]@{ State = 'Connected' } }
}
function global:Disconnect-MgGraph {
    [CmdletBinding()] param()
    $global:PivotDisconnectFixture.Calls += 'Graph'
    if ($global:PivotDisconnectFixture.Mode -in 'graph-error','both-errors') { Write-Error 'fictional Graph teardown failed'; return }
    if ($global:PivotDisconnectFixture.Mode -eq 'graph-survives') { return }
    $global:PivotDisconnectFixture.Graph = $false
}
function global:Get-MgContext {
    [CmdletBinding()] param()
    if ($global:PivotDisconnectFixture.Mode -eq 'graph-query-error') { Write-Error 'fictional Graph query failed'; return }
    if ($global:PivotDisconnectFixture.Graph) { [pscustomobject]@{ TenantId = 'fictional' } }
}
$module = Import-Module $ModulePath -Force -PassThru
try {
    foreach ($case in 'exchange-error','graph-error','both-errors','success','exchange-survives','graph-survives','exchange-query-error','graph-query-error') {
        $global:PivotDisconnectFixture = @{ Exchange = $true; Graph = $true; Mode = $case; Calls = @() }
        $script:disconnectFailure = $null
        $messages = & { try { Disconnect-PivotTenant } catch { $script:disconnectFailure = $_.Exception.Message } } 6>&1
        $failure = $script:disconnectFailure
        $reportedClosed = ($messages | Out-String) -match 'All sessions closed'
        if ($case -eq 'success') {
            if ($failure -or -not $reportedClosed) { throw "Successful teardown was not reported: $failure" }
        } elseif ($reportedClosed -or -not $failure) { throw "$case was reported as complete or its failure was hidden." }
        if ($case -in 'exchange-error','both-errors' -and $failure -notmatch 'Exchange teardown failed') { throw 'Exchange failure was hidden.' }
        if ($case -in 'graph-error','both-errors' -and $failure -notmatch 'Graph teardown failed') { throw 'Graph failure was hidden.' }
        if ('Exchange' -notin $global:PivotDisconnectFixture.Calls -or 'Graph' -notin $global:PivotDisconnectFixture.Calls) { throw "$case skipped a provider teardown." }
        Write-Output "PASS $case"
    }
} finally {
    Remove-Module $module -Force
    foreach ($command in 'Disconnect-ExchangeOnline','Get-ConnectionInformation','Disconnect-MgGraph','Get-MgContext') {
        Remove-Item -LiteralPath "Function:\global:$command"
    }
    Remove-Variable PivotDisconnectFixture -Scope Global
}
