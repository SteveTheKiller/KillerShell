#Requires -Version 5.1
<#
    KillerScripts
    Always-latest launcher for the killer-scripts repo.

    Every tool is pulled fresh from GitHub the moment you run it, so you always
    execute the current version. A local cache under %LOCALAPPDATA%\KillerScripts
    supports offline fallback and retains script versions for detached relaunches.

    Repo: https://github.com/SteveTheKiller/killer-scripts
#>

$script:RawBase  = 'https://raw.githubusercontent.com/SteveTheKiller/killer-scripts/main/'
$script:CacheDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'KillerScripts'

# Custom short aliases for scripts whose lowercase name would collide with a
# built-in (PRINT.ps1 -> KillerPrint, since 'print' is print.exe).
$script:AliasOverrides = @{ 'PRINT.ps1' = 'KillerPrint' }

function Set-KsTls {
    # GitHub requires TLS 1.2. Windows PowerShell 5.1 on older boxes can default lower.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    catch { }
}

function Get-KsCacheDir {
    if (-not (Test-Path -LiteralPath $script:CacheDir)) {
        New-Item -ItemType Directory -Path $script:CacheDir -Force | Out-Null
    }
    $script:CacheDir
}

function Open-KsCacheLease {
    param([string]$Path)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ($true) {
        try { return [IO.File]::Open("$Path.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) { throw "KillerScripts: cache is busy. Retry the operation. $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 25
        }
    }
}

function Write-KsCacheContent {
    param([string]$Path, [string]$Content)

    $temporary = Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.ks-stage-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $owned = $false
    try {
        $created = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $owned = $true
        $created.Dispose()
        $encoding = if ($PSVersionTable.PSVersion.Major -ge 6) { 'utf8BOM' } else { 'UTF8' }
        Set-Content -LiteralPath $temporary -Value $Content -Encoding $encoding -NoNewline -ErrorAction Stop
        if ([IO.File]::ReadAllText($temporary) -cne $Content) { throw 'KillerScripts: incomplete cache write.' }
        $completed = [IO.File]::Open($temporary, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try { $completed.Flush($true) } finally { $completed.Dispose() }
        $lease = Open-KsCacheLease -Path $Path
        try {
            if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Move($temporary, $Path) }
        } finally { $lease.Dispose() }
    } finally {
        if ($owned -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Get-KsWeb {
    param([Parameter(Mandatory = $true)][string]$Url)
    Set-KsTls
    (Invoke-WebRequest -Uri $Url -UseBasicParsing -ErrorAction Stop).Content
}

function Get-KsManifest {
    # Returns parsed descriptions.json. Live from the repo, cached on success,
    # read from cache only when the repo is unreachable.
    [CmdletBinding()]
    param()

    $cacheFile = Join-Path (Get-KsCacheDir) 'descriptions.json'
    try {
        $json = Get-KsWeb -Url ($script:RawBase + 'descriptions.json')
        $manifest = $json | ConvertFrom-Json -ErrorAction Stop
        if ($manifest -isnot [pscustomobject]) { throw 'KillerScripts: invalid manifest content.' }
        Write-KsCacheContent -Path $cacheFile -Content $json
    }
    catch {
        if (Test-Path -LiteralPath $cacheFile) {
            Write-Verbose 'KillerScripts: repo unreachable, using cached manifest.'
            $lease = Open-KsCacheLease -Path $cacheFile
            try { $json = [System.IO.File]::ReadAllText($cacheFile) }
            finally { $lease.Dispose() }
        }
        else {
            throw "KillerScripts: cannot reach the repo and no cached manifest exists. $($_.Exception.Message)"
        }
    }
    $json | ConvertFrom-Json
}

function Get-KillerScript {
    # List the available killer-scripts with their titles and descriptions.
    [CmdletBinding()]
    param()
    $m = Get-KsManifest
    $out = foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        [pscustomobject]@{
            Command     = 'Invoke-' + ($p.Name -replace '\.ps1$', '')
            Script      = $p.Name
            Name        = $p.Value.name
            Description = $p.Value.description
        }
    }
    $out | Sort-Object Script
}

function Invoke-KillerScript {
    # Fetch the latest version of a killer-script from the repo and run it.
    # Falls back to the cached copy only if the repo is unreachable.
    #
    # A plain function on purpose, with no [CmdletBinding()] and no [Parameter()]: either one adds
    # the common parameters, and then a short script option such as "-D" binds to -Debug and never
    # reaches the script. Only -Name is declared, which keeps it positional and tab-completable;
    # -Offline and -ScriptArgs are read from $args by their exact full names, because a declared
    # parameter in a plain function is positional and would take a value meant for the script.
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { throw 'KillerScripts: name the script to run, for example Invoke-KillerScript AMORT.' }
    $offline = $false
    $forward = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $args.Count; $i++) {
        $item = $args[$i]
        if ($item -is [string] -and $item -ieq '-Offline') { $offline = $true; continue }
        if ($item -is [string] -and $item -ieq '-Offline:' -and $i + 1 -lt $args.Count) { $offline = [bool]$args[++$i]; continue }
        if ($item -is [string] -and $item -ieq '-ScriptArgs' -and $i + 1 -lt $args.Count) {
            foreach ($value in @($args[++$i])) { $forward.Add($value) }
            continue
        }
        $forward.Add($item)
    }
    Invoke-KsCore -Name $Name -Offline:$offline -Arguments $forward.ToArray()
}

function Invoke-KsCore {
    # Not exported. The argument list is one value here, never a splat, so no token in it can be
    # captured by a parameter of this function on the way through.
    param([string]$Name, [switch]$Offline, [object[]]$Arguments)
    $ScriptArgs = $Arguments

    $file      = if ($Name -like '*.ps1') { $Name } else { "$Name.ps1" }
    $cachePath = Join-Path (Get-KsCacheDir) $file

    if (-not $Offline) {
        try {
            $body = Get-KsWeb -Url ($script:RawBase + $file)
            $parseTokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseInput($body, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
            if ([string]::IsNullOrWhiteSpace($body) -or $parseErrors.Count) { throw "KillerScripts: invalid script content for $file." }
            Write-KsCacheContent -Path $cachePath -Content $body
        }
        catch {
            if (Test-Path -LiteralPath $cachePath) {
                Write-Warning "KillerScripts: could not fetch $file, running the cached copy."
            }
            else {
                throw "KillerScripts: could not fetch $file and there is no cached copy. $($_.Exception.Message)"
            }
        }
    }
    elseif (-not (Test-Path -LiteralPath $cachePath)) {
        throw "KillerScripts: no cached copy of $file. Run it once online first."
    }

    # Detached relaunches can load this path after the original caller exits.
    # Retain one immutable file per content version beside the cache for PSScriptRoot.
    # Caller exit or age alone cannot prove that a version is no longer needed.
    $lease = Open-KsCacheLease -Path $cachePath
    try {
        $bytes = [IO.File]::ReadAllBytes($cachePath)
        $hash = [Security.Cryptography.SHA256]::Create()
        try {
            $version = [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '')
            # A compact filename preserves the PS5.1 path budget. The complete hash
            # still verifies the content below, so a filename collision is refused.
            $snapshot = Join-Path ([IO.Path]::GetDirectoryName($cachePath)) ('.ks-v-' + $version.Substring(0, 32) + '.ps1')
            if (-not [IO.File]::Exists($snapshot)) {
                $temporary = Join-Path ([IO.Path]::GetDirectoryName($cachePath)) ('.ks-stage-' + [guid]::NewGuid().ToString('N') + '.tmp')
                $owned = $false
                try {
                    $created = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $owned = $true
                    try {
                        $created.Write($bytes, 0, $bytes.Length)
                        $created.Flush($true)
                    } finally { $created.Dispose() }
                    try { [IO.File]::Move($temporary, $snapshot) }
                    catch [IO.IOException] {
                        # Another script with identical content may publish this version first.
                        if (-not [IO.File]::Exists($snapshot)) { throw }
                    }
                } finally {
                    if ($owned -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
                }
            }
            $existingVersion = [BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($snapshot))).Replace('-', '')
            if ($existingVersion -cne $version) { throw "KillerScripts: retained script version has changed: $snapshot" }
        } finally { $hash.Dispose() }
    } finally { $lease.Dispose() }
    $parseTokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($snapshot, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count) { throw "KillerScripts: invalid cached script for $file." }
    $invocation = ConvertTo-KsInvocation -Path $snapshot -Arguments $ScriptArgs
    $named = $invocation.Named
    $positional = $invocation.Positional
    & $snapshot @named @positional
}

function ConvertTo-KsInvocation {
    # A remaining-arguments list loses its parameter names when it is splatted again: every item
    # arrives positionally, so "-RetainDays 30" reaches the script as two plain values and an empty
    # call can hand it a lone $null that lands on its first positional parameter. Rebuild the named
    # and positional arguments against the script's own parameters, the way the binder reads a
    # typed command line.
    param([string]$Path, [object[]]$Arguments)
    $named = @{}
    $positional = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Arguments) { return @{ Named = $named; Positional = @() } }

    $parameters = $ExecutionContext.InvokeCommand.GetCommand($Path, [System.Management.Automation.CommandTypes]::ExternalScript).Parameters
    $lookup = @{}
    foreach ($parameter in $parameters.Values) {
        $lookup[$parameter.Name] = $parameter
        foreach ($alias in $parameter.Aliases) { $lookup[$alias] = $parameter }
    }

    $i = 0
    while ($i -lt $Arguments.Count) {
        $item = $Arguments[$i]
        $i++
        if (-not ($item -is [string] -and $item -match '^-([A-Za-z_][\w-]*)(:?)$')) { $positional.Add($item); continue }
        $token = $Matches[1]
        $colon = $Matches[2] -eq ':'
        $parameter = $lookup[$token]
        if ($null -eq $parameter) {
            $candidates = @($lookup.Keys | Where-Object { $_ -like "$token*" } | ForEach-Object { $lookup[$_].Name } | Select-Object -Unique)
            if ($candidates.Count -gt 1) { throw "KillerScripts: parameter -$token is ambiguous. Possible matches: -$($candidates -join ', -')." }
            if ($candidates.Count -eq 1) { $parameter = $parameters[$candidates[0]] }
        }
        # Not one of the script's parameters: pass it on as written and let the script decide.
        if ($null -eq $parameter) { $positional.Add($item); continue }
        if ($named.ContainsKey($parameter.Name)) { throw "KillerScripts: parameter -$($parameter.Name) is specified more than once." }
        if (-not $colon -and $parameter.SwitchParameter) { $named[$parameter.Name] = $true; continue }
        if ($i -ge $Arguments.Count) { throw "KillerScripts: missing an argument for parameter -$($parameter.Name)." }
        $named[$parameter.Name] = $Arguments[$i]
        $i++
    }
    @{ Named = $named; Positional = $positional.ToArray() }
}

function Update-KillerScripts {
    # Refresh the cached manifest and pre-download every script for offline use.
    [CmdletBinding()]
    param()
    $m = Get-KsManifest
    $count = 0
    foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        try {
            $body = Get-KsWeb -Url ($script:RawBase + $p.Name)
            $parseTokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseInput($body, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
            if ([string]::IsNullOrWhiteSpace($body) -or $parseErrors.Count) { throw "KillerScripts: invalid script content for $($p.Name)." }
            Write-KsCacheContent -Path (Join-Path (Get-KsCacheDir) $p.Name) -Content $body
            $count++
        }
        catch {
            Write-Warning "KillerScripts: failed to cache $($p.Name): $($_.Exception.Message)"
        }
    }
    Write-Host "KillerScripts: cached $count scripts for offline use."
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

# Build a friendly wrapper (Invoke-<NAME>) and short alias (<name>) per script from
# the manifest shipped with this version. Content is still pulled live at run time.
# New scripts get wrappers after the next publish (Build-KillerScripts.ps1).
$script:KsFunctions = @('Get-KillerScript', 'Invoke-KillerScript', 'Update-KillerScripts')
$script:KsAliases   = @()

$shippedManifest = Join-Path $PSScriptRoot 'descriptions.json'
if (Test-Path -LiteralPath $shippedManifest) {
    $m = [System.IO.File]::ReadAllText($shippedManifest) | ConvertFrom-Json
    foreach ($p in $m.PSObject.Properties) {
        if ($p.Name -notlike '*.ps1') { continue }
        $base = $p.Name -replace '\.ps1$', ''
        $fn   = "Invoke-$base"
        # Bound to this module so the wrapper can reach the private Invoke-KsCore.
        Set-Item -Path "function:\$fn" -Value $ExecutionContext.SessionState.Module.NewBoundScriptBlock([ScriptBlock]::Create(
            # No param block: a [Parameter()] attribute would make the wrapper advanced, and its
            # common parameters would swallow script options ("amort -D" became -Debug). $args
            # carries every token as typed and is handed on as one value, never splatted.
            "Invoke-KsCore -Name '$($p.Name)' -Arguments `$args"
        )) -Force
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
