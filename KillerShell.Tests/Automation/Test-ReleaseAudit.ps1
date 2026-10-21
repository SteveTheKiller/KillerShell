param([string]$ReleasePath = (Join-Path $PSScriptRoot '..\..\release.ps1'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleasePath))
$start = $source.IndexOf('# --- 3. Vulnerable package scan')
$end = $source.IndexOf('# --- 3b. Release tests')
if ($start -lt 0 -or $end -le $start) { throw 'Audit fixture boundaries changed.' }
$audit = $source.Substring($start, $end - $start)
$clean = '{"version":1,"parameters":"--vulnerable --include-transitive","sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"KillerShell.csproj"}]}'
$cases = @(
    @{ Name = 'clean'; Restore = 0; Scan = 0; Output = $clean; Reject = $false },
    @{ Name = 'restore-failure'; Restore = 1; Scan = 0; Output = $clean; Reject = $true },
    @{ Name = 'unsupported-command'; Restore = 0; Scan = 1; Output = 'Unrecognized option --format'; Reject = $true },
    @{ Name = 'failed-clean-text'; Restore = 0; Scan = 1; Output = $clean; Reject = $true },
    @{ Name = 'missing-sources'; Restore = 0; Scan = 0; Output = ($clean -replace '"sources":\[[^\]]+\],', ''); Reject = $true },
    @{ Name = 'warning'; Restore = 0; Scan = 0; Output = ($clean -replace '"path":"KillerShell.csproj"', '"path":"KillerShell.csproj","logs":[{"level":"warning","message":"audit unavailable"}]'); Reject = $true },
    @{ Name = 'feed-failure'; Restore = 0; Scan = 1; Output = 'error: feed unavailable'; Reject = $true },
    @{ Name = 'malformed-output'; Restore = 0; Scan = 0; Output = 'not an audit result'; Reject = $true },
    @{ Name = 'incomplete-output'; Restore = 0; Scan = 0; Output = '{"version":1}'; Reject = $true },
    @{ Name = 'vulnerable'; Restore = 0; Scan = 0; Output = ($clean -replace '"path":"KillerShell.csproj"', '"path":"KillerShell.csproj","frameworks":[{"framework":"net48","transitivePackages":[{"id":"Unsafe.Package","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.invalid/advisory"}]}]}]'); Reject = $true }
)
foreach ($case in $cases) {
    $state = @{ ReachedPublication = $false; ScanCalls = 0 }
    $rejected = $false
    try {
        & {
            function Step { param($Message) }
            function Fail { param($Message) throw "AUDIT_REJECTED: $Message" }
            function dotnet {
                if ($args[0] -eq 'restore') { $global:LASTEXITCODE = $case.Restore; return }
                $state.ScanCalls++
                $global:LASTEXITCODE = $case.Scan
                $case.Output
            }
            & ([scriptblock]::Create($audit))
            $state.ReachedPublication = $true
        }
    } catch {
        if ($_.Exception.Message -notlike 'AUDIT_REJECTED:*') { throw }
        $rejected = $true
    }
    if ($rejected -ne $case.Reject -or ($case.Reject -and $state.ReachedPublication)) {
        throw "$($case.Name): incomplete assessment reached signing/publication. Rejected=$rejected"
    }
    if ($case.Restore -ne 0 -and $state.ScanCalls -ne 0) { throw 'Failed restore continued to scan.' }
    Write-Host "PASS $($case.Name)"
}
