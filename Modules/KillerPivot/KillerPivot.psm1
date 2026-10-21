<#
.SYNOPSIS
KillerPivot - Per-Identity Validated Org-Toggle (P.I.V.O.T.) v1.0.0
Developed by SteveTheKiller | Updated: 2026-07-24

.DESCRIPTION
Multi-tenant connect helper for Exchange Online and Microsoft Graph.
Confirms the tenant ID after every connect and tears the session down
on mismatch, so a wrong-tenant session never survives to the prompt.
Stores identifiers only, never credentials. The tenant list lives
outside the module at %LOCALAPPDATA%\PIVOT\tenants.json, so the module
itself contains no client data and can be shared unchanged.
#>

#region 0: PALETTE & OUTPUT HELPERS
# ------------------------------------------------------------------------------

$script:Width = 85
$LineCol      = "DarkMagenta"
$MainCol      = "Green"
$AccentCol    = "Magenta"
$DimCol       = "DarkGray"

function Write-StatusTag {
    param([string]$Text, [string]$Color)
    try {
        $cur = [Console]::CursorLeft
        $pad = $script:Width - $cur - $Text.Length
        if ($pad -gt 0) { Write-Host (" " * $pad) -NoNewline }
    } catch { Write-Host " " -NoNewline }
    Write-Host $Text -ForegroundColor $Color
}

function Write-HLine {
    param(
        [string]$Style = "dashed",
        [int]$Width = $script:Width
    )

    if ($Style -eq "dashed") {
        $line = ("- " * [math]::Ceiling($Width / 2)).Substring(0, $Width)
    } else {
        $line = "━" * $Width
    }

    $colors = @(
        [ConsoleColor]$LineCol,
        [ConsoleColor]$AccentCol,
        [ConsoleColor]$AccentCol,
        [ConsoleColor]$DimCol
    )

    $useConsole = $true
    try { $saved = [Console]::ForegroundColor } catch { $useConsole = $false }

    $i = 0
    foreach ($char in $line.ToCharArray()) {
        if ($char -eq ' ') {
            $fg = [ConsoleColor]$DimCol
        } else {
            $fg = $colors[$i % $colors.Count]
            $i++
        }
        if ($useConsole) {
            [Console]::ForegroundColor = $fg
            [Console]::Write($char)
        } else {
            Write-Host $char -NoNewline -ForegroundColor $fg
        }
    }

    if ($useConsole) {
        [Console]::ForegroundColor = $saved
        [Console]::WriteLine()
    } else {
        Write-Host ""
    }
}

function Show-Header {
    $_pfx  = "█ "
    $_art1 = "╔═╗ ╦ ╦  ╦ ╔═╗ ╔╦╗ "
    $_art2 = "╠═╝ ║ ╚╗╔╝ ║ ║  ║  "
    $_art3 = "╩   ╩  ╚╝  ╚═╝  ╩  "

    $_artW  = [Math]::Max($_art1.Length, [Math]::Max($_art2.Length, $_art3.Length))
    $_art1  = $_art1.PadRight($_artW); $_art2 = $_art2.PadRight($_artW); $_art3 = $_art3.PadRight($_artW)
    $_fillW = $script:Width - $_pfx.Length - $_artW
    $_title = "PER-IDENTITY VALIDATED ORG-TOGGLE"
    $_tpad  = " " * [Math]::Max(0, ($_fillW - $_title.Length))

    Write-Host $_pfx -ForegroundColor $LineCol -NoNewline; Write-Host $_art1 -ForegroundColor $AccentCol -NoNewline; Write-Host ("-" * $_fillW) -ForegroundColor $LineCol
    Write-Host $_pfx -ForegroundColor $LineCol -NoNewline; Write-Host $_art2 -ForegroundColor $AccentCol -NoNewline; Write-Host "$_title$_tpad" -ForegroundColor $MainCol
    Write-Host $_pfx -ForegroundColor $LineCol -NoNewline; Write-Host $_art3 -ForegroundColor $AccentCol -NoNewline; Write-Host ("-" * $_fillW) -ForegroundColor $LineCol
}

#endregion

