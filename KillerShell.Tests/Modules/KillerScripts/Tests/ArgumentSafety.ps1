param([string]$ModulePath = (Join-Path $PSScriptRoot '..\KillerScripts.psd1'), [string]$ScratchRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$root = Join-Path $ScratchRoot ('KillerScripts-arguments-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$savedTemp = $env:TEMP
$savedPolicy = Get-ExecutionPolicy -Scope Process
# Each command is the website's one-liner: download the latest script to %TEMP% and run it. The
# fixture points %TEMP% at its own folder and stands in for the download, so nothing leaves the
# machine. The body has AMORT's shape: a switch first, then a validated int with a default, as a
# plain script with no [Parameter()] attribute, so extra values fall into $args.
$global:KsArgumentFixture = @'
param(
    [switch]$DryRun,
    [ValidateRange(1, 365)][int]$RetainDays = 14,
    [Alias('Label')]$Tag = 'none'
)
'DryRun=' + [bool]$DryRun + ' RetainDays=' + $RetainDays + ' Tag=' + $(if ($null -eq $Tag) { '<null>' } else { $Tag }) + ' Rest=' + (@($args) -join ',')
'@
$global:KsDownloads = [System.Collections.Generic.List[string]]::new()
function global:Invoke-RestMethod {
    # Advanced, so -ErrorAction works on it the way it does on the real cmdlet.
    [CmdletBinding()]
    param($Uri, $OutFile)
    $global:KsDownloads.Add([string]$Uri)
    # Offline: the error a failed download writes, non-terminating unless the caller says Stop.
    if ($global:KsOffline) { Write-Error 'Unable to connect to the remote server'; return }
    if ($Uri -like '*descriptions.json') { return [pscustomobject]@{ 'AMORT.ps1' = [pscustomobject]@{ name = 'Fixture'; description = 'Harmless' } } }
    [IO.File]::WriteAllText($OutFile, $global:KsArgumentFixture, [Text.UTF8Encoding]::new($true))
}
try {
    $env:TEMP = $root
    Import-Module $ModulePath -Force -DisableNameChecking
    function Assert-Run([string]$Label, [string]$Expected, [scriptblock]$Run) {
        $actual = try { (& $Run) -join "`n" } catch { 'ERROR ' + $_.Exception.Message }
        if ($actual -cne $Expected) { throw "$Label returned '$actual', expected '$Expected'." }
    }
    Set-ExecutionPolicy RemoteSigned -Scope Process -Force -ErrorAction Stop
    Assert-Run 'A wrapper call with no arguments' 'DryRun=False RetainDays=14 Tag=none Rest=' { Invoke-AMORT }
    if ((Get-ExecutionPolicy -Scope Process) -ne 'RemoteSigned') {
        throw 'A killer command changed the process execution policy after returning.'
    }
    Write-Output 'PASS a killer command restores the process execution policy'
    Assert-Run 'A direct call with no arguments' 'DryRun=False RetainDays=14 Tag=none Rest=' { Invoke-KillerScript -Name 'AMORT.ps1' }
    Assert-Run 'Named arguments' 'DryRun=True RetainDays=30 Tag=none Rest=' { Invoke-AMORT -RetainDays 30 -DryRun }
    # A colon value passes through. A switch in colon form (-DryRun:$false) does not survive @args
    # splatting in PowerShell itself, which is why the README says to leave a switch off instead.
    Assert-Run 'A colon value' 'DryRun=False RetainDays=7 Tag=none Rest=' { Invoke-AMORT -RetainDays:7 }
    Assert-Run 'A prefix and an alias' 'DryRun=False RetainDays=9 Tag=x Rest=' { Invoke-AMORT -Ret 9 -Label x }
    Assert-Run 'A value that starts with a dash' 'DryRun=False RetainDays=14 Tag=-x Rest=' { Invoke-AMORT -Tag '-x' }
    Assert-Run 'Positional arguments' 'DryRun=False RetainDays=5 Tag=y Rest=' { Invoke-AMORT 5 y }
    Assert-Run 'Mixed named and positional arguments' 'DryRun=True RetainDays=21 Tag=z Rest=' { Invoke-AMORT -DryRun 21 -Tag z }
    Assert-Run 'Extra positional arguments' 'DryRun=False RetainDays=2 Tag=a Rest=b,c' { Invoke-AMORT 2 a b c }
    Assert-Run 'A direct call with named arguments' 'DryRun=True RetainDays=3 Tag=none Rest=' { Invoke-KillerScript -Name 'AMORT.ps1' -RetainDays 3 -DryRun }
    # Short options that also prefix a PowerShell common parameter (-D for -Debug) belong to the
    # script. "amort -D" taken as -Debug once ran AMORT for real instead of a dry run.
    Assert-Run 'A switch prefix that also prefixes -Debug' 'DryRun=True RetainDays=14 Tag=none Rest=' { Invoke-AMORT -D }
    Assert-Run 'A direct call with that prefix' 'DryRun=True RetainDays=14 Tag=none Rest=' { Invoke-KillerScript -Name 'AMORT.ps1' -D }
    Assert-Run 'A positional name with that prefix' 'DryRun=True RetainDays=14 Tag=none Rest=' { Invoke-KillerScript AMORT -D }
    Assert-Run 'A dry run with a value after it' 'DryRun=True RetainDays=5 Tag=none Rest=' { Invoke-AMORT -D -R 5 }
    $failed = $false
    try { Invoke-AMORT -RetainDays 0 | Out-Null } catch { $failed = $_.Exception.Message -like '*RetainDays*' }
    if (-not $failed) { throw 'An out-of-range value was not refused by the script itself.' }
    Write-Output 'PASS wrapper and direct calls pass defaults, named, colon, alias, prefix and positional arguments through'

    # Always the latest: every run downloads again, to %TEMP%\<script>-<process id>, and runs what it got.
    $before = $global:KsDownloads.Count
    $global:KsArgumentFixture = "'newer version'"
    if ((Invoke-AMORT) -cne 'newer version') { throw 'A run did not use the version it just downloaded.' }
    if ($global:KsDownloads.Count -ne $before + 1) { throw 'A run did not download the script again.' }
    if ($global:KsDownloads[$global:KsDownloads.Count - 1] -cne 'https://raw.githubusercontent.com/SteveTheKiller/killer-scripts/main/AMORT.ps1') { throw 'The download did not come from killer-scripts main.' }
    if ([IO.File]::ReadAllText((Join-Path $root "AMORT-$PID.ps1")).Trim() -cne "'newer version'") { throw 'The script was not saved as %TEMP%\AMORT-<process id>.ps1.' }
    Write-Output 'PASS every run downloads the latest script to %TEMP% and runs it'

    # Each terminal tab is its own PowerShell process with its own copy, so two tabs running the
    # same command at once never write over the script the other is about to run. The website
    # one-liner's %TEMP%\AMORT.ps1 is not touched either.
    [IO.File]::WriteAllText((Join-Path $root 'AMORT.ps1'), "'another tab'")
    if ((Invoke-AMORT) -cne 'newer version') { throw 'A run ran a copy another process saved.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'AMORT.ps1')) -cne "'another tab'") { throw "A run wrote over another process's copy." }
    Write-Output 'PASS each process downloads to its own copy'

    # A download that fails stops the command. The copy an earlier run left in %TEMP% is not run.
    [IO.File]::WriteAllText((Join-Path $root "AMORT-$PID.ps1"), "[IO.File]::WriteAllText('$root\stale-ran.txt','ran')")
    $global:KsOffline = $true
    $failed = $false
    # A terminal tab runs with the default Continue preference, not this fixture's Stop.
    & { $ErrorActionPreference = 'Continue'; try { Invoke-AMORT 2>$null | Out-Null } catch { $script:failedRun = $true } }
    $failed = [bool]$script:failedRun
    $global:KsOffline = $false
    if (-not $failed) { throw 'A failed download did not stop the command.' }
    if (Test-Path (Join-Path $root 'stale-ran.txt')) { throw 'A failed download ran the stale copy left in %TEMP%.' }
    Write-Output 'PASS a failed download stops the command'

    Microsoft.PowerShell.Security\Set-ExecutionPolicy AllSigned -Scope Process -Force -ErrorAction Stop
    $savedArgumentBody = $global:KsArgumentFixture
    $global:KsArgumentFixture = "throw 'execution-policy-fixture-failure'"
    $executionFailed = $false
    $executionError = ''
    try { Invoke-KillerScript AMORT | Out-Null }
    catch {
        $executionError = $_.Exception.Message
        $executionFailed = $executionError -eq 'execution-policy-fixture-failure'
    }
    if (-not $executionFailed) { throw "The script failure did not reach the caller: $executionError" }
    if ((Get-ExecutionPolicy -Scope Process) -ne 'AllSigned') {
        throw 'A failed killer script did not restore the chosen AllSigned process policy.'
    }
    Write-Output 'PASS a failed script restores the chosen AllSigned process policy'
    $global:KsArgumentFixture = $savedArgumentBody
    Microsoft.PowerShell.Security\Set-ExecutionPolicy RemoteSigned -Scope Process -Force -ErrorAction Stop

    # Where Group Policy pins the execution policy, Set-ExecutionPolicy throws; the command still runs, quietly.
    function global:Set-ExecutionPolicy { throw 'Windows PowerShell updated your execution policy successfully, but the setting is overridden by a policy defined at a more specific scope.' }
    try {
        # Nothing red on screen: no error record in what the command writes.
        $written = & { $ErrorActionPreference = 'Continue'; Invoke-AMORT 2>&1 }
        $errors = @($written | Where-Object { $_ -is [Management.Automation.ErrorRecord] })
        if ($errors.Count) { throw "A pinned execution policy printed an error: $($errors[0])" }
        if ((@($written) -join '') -cne 'newer version') { throw "A run under a pinned policy returned '$written'." }
    } finally { Remove-Item function:global:Set-ExecutionPolicy }
    Write-Output 'PASS a pinned execution policy is not an error'

    # A run adds TLS 1.2 when the session lacks it, and leaves the rest of the session's TLS alone.
    $savedProtocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]0
        Invoke-AMORT | Out-Null
        if ([int][Net.ServicePointManager]::SecurityProtocol -ne 0) { throw "A run pinned the system default TLS to $([Net.ServicePointManager]::SecurityProtocol)." }
        $both = [Net.SecurityProtocolType]3072 -bor [Net.SecurityProtocolType]12288
        [Net.ServicePointManager]::SecurityProtocol = $both
        Invoke-AMORT | Out-Null
        if ([Net.ServicePointManager]::SecurityProtocol -ne $both) { throw "A run replaced the session TLS with $([Net.ServicePointManager]::SecurityProtocol)." }
    } finally { [Net.ServicePointManager]::SecurityProtocol = $savedProtocol }
    Write-Output 'PASS TLS settings kept'

    # The manifest's export list and the shipped script list name the same commands, so a script
    # added to the repo cannot ship without its command (PEEK once did).
    $moduleDir = Split-Path -Parent $ModulePath
    $shipped = [IO.File]::ReadAllText((Join-Path $moduleDir 'descriptions.json')) | ConvertFrom-Json
    $expected = @('Get-KillerScript', 'Invoke-KillerScript') + @($shipped.PSObject.Properties.Name | Where-Object { $_ -like '*.ps1' } | ForEach-Object { 'Invoke-' + ($_ -replace '\.ps1$', '') })
    $declared = @((Import-PowerShellDataFile $ModulePath).FunctionsToExport)
    $missing = @($expected | Where-Object { $declared -notcontains $_ })
    $extra = @($declared | Where-Object { $expected -notcontains $_ })
    if ($missing.Count -or $extra.Count) { throw "The manifest exports do not match descriptions.json. Missing: $($missing -join ', '). Extra: $($extra -join ', ')." }
    $exported = @((Get-Module KillerScripts).ExportedFunctions.Keys)
    $unexported = @($expected | Where-Object { $exported -notcontains $_ })
    if ($unexported.Count) { throw "The module does not export: $($unexported -join ', ')." }
    Write-Output "PASS all $($expected.Count - 2) shipped scripts have a command and match the manifest"

} finally {
    Microsoft.PowerShell.Security\Set-ExecutionPolicy $savedPolicy -Scope Process -Force -ErrorAction SilentlyContinue
    $env:TEMP = $savedTemp
    Remove-Module KillerScripts -Force -ErrorAction SilentlyContinue
    Remove-Item Function:\global:Invoke-RestMethod
    Remove-Variable KsArgumentFixture, KsDownloads -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
