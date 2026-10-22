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
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Name,

        [switch]$Offline,

        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]]$ScriptArgs
    )

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
            $snapshot = Join-Path ([IO.Path]::GetDirectoryName($cachePath)) ('.ks-version-' + $version + '.ps1')
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
    & $snapshot @ScriptArgs
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
        Set-Item -Path "function:\$fn" -Value ([ScriptBlock]::Create(
            "param([Parameter(ValueFromRemainingArguments=`$true)]`$ScriptArgs) Invoke-KillerScript -Name '$($p.Name)' @ScriptArgs"
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