#region 1: CONFIG STORE
# ------------------------------------------------------------------------------
# Identifiers only. No secrets, no credentials, no tokens.

function Get-PivotConfigPath {
    # PIVOT_CONFIG lets the list live somewhere backed up (OneDrive, a synced
    # folder). Unset falls back to LOCALAPPDATA, so a fresh install still works.
    if (-not [string]::IsNullOrWhiteSpace($env:PIVOT_CONFIG)) {
        return $env:PIVOT_CONFIG
    }
    Join-Path $env:LOCALAPPDATA 'PIVOT\tenants.json'
}

function ConvertFrom-PivotConfig {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Raw)

    if ([string]::IsNullOrWhiteSpace($Raw)) { throw 'PIVOT: existing configuration is empty. Restore or repair it before making changes.' }
    $obj = $Raw | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $obj -or ($obj.schema -isnot [int] -and $obj.schema -isnot [long]) -or $obj.schema -ne 1 -or
        $obj.tenants -isnot [System.Array]) {
        throw 'PIVOT: invalid configuration schema. Expected schema 1 and a tenants array.'
    }
    $keys = @{}
    foreach ($tenant in $obj.tenants) {
        if ($tenant.key -isnot [string] -or $tenant.key -cnotmatch '^[a-z0-9][a-z0-9-]{0,31}$' -or
            $tenant.name -isnot [string] -or [string]::IsNullOrWhiteSpace($tenant.name) -or
            $tenant.upn -isnot [string] -or $tenant.upn -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$' -or
            $tenant.tenantId -isnot [string] -or $tenant.tenantId -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$' -or
            $keys.ContainsKey($tenant.key)) {
            throw 'PIVOT: invalid or duplicate tenant entry. Restore or repair the configuration before making changes.'
        }
        $keys[$tenant.key] = $true
    }
    return @($obj.tenants)
}

function Read-PivotConfig {
    param([switch]$ForUpdate)

    $path = [IO.Path]::GetFullPath((Get-PivotConfigPath))
    $exists = $true
    $bytes = $null
    try { $bytes = [IO.File]::ReadAllBytes($path) }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) {
            $exists = $false
        } else { throw "PIVOT: cannot read configuration '$path'. $($cause.Message)" }
    }
    $tenants = @()
    $snapshot = $null
    if ($exists) {
        $snapshot = [Convert]::ToBase64String($bytes)
        $stream = [IO.MemoryStream]::new($bytes, $false)
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
        try { $tenants = @(ConvertFrom-PivotConfig -Raw $reader.ReadToEnd()) }
        finally { $reader.Dispose() }
    }
    if ($ForUpdate) { return [pscustomobject]@{ Path=$path; Exists=$exists; Snapshot=$snapshot; Tenants=$tenants } }
    return $tenants
}

