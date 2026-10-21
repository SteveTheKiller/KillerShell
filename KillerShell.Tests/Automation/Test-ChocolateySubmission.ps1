param([string]$WorkflowPath = (Join-Path $PSScriptRoot '..\..\.github\workflows\chocolatey-release.yml'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkflowPath))
$start = $source.IndexOf('          # Pack and push')
$end = $source.IndexOf('      # A successful push')
if ($start -lt 0 -or $end -le $start) { throw 'Submission fixture boundaries changed.' }
$body = [regex]::Replace($source.Substring($start, $end - $start), '(?m)^          ', '')
$root = Join-Path ([IO.Path]::GetTempPath()) ('ks-choco-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$previousEnvironment = $env:GITHUB_ENV
class SubmissionFeedFailure : System.Exception {
    [object]$Response
    SubmissionFeedFailure([int]$status) : base('Fixture feed failure') {
        $this.Response = [pscustomobject]@{ StatusCode = $status }
    }
}
try {
    $env:GITHUB_ENV = Join-Path $root 'environment.txt'
    foreach ($case in @('existing', 'new-and-retry', 'unavailable', 'malformed', 'wrong-version', 'push-failure', 'pack-failure')) {
        $state = @{ Pushes = 0; Exists = ($case -eq 'existing') }
        $failed = $false
        try { & {
            $version = '1.4.1'
            function choco {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq 'push') {
                    $state.Pushes++
                    $state.Exists = $true
                    if ($case -eq 'push-failure') { $global:LASTEXITCODE = 1 }
                }
                if ($args[0] -eq 'pack' -and $case -eq 'pack-failure') { $global:LASTEXITCODE = 1 }
            }
            function Invoke-WebRequest {
                param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
                if ($case -eq 'unavailable') { throw [SubmissionFeedFailure]::new(503) }
                if ($case -eq 'malformed') { return [pscustomobject]@{ Content = '<html>not package data</html>' } }
                if ($case -eq 'wrong-version') { return [pscustomobject]@{ Content = '<entry xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"><d:Id>killershell</d:Id><d:Version>1.4.0</d:Version></entry>' } }
                if (-not $state.Exists) { throw [SubmissionFeedFailure]::new(404) }
                [pscustomobject]@{ Content = '<entry xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"><d:Id>killershell</d:Id><d:Version>1.4.1</d:Version><d:PackageValidationResultStatus>Pending</d:PackageValidationResultStatus></entry>' }
            }
            & ([scriptblock]::Create($body))
            if ($case -eq 'new-and-retry') { & ([scriptblock]::Create($body)) }
        } } catch { $failed = $true }
        $expectedPushes = $(if ($case -in @('new-and-retry', 'push-failure')) { 1 } else { 0 })
        $expectedFailure = $case -notin @('existing', 'new-and-retry')
        if ($state.Pushes -ne $expectedPushes -or $failed -ne $expectedFailure) {
            throw "${case}: expected $expectedPushes initial submissions and failure=$expectedFailure, got $($state.Pushes) and failure=$failed."
        }
        Write-Host "PASS $case"
    }
} finally {
    $env:GITHUB_ENV = $previousEnvironment
    Remove-Item -LiteralPath $root -Recurse -Force
}
$global:LASTEXITCODE = 0
