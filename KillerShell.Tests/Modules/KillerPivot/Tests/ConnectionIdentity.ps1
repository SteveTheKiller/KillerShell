param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerPivot.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerPivot-identity-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$previousConfig = $env:PIVOT_CONFIG
$env:PIVOT_CONFIG = Join-Path $root 'tenants.json'
[IO.File]::WriteAllText($env:PIVOT_CONFIG, '{"schema":1,"tenants":[{"key":"test","name":"Fictional","upn":"admin@example.invalid","tenantId":"11111111-1111-1111-1111-111111111111"}]}')
$module = Import-Module $ModulePath -Force -PassThru
try {
    & $module {
        function script:Get-Module { param([switch]$ListAvailable,[string]$Name) [pscustomobject]@{Name=$Name} }
        function script:Connect-ExchangeOnline {
            [CmdletBinding()] param([string]$UserPrincipalName,[bool]$ShowBanner,[switch]$DisableWAM,[string]$Prefix)
            if ($script:Mode -eq 'reuse') { return }
            $script:Connections += [pscustomobject]@{State='Connected';TenantID=$script:NewTenant;ConnectionId='new';ConnectionUsedForInbuiltCmdlets=($script:Mode -ne 'not-selected');ModulePrefix=$Prefix}
            if ($script:Mode -eq 'ambiguous') {
                $script:Connections += [pscustomobject]@{State='Connected';TenantID=$script:NewTenant;ConnectionId='other';ConnectionUsedForInbuiltCmdlets=$true;ModulePrefix=$Prefix}
            }
        }
        function script:Get-ConnectionInformation {
            [CmdletBinding()] param()
            if ($script:Mode -eq 'metadata-error' -and $script:Connections.Count -gt 1) {throw 'metadata unavailable'}
            if ($script:Mode -eq 'metadata-empty' -and $script:Connections.Count -gt 1) {return}
            $script:Connections
        }
        function script:Disconnect-ExchangeOnline {
            [CmdletBinding(SupportsShouldProcess)] param([string[]]$ConnectionId,[string[]]$ModulePrefix)
            if ($ConnectionId) { $script:Connections=@($script:Connections | Where-Object {$_.ConnectionId -notin $ConnectionId}) }
            elseif ($ModulePrefix) { $script:Connections=@($script:Connections | Where-Object {$_.ModulePrefix -notin $ModulePrefix}) }
            else { $script:Connections=@() }
        }
    }
    foreach ($case in 'old-expected-new-wrong','old-wrong-new-expected','multiple-existing','reuse','ambiguous','not-selected','metadata-error','metadata-empty') {
        & $module {
            param($case)
            $expected='11111111-1111-1111-1111-111111111111'; $wrong='22222222-2222-2222-2222-222222222222'
            $script:Mode=$case
            $script:NewTenant=if ($case -eq 'old-expected-new-wrong') {$wrong} else {$expected}
            $oldTenant=if ($case -eq 'old-wrong-new-expected') {$wrong} else {$expected}
            $script:Connections=@([pscustomobject]@{State='Connected';TenantID=$oldTenant;ConnectionId='old';ConnectionUsedForInbuiltCmdlets=($case -eq 'reuse')})
            if ($case -eq 'multiple-existing') { $script:Connections += [pscustomobject]@{State='Connected';TenantID=$wrong;ConnectionId='old2';ConnectionUsedForInbuiltCmdlets=$false} }
        } $case
        $failure=$null
        $messages = & { try { Connect-PivotTenant test -KeepExisting } catch { $script:identityFailure=$_.Exception.Message } } 6>&1
        $failure=$script:identityFailure
        $script:identityFailure=$null
        $connections=@(& $module {$script:Connections})
        $mustFail=$case -in 'old-expected-new-wrong','ambiguous','reuse','not-selected','metadata-error','metadata-empty'
        if ($mustFail -and -not $failure) { throw "$case incorrectly verified" }
        if ($mustFail -and (($messages | Out-String) -match '\[VERIFIED\]')) { throw "$case reported VERIFIED before failure" }
        if (-not $mustFail -and $failure) { throw "$case rejected: $failure" }
        if ('old' -notin $connections.ConnectionId) { throw "$case removed the older session" }
        if ($mustFail -and @($connections | Where-Object {$_.ConnectionId -in 'new','other'}).Count) { throw "$case left unvalidated new sessions" }
        Write-Output "PASS $case"
    }
} finally {
    Remove-Module $module -Force
    $env:PIVOT_CONFIG=$previousConfig
    Remove-Item -LiteralPath $root -Recurse -Force
}