function Write-PivotConfig {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Tenants,
        [Parameter(Mandatory)][object]$Expected
    )

    $path = $Expected.Path
    $dir = [IO.Path]::GetDirectoryName($path)
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $lock = $null
    $temporary = $null
    $temporaryOwned = $false
    try {
        # Cooperating writers serialize publication, then reject stale reads.
        $lock = [IO.File]::Open("$path.lock", [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $current = Read-PivotConfig -ForUpdate
        if ($current.Path -ne $path -or $current.Exists -ne $Expected.Exists -or $current.Snapshot -cne $Expected.Snapshot) {
            throw 'PIVOT: configuration changed during this operation. Nothing written. Reload and retry.'
        }
        $payload = [ordered]@{
            schema  = 1
            updated = (Get-Date).ToString('yyyy-MM-dd')
            tenants = @($Tenants | Sort-Object key)
        }
        $json = $payload | ConvertTo-Json -Depth 5
        ConvertFrom-PivotConfig -Raw $json | Out-Null
        $temporary = Join-Path $dir ([IO.Path]::GetRandomFileName())
        $file = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $temporaryOwned = $true
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
            $file.Write($bytes, 0, $bytes.Length)
            $file.Flush($true)
        } finally { $file.Dispose() }
        if ($Expected.Exists) {
            $backup = "$path.previous.$([guid]::NewGuid().ToString('N'))"
            [IO.File]::Replace($temporary, $path, $backup)
        } else {
            [IO.File]::Move($temporary, $path)
        }
        $temporary = $null
    } finally {
        try {
            if ($temporaryOwned -and $temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        } finally { if ($lock) { $lock.Dispose() } }
    }
}

function Resolve-PivotTenant {
    param([Parameter(Mandatory)][string]$Key)

    $all = Read-PivotConfig
    $hit = @($all | Where-Object { $_.key -eq $Key })

    if ($hit.Count -eq 0) {
        $known = ($all | ForEach-Object { $_.key }) -join ', '
        if ([string]::IsNullOrWhiteSpace($known)) {
            throw "PIVOT: no tenants configured. Run Add-PivotTenant to add one."
        }
        throw "PIVOT: no tenant with key '$Key'. Known keys: $known"
    }
    if ($hit.Count -gt 1) {
        throw "PIVOT: duplicate key '$Key' in config. Fix $(Get-PivotConfigPath)."
    }
    return $hit[0]
}

#endregion

#region 2: TENANT DISCOVERY
# ------------------------------------------------------------------------------
# The OIDC discovery document is public and unauthenticated. Its issuer URL
# carries the tenant GUID, which saves hunting for it in the portal.

function Get-PivotTenantIdFromDomain {
    param([Parameter(Mandatory)][string]$Domain)

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    $uri = "https://login.microsoftonline.com/$Domain/v2.0/.well-known/openid-configuration"
    try {
        $resp = Invoke-RestMethod -Uri $uri -ErrorAction Stop
    } catch {
        return $null
    }

    if ($resp.issuer -match '([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})') {
        return $Matches[1].ToLower()
    }
    return $null
}

function Get-PivotBrandName {
    param([Parameter(Mandatory)][string]$Upn)

    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    $uri = "https://login.microsoftonline.com/getuserrealm.srf?login=$Upn&api-version=2.1"
    try {
        $resp = Invoke-RestMethod -Uri $uri -ErrorAction Stop
    } catch {
        return $null
    }

    $brand = $resp.FederationBrandName
    if ([string]::IsNullOrWhiteSpace($brand)) { return $null }
    return $brand
}

function ConvertTo-PivotKey {
    param([Parameter(Mandatory)][string]$Domain)

    $stem = ($Domain -split '\.')[0]
    $stem = ($stem -replace '[^a-zA-Z0-9-]', '').ToLower()
    if ($stem.Length -gt 32) { $stem = $stem.Substring(0, 32) }
    if ($stem -notmatch '^[a-z0-9]') { $stem = "t$stem" }
    return $stem
}

#endregion

#region 3: TENANT LIST MANAGEMENT
# ------------------------------------------------------------------------------

function Add-PivotTenant {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')][string]$Key,
        [string]$Upn,
        [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')][string]$TenantId,
        [string]$Name,
        [switch]$Force
    )

    $interactive = [string]::IsNullOrWhiteSpace($Upn)

    # --- UPN ---
    if ($interactive) {
        Write-Host ""
        Write-Host "[*] Add a tenant to PIVOT" -ForegroundColor $LineCol
        Write-HLine -Style "dashed"
        while ([string]::IsNullOrWhiteSpace($Upn) -or $Upn -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
            $Upn = (Read-Host " > Admin UPN").Trim()
            if ($Upn -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
                Write-Host " [!] That does not look like a UPN. Try again." -ForegroundColor Yellow
            }
        }
    }

    $domain = ($Upn -split '@')[-1]

    # --- Tenant ID ---
    if (-not $TenantId) {
        Write-Host " > Looking up tenant for $domain..." -NoNewline -ForegroundColor $DimCol
        $TenantId = Get-PivotTenantIdFromDomain -Domain $domain
        if ($TenantId) {
            Write-StatusTag "[FOUND]" $MainCol
            Write-Host "   Tenant ID : $TenantId" -ForegroundColor $AccentCol
        } else {
            Write-StatusTag "[NOT FOUND]" "Yellow"
            if (-not $interactive) {
                throw "PIVOT: could not resolve a tenant ID for '$domain'. Pass -TenantId explicitly."
            }
            while ($TenantId -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
                $TenantId = (Read-Host " > Tenant ID (GUID)").Trim()
                if ($TenantId -notmatch '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') {
                    Write-Host " [!] That is not a GUID. Try again." -ForegroundColor Yellow
                }
            }
        }
        $TenantId = $TenantId.ToLower()
    }

    # --- Display name ---
    if (-not $Name) {
        $suggested = Get-PivotBrandName -Upn $Upn
        if ([string]::IsNullOrWhiteSpace($suggested)) { $suggested = $domain }
        if ($interactive) {
            $entered = (Read-Host " > Display name [$suggested]").Trim()
            if ([string]::IsNullOrWhiteSpace($entered)) { $Name = $suggested } else { $Name = $entered }
        } else {
            $Name = $suggested
        }
    }

    # --- Short key ---
    if (-not $Key) {
        $suggestedKey = ConvertTo-PivotKey -Domain $domain
        if ($interactive) {
            while ($true) {
                $entered = (Read-Host " > Short key [$suggestedKey]").Trim().ToLower()
                if ([string]::IsNullOrWhiteSpace($entered)) { $entered = $suggestedKey }
                if ($entered -match '^[a-z0-9][a-z0-9-]{0,31}$') { $Key = $entered; break }
                Write-Host " [!] Lowercase letters, numbers and dashes only. Try again." -ForegroundColor Yellow
            }
        } else {
            $Key = $suggestedKey
        }
    }

    # --- Collision check ---
    $store = Read-PivotConfig -ForUpdate
    $all = @($store.Tenants)
    if (($all | Where-Object { $_.key -eq $Key }) -and -not $Force) {
        if ($interactive) {
            $ow = (Read-Host " > Key '$Key' already exists. Overwrite? (y/N)").Trim()
            if ($ow -notmatch '^[Yy]') {
                Write-Host "[!] Canceled. Nothing written." -ForegroundColor Yellow
                return
            }
        } else {
            throw "PIVOT: key '$Key' already exists. Use -Force to overwrite."
        }
    }

    # --- Confirm ---
    if ($interactive) {
        Write-HLine -Style "dashed"
        Write-Host "[>] Key       : " -NoNewline -ForegroundColor $LineCol; Write-Host $Key      -ForegroundColor $AccentCol
        Write-Host "[>] Name      : " -NoNewline -ForegroundColor $LineCol; Write-Host $Name     -ForegroundColor $AccentCol
        Write-Host "[>] UPN       : " -NoNewline -ForegroundColor $LineCol; Write-Host $Upn      -ForegroundColor $AccentCol
        Write-Host "[>] Tenant ID : " -NoNewline -ForegroundColor $LineCol; Write-Host $TenantId -ForegroundColor $AccentCol
        Write-HLine -Style "dashed"

        $confirm = (Read-Host " > Save? (Y/n)").Trim()
        if ($confirm -match '^[Nn]') {
            Write-Host "[!] Canceled. Nothing written." -ForegroundColor Yellow
            return
        }
    }

    $entry = [pscustomobject]@{
        key      = $Key
        name     = $Name
        upn      = $Upn
        tenantId = $TenantId.ToLower()
    }
    $new = @(@($all | Where-Object { $_.key -ne $Key }) + $entry)

    if ($PSCmdlet.ShouldProcess($Key, "Write tenant entry")) {
        Write-PivotConfig -Tenants $new -Expected $store
        Write-Host "[>] Saved: " -NoNewline -ForegroundColor $LineCol
        Write-Host "$Key ($Name)" -ForegroundColor $AccentCol
        if ($interactive) {
            Write-Host "    Connect with: " -NoNewline -ForegroundColor $DimCol
            Write-Host "pivot $Key" -ForegroundColor $MainCol
        }
    }
}

function Remove-PivotTenant {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Key)

    $store = Read-PivotConfig -ForUpdate
    $all = @($store.Tenants)
    if (-not ($all | Where-Object { $_.key -eq $Key })) {
        throw "PIVOT: no tenant with key '$Key'."
    }
    if ($PSCmdlet.ShouldProcess($Key, "Remove tenant entry")) {
        Write-PivotConfig -Tenants @($all | Where-Object { $_.key -ne $Key }) -Expected $store
        Write-Host "[>] Removed: " -NoNewline -ForegroundColor $LineCol
        Write-Host $Key -ForegroundColor $AccentCol
    }
}

