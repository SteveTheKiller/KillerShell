#Requires -Version 5.1
<#
    KillerScripts
    One command per killer script, for example amort or Invoke-AMORT.

    Each command does exactly what the website's one-liner does: allow scripts for this process,
    download the latest script from killer-scripts main to %TEMP%, and run it. Every option typed
    after the command goes to the script as typed.

    Repo: https://github.com/SteveTheKiller/killer-scripts
#>

$script:RawBase = 'https://raw.githubusercontent.com/SteveTheKiller/killer-scripts/main/'

# Custom short aliases for scripts whose lowercase name would collide with a
# built-in (PRINT.ps1 -> KillerPrint, since 'print' is print.exe).
$script:AliasOverrides = @{ 'PRINT.ps1' = 'KillerPrint' }

function Get-KsLatest {
    # The website's one-liner, minus the run: Bypass for this process, then the latest copy of the
    # script saved to %TEMP%\<script>. Returns the path to run.
    param([string]$Name)
    $file = if ($Name -like '*.ps1') { $Name } else { "$Name.ps1" }
    Set-ExecutionPolicy Bypass -Scope Process -Force -ErrorAction SilentlyContinue
    # GitHub requires TLS 1.2, which Windows PowerShell 5.1 on older systems does not enable on its own.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $path = Join-Path $env:TEMP $file
    Invoke-RestMethod -Uri ($script:RawBase + $file) -OutFile $path
    $path
}

function Invoke-KillerScript {
    # Run any killer script by name: Invoke-KillerScript AMORT -DryRun.
    #
    # A plain function on purpose, with no [CmdletBinding()] and no [Parameter()]: either one adds
    # PowerShell's common parameters, and then a short script option such as -D is taken as -Debug
    # and never reaches the script. Splatting @args from a plain function keeps every option's name.
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { throw 'KillerScripts: name the script to run, for example Invoke-KillerScript AMORT.' }
    & (Get-KsLatest $Name) @args
}

function Get-KillerScript {
    # List the killer scripts with their titles and descriptions, from the repo's own list, or from
    # the list this module shipped with when the repo cannot be reached.
    try {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        $m = Invoke-RestMethod -Uri ($script:RawBase + 'descriptions.json')
    }
    catch {
        $m = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'descriptions.json')) | ConvertFrom-Json
    }
    $out = foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        $base = $p.Name -replace '\.ps1$', ''
        [pscustomobject]@{
            # A script newer than this module has no command of its own yet, so name the form that works.
            Command     = if ($script:KsFunctions -contains "Invoke-$base") { "Invoke-$base" } else { "Invoke-KillerScript $base" }
            Script      = $p.Name
            Name        = $p.Value.name
            Description = $p.Value.description
        }
    }
    $out | Sort-Object Script
}

# Tab completion of -Name, sourced from the manifest that shipped with the module.
Register-ArgumentCompleter -CommandName Invoke-KillerScript -ParameterName Name -ScriptBlock {
    param($commandName, $parameterName, $wordToComplete)
    $shipped = Join-Path $PSScriptRoot 'descriptions.json'
    if (-not (Test-Path -LiteralPath $shipped)) { return }
    $m = [System.IO.File]::ReadAllText($shipped) | ConvertFrom-Json
    foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        $base = $p.Name -replace '\.ps1$', ''
        if ($base -like "$wordToComplete*") {
            [System.Management.Automation.CompletionResult]::new($base, $base, 'ParameterValue', $p.Value.name)
        }
    }
}

# One wrapper (Invoke-<NAME>) and short alias (<name>) per script in the manifest shipped with this
# version. The script itself is downloaded fresh every run.
$script:KsFunctions = @('Get-KillerScript', 'Invoke-KillerScript')
$script:KsAliases   = @()

$shippedManifest = Join-Path $PSScriptRoot 'descriptions.json'
if (Test-Path -LiteralPath $shippedManifest) {
    $m = [System.IO.File]::ReadAllText($shippedManifest) | ConvertFrom-Json
    foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        $base = $p.Name -replace '\.ps1$', ''
        $fn   = "Invoke-$base"
        # No param block: a [Parameter()] attribute would make the wrapper advanced, and its common
        # parameters would take script options ("amort -D" became -Debug). Bound to this module so
        # it can reach the private Get-KsLatest.
        Set-Item -Path "function:\$fn" -Value $ExecutionContext.SessionState.Module.NewBoundScriptBlock(
            [ScriptBlock]::Create("& (Get-KsLatest '$($p.Name)') @args")) -Force
        $script:KsFunctions += $fn

        # Use a custom alias where defined, otherwise the lowercase name. Only
        # claim it when nothing already owns that name, so built-ins like
        # print.exe and any future collision stay untouched.
        $short = if ($script:AliasOverrides.ContainsKey($p.Name)) { $script:AliasOverrides[$p.Name] } else { $base.ToLower() }
        if (-not (Get-Command -Name $short -ErrorAction SilentlyContinue)) {
            Set-Alias -Name $short -Value $fn -Scope Script -Force
            $script:KsAliases += $short
        }
    }
}

Export-ModuleMember -Function $script:KsFunctions -Alias $script:KsAliases
