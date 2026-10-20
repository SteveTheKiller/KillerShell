param(
    [string]$WorkflowPath = (Join-Path $PSScriptRoot '..\..\.github\workflows\chocolatey-release.yml'),
    [string]$ScratchRoot = '',
    [string]$Worker = ''
)
$ErrorActionPreference = 'Stop'

$cases = @{
    immediatePass = @{ States = @('Pass'); Success = $true; Pushes = 0; Warning = $false }
    failPendingPass = @{ States = @('Fail', 'Pending', 'Pass'); Success = $true; Pushes = 1; Warning = $false }
    repeatedFailDeadline = @{ States = @('Fail'); Success = $false; Pushes = 1; Warning = $false }
    failThenFeedUnavailable = @{ States = @('Fail', 'Unavailable'); Success = $false; Pushes = 1; Warning = $false }
    failPendingFail = @{ States = @('Fail', 'Pending', 'Fail'); Success = $false; Pushes = 1; Warning = $false }
    genuinePendingDeadline = @{ States = @('Pending'); Success = $true; Pushes = 0; Warning = $true }
    repushFailure = @{ States = @('Fail'); Success = $false; Pushes = 1; Warning = $false; PushFails = $true }
}

if ($Worker) {
    try {
        if (-not $cases.ContainsKey($Worker)) { throw "Unknown scenario: $Worker" }
        $text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkflowPath).Path)
        $step = [regex]::Match($text, '(?ms)^      - name: Wait for Chocolatey validation\r?\n(?<step>.*?)(?=^      - |\z)')
        if (-not $step.Success) { throw 'The actual validation workflow step was not found.' }
        $run = [regex]::Match($step.Groups['step'].Value, '(?ms)^        run: \|\r?\n(?<body>.*)')
        if (-not $run.Success) { throw 'The validation step must have a literal run body.' }
        $body = [regex]::Replace($run.Groups['body'].Value, '(?m)^          ', '')
        $script:scenario = $cases[$Worker]
        $script:clock = [datetime]'2026-10-20T00:00:00Z'
        $script:feedIndex = 0
        function Get-Date {
            param([string]$Format)
            if ($Format) { return $script:clock.ToString($Format) }
            return $script:clock
        }
        function Start-Sleep {
            param([int]$Seconds)
            if ($Seconds -ne 300) { throw "Unexpected workflow sleep: $Seconds" }
            $script:clock = $script:clock.AddMinutes(60)
        }
        function Invoke-WebRequest {
            param([string]$Uri, [switch]$UseBasicParsing, [int]$TimeoutSec)
            if ($Uri -notlike '*api/v2/Packages*') { throw "Unexpected feed boundary: $Uri" }
            $index = [Math]::Min($script:feedIndex, $script:scenario.States.Count - 1)
            $state = $script:scenario.States[$index]
            $script:feedIndex++
            Write-Host "FIXTURE_FEED=$state"
            if ($state -eq 'Unavailable') { throw 'Fixture feed unavailable' }
            return @{ Content = '<d:PackageValidationResultStatus>' + $state + '</d:PackageValidationResultStatus>' }
        }
        function choco {
            param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)
            if ($Arguments[0] -ne 'push') { throw 'Only the package re-push boundary is expected.' }
            Write-Host 'FIXTURE_PUSH'
            $global:LASTEXITCODE = if ($script:scenario.PushFails) { 42 } else { 0 }
        }
        $env:CHOCO_VERSION = '1.4.1'
        $env:CHOCO_PACKAGE = 'fixture-never-uploaded.nupkg'
        $env:CHOCO_API_KEY = 'fixture-not-a-key'
        & ([scriptblock]::Create($body))
        throw 'The workflow body returned without its expected exit.'
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.ToString())
        exit 1
    }
}

if (-not $ScratchRoot) { $ScratchRoot = Join-Path ([IO.Path]::GetTempPath()) ('KillerShell-workflow-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $ScratchRoot) { throw 'Choose a new scratch directory so existing evidence is preserved.' }
New-Item -ItemType Directory -Path $ScratchRoot | Out-Null
$shell = (Get-Process -Id $PID).Path
$workflow = (Resolve-Path -LiteralPath $WorkflowPath).Path
foreach ($name in @('immediatePass', 'failPendingPass', 'repeatedFailDeadline', 'failThenFeedUnavailable', 'failPendingFail', 'genuinePendingDeadline', 'repushFailure')) {
    $output = Join-Path $ScratchRoot ($name + '.out.log')
    $errors = Join-Path $ScratchRoot ($name + '.err.log')
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -WorkflowPath "' + $workflow + '" -Worker ' + $name
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $shell
    $start.Arguments = $arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    $readOutput = $process.StandardOutput.ReadToEndAsync()
    $readErrors = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw "Scenario exceeded its deadline: $name" }
        $stdout = $readOutput.GetAwaiter().GetResult()
        $stderr = $readErrors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($output, $stdout)
        [IO.File]::WriteAllText($errors, $stderr)
        $case = $cases[$name]
        $pushes = [regex]::Matches($stdout, '(?m)^FIXTURE_PUSH\r?$').Count
        $warning = $stdout.Contains('::warning::Chocolatey validation was still pending')
        if (($process.ExitCode -eq 0) -ne $case.Success -or $pushes -ne $case.Pushes -or $warning -ne $case.Warning) {
            throw "FAIL $name exit=$($process.ExitCode) pushes=$pushes warning=$warning`n$stdout`n$stderr"
        }
        Write-Host "PASS $name exit=$($process.ExitCode) pushes=$pushes warning=$warning"
    }
    finally { $process.Dispose() }
}
Write-Host "PASS all seven actual-workflow scenarios. Logs: $ScratchRoot"