function Get-PivotTenant {
    param([string]$Key)
    $all = Read-PivotConfig
    if ($Key) { $all = $all | Where-Object { $_.key -like $Key } }
    $all | Select-Object key, name, upn, tenantId
}

#endregion

#region 4: SESSION STATE
# ------------------------------------------------------------------------------
# The header lives here, not on connect. This is the deliberate "where am I"
# status display, so it earns the full block. Connect stays terse.

function Get-PivotContext {
    Show-Header

    $exo = $null
    if (Get-Command Get-ConnectionInformation -ErrorAction SilentlyContinue) {
        $exo = Get-ConnectionInformation -ErrorAction SilentlyContinue |
               Where-Object { $_.State -eq 'Connected' } | Select-Object -First 1
    }

    Write-Host "[>] Exchange     : " -NoNewline -ForegroundColor $LineCol
    if ($exo) {
        Write-Host $exo.Organization -ForegroundColor $AccentCol
        Write-Host "    Account      : $($exo.UserPrincipalName)" -ForegroundColor $DimCol
        Write-Host "    Tenant       : $($exo.TenantID)" -ForegroundColor $DimCol
    } else {
        Write-Host "not connected" -ForegroundColor $DimCol
    }

    $mg = $null
    if (Get-Command Get-MgContext -ErrorAction SilentlyContinue) {
        $mg = Get-MgContext -ErrorAction SilentlyContinue
    }

    Write-Host "[>] Graph        : " -NoNewline -ForegroundColor $LineCol
    if ($mg) {
        Write-Host $mg.Account -ForegroundColor $AccentCol
        Write-Host "    Tenant       : $($mg.TenantId)" -ForegroundColor $DimCol
    } else {
        Write-Host "not connected" -ForegroundColor $DimCol
    }

    Write-HLine -Style "dashed"
}

function Disconnect-PivotTenant {
    [CmdletBinding()]
    param()

    $failures = @()
    if (Get-Command Disconnect-ExchangeOnline -ErrorAction SilentlyContinue) {
        try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction Stop }
        catch { $failures += "Exchange teardown failed: $($_.Exception.Message)" }
    }
    $graphContextCommand = Get-Command Get-MgContext -ErrorAction SilentlyContinue
    if (Get-Command Disconnect-MgGraph -ErrorAction SilentlyContinue) {
        $disconnectGraph = $true
        if ($graphContextCommand) {
            try { $disconnectGraph = $null -ne (Get-MgContext -ErrorAction Stop) }
            catch { $failures += "Graph state query failed: $($_.Exception.Message)" }
        }
        if ($disconnectGraph) {
            try { Disconnect-MgGraph -ErrorAction Stop | Out-Null }
            catch { $failures += "Graph teardown failed: $($_.Exception.Message)" }
        }
    }
    if (Get-Command Get-ConnectionInformation -ErrorAction SilentlyContinue) {
        try {
            $remaining = @(Get-ConnectionInformation -ErrorAction Stop | Where-Object { $_.State -eq 'Connected' })
            if ($remaining.Count) { $failures += 'Exchange still reports connected sessions.' }
        } catch { $failures += "Exchange verification failed: $($_.Exception.Message)" }
    } elseif (Get-Command Disconnect-ExchangeOnline -ErrorAction SilentlyContinue) {
        $failures += 'Exchange disconnect could not be verified by this provider version.'
    }
    if ($graphContextCommand) {
        try {
            if (Get-MgContext -ErrorAction Stop) { $failures += 'Graph still reports a connected context.' }
        } catch { $failures += "Graph verification failed: $($_.Exception.Message)" }
    } elseif (Get-Command Disconnect-MgGraph -ErrorAction SilentlyContinue) {
        $failures += 'Graph disconnect could not be verified by this provider version.'
    }
    if ($failures.Count) {
        throw "PIVOT: disconnect incomplete. $($failures -join '; ') Close this PowerShell session before running tenant commands."
    }
    Write-Host "[>] All sessions closed." -ForegroundColor $DimCol
}

#endregion

#region 5: CONNECT & VALIDATE
# ------------------------------------------------------------------------------
# EXO returns whatever the broker decided to hand back, so the tenant is not
# known until a session exists. Confirm it, and kill the session if it is wrong.
# Quiet on success (two lines), loud on mismatch.

function Connect-PivotTenant {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Key,
        [switch]$Graph,
        [string[]]$Scopes = @('User.Read.All'),
        [switch]$KeepExisting
    )

    $t = Resolve-PivotTenant -Key $Key

    if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
        throw "PIVOT: ExchangeOnlineManagement not found. Check with Get-Module -ListAvailable ExchangeOnlineManagement."
    }

    $beforeIds = @()
    $connectionPrefix = $null
    if ($KeepExisting) {
        $connectCommand = Get-Command Connect-ExchangeOnline -ErrorAction Stop
        $disconnectCommand = Get-Command Disconnect-ExchangeOnline -ErrorAction Stop
        if (-not $connectCommand.Parameters.ContainsKey('Prefix') -or
            -not $disconnectCommand.Parameters.ContainsKey('ModulePrefix') -or
            -not $disconnectCommand.Parameters.ContainsKey('ConnectionId')) {
            throw 'PIVOT: KeepExisting requires Exchange connection identity and targeted cleanup support. Update ExchangeOnlineManagement.'
        }
        $before = @(Get-ConnectionInformation -ErrorAction Stop | Where-Object { $_.State -eq 'Connected' })
        if (@($before | Where-Object { [string]::IsNullOrWhiteSpace($_.ConnectionId) }).Count) {
            throw 'PIVOT: existing Exchange connection identity is unavailable. Nothing connected.'
        }
        $beforeIds = @($before | ForEach-Object { $_.ConnectionId })
        $connectionPrefix = 'Pivot' + [guid]::NewGuid().ToString('N')
    } else {
        Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
    }

    # -DisableWAM forces a real prompt. Without it the Windows broker silently
    # reuses the last account and -UserPrincipalName is treated as a hint only.
    $exoParams = @{
        UserPrincipalName = $t.upn
        ShowBanner        = $false
        ErrorAction       = 'Stop'
    }
    $exoCmd = Get-Command Connect-ExchangeOnline -ErrorAction Stop
    if ($exoCmd.Parameters.ContainsKey('DisableWAM')) { $exoParams['DisableWAM'] = $true }
    if ($connectionPrefix) { $exoParams['Prefix'] = $connectionPrefix }

    Write-Host "[*] Exchange : $($t.name)" -NoNewline -ForegroundColor $LineCol
    $graphStarted = $false
    try {
        Connect-ExchangeOnline @exoParams

        $connections = @(Get-ConnectionInformation -ErrorAction Stop | Where-Object { $_.State -eq 'Connected' })
        if ($KeepExisting) {
            $created = @($connections | Where-Object { $_.ConnectionId -notin $beforeIds })
            $selected = @($connections | Where-Object { $_.ConnectionUsedForInbuiltCmdlets -eq $true })
            if ($created.Count -ne 1 -or [string]::IsNullOrWhiteSpace($created[0].ConnectionId) -or
                $created[0].ModulePrefix -ne $connectionPrefix -or $selected.Count -ne 1 -or
                $selected[0].ConnectionId -ne $created[0].ConnectionId) {
                throw 'PIVOT: cannot identify the newly selected Exchange connection. Reused or ambiguous connections are not verified.'
            }
            $ci = $created[0]
        } else {
            if ($connections.Count -gt 1) { throw 'PIVOT: multiple Exchange connections returned after reconnect. State is ambiguous.' }
            $ci = $connections | Select-Object -First 1
        }

        if (-not $ci) {
            Write-StatusTag "[NO SESSION INFO]" "Red"
            throw "PIVOT: connected but no session info returned. State unknown, nothing assumed."
        }

        if ($ci.TenantID -ne $t.tenantId) {
            Write-StatusTag "[WRONG TENANT]" "Red"
            Write-HLine -Style "solid"
            Write-Host "[!] Expected : $($t.tenantId)  ($($t.name))" -ForegroundColor Red
            Write-Host "[!] Received : $($ci.TenantID)  ($($ci.Organization))" -ForegroundColor Red
            Write-HLine -Style "solid"
            throw "PIVOT: tenant mismatch on Exchange connect."
        }

        if ($Graph) {
            if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
                throw "PIVOT: Microsoft.Graph.Authentication not found. Check with Get-Module -ListAvailable Microsoft.Graph.Authentication."
            }
            if (Get-MgContext -ErrorAction SilentlyContinue) {
                Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
            }

            $mgParams = @{ TenantId = $t.tenantId; Scopes = $Scopes; ErrorAction = 'Stop' }
            $mgCmd = Get-Command Connect-MgGraph -ErrorAction Stop
            if ($mgCmd.Parameters.ContainsKey('NoWelcome')) { $mgParams['NoWelcome'] = $true }

            Write-Host "[*] Graph    : $($t.name)" -NoNewline -ForegroundColor $LineCol
            $graphStarted = $true
            Connect-MgGraph @mgParams

            $mg = Get-MgContext -ErrorAction Stop
            if (-not $mg -or $mg.TenantId -ne $t.tenantId) {
                Write-StatusTag "[WRONG TENANT]" "Red"
                Write-HLine -Style "solid"
                Write-Host "[!] Graph landed in the wrong tenant." -ForegroundColor Red
                Write-HLine -Style "solid"
                throw "PIVOT: tenant mismatch on Graph connect."
            }

        }
        Write-StatusTag "[VERIFIED]" $MainCol
        if ($connectionPrefix) {
            Write-Host "    Exchange command prefix: $connectionPrefix (for example Get-${connectionPrefix}Mailbox)." -ForegroundColor $DimCol
        }
    } catch {
        $failure = $_
        $cleanupErrors = @()
        if ($graphStarted) {
            try { Disconnect-MgGraph -ErrorAction Stop | Out-Null }
            catch { $cleanupErrors += "Graph teardown failed: $($_.Exception.Message)" }
        }
        try {
            if ($connectionPrefix) {
                Disconnect-ExchangeOnline -ModulePrefix $connectionPrefix -Confirm:$false -ErrorAction Stop
            } else {
                Disconnect-ExchangeOnline -Confirm:$false -ErrorAction Stop
            }
        }
        catch { $cleanupErrors += "Exchange teardown failed: $($_.Exception.Message)" }
        if ($cleanupErrors.Count) {
            throw "PIVOT: $($failure.Exception.Message) Cleanup incomplete. $($cleanupErrors -join '; ') Close this PowerShell session before running tenant commands."
        }
        throw $failure
    }
}

#endregion

#region 6: COMPLETION & EXPORT
# ------------------------------------------------------------------------------

$script:PivotKeyCompleter = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    Read-PivotConfig |
        Where-Object { $_.key -like "$wordToComplete*" } |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new(
                $_.key, $_.key, 'ParameterValue', "$($_.name) / $($_.upn)"
            )
        }
}

foreach ($fn in 'Connect-PivotTenant', 'Remove-PivotTenant', 'Get-PivotTenant') {
    Register-ArgumentCompleter -CommandName $fn -ParameterName Key -ScriptBlock $script:PivotKeyCompleter
}

Set-Alias -Name pivot -Value Connect-PivotTenant
Set-Alias -Name pvc   -Value Get-PivotContext
Set-Alias -Name pvx   -Value Disconnect-PivotTenant

Export-ModuleMember -Function @(
    'Connect-PivotTenant',
    'Disconnect-PivotTenant',
    'Get-PivotContext',
    'Add-PivotTenant',
    'Remove-PivotTenant',
    'Get-PivotTenant'
) -Alias @('pivot', 'pvc', 'pvx')

#endregion
