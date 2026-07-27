# KillerPivot

**Per-Identity Validated Org-Toggle**

Switch between client Microsoft 365 tenants with one command, and verify the
tenant ID after every connect so a wrong-tenant session never survives to the
prompt.

Stores no credentials. The tenant list holds identifiers only and lives outside
the module, so KillerPivot contains no client data and can be installed by
anyone without carrying yours.

**Compatibility:** PowerShell 5.1 and PowerShell 7. Workstation only, since
Kaseya LiveConnect consoles do not load user modules.

**Requires:** ExchangeOnlineManagement v3 or later. Microsoft.Graph.Authentication
only if you use `-Graph`.

---

## Why

Every tech has had the moment where a command ran against the tenant they were
still connected to from an hour ago. `Connect-ExchangeOnline -UserPrincipalName`
does not prevent this: the Windows broker treats that parameter as a hint and
will hand back whatever account it saw last.

KillerPivot disables the broker to force a real prompt, then asks the session
what tenant it actually landed in. On a mismatch it disconnects and throws, so
you are left with no session rather than the wrong one.

---

## Install

```powershell
Install-Module KillerPivot -Scope CurrentUser
```

---

## Quickstart

Add a tenant:

```powershell
Add-PivotTenant
```

It prompts for the admin UPN, looks up the tenant GUID automatically from
Microsoft's public discovery endpoint, and suggests a display name and a short
key. Press Enter to accept the suggestions.

Switch to it:

```powershell
pivot acme
```

The key tab-completes from your saved tenants.

---

## Commands

| Command                  | Alias   | Purpose                                        |
| ------------------------ | ------- | ---------------------------------------------- |
| `Connect-PivotTenant`    | `pivot` | Connect and verify. Add `-Graph` for Graph too  |
| `Get-PivotContext`       | `pvc`   | Show which tenant you are currently in         |
| `Disconnect-PivotTenant` | `pvx`   | Close all sessions                             |
| `Add-PivotTenant`        |         | Save a tenant. Run bare for the guided version |
| `Remove-PivotTenant`     |         | Drop a tenant from the config                  |
| `Get-PivotTenant`        |         | List saved tenants                             |

Scripted onboarding, if you prefer it over the prompts:

```powershell
Add-PivotTenant -Key acme -Upn admin@acme.com -TenantId <guid> -Name 'Acme Inc'
```

---

## Config

Written to `%LOCALAPPDATA%\PIVOT\tenants.json` by default.

Four fields per tenant: a short lowercase key you type, a display name, the
admin UPN, and the tenant GUID. All four are identifiers. Nothing secret is
stored, and authentication stays interactive on every connect.

To keep the list somewhere backed up, point `PIVOT_CONFIG` at it:

```powershell
[Environment]::SetEnvironmentVariable('PIVOT_CONFIG', 'C:\path\to\tenants.json', 'User')
```

Unset falls back to `%LOCALAPPDATA%`, so a fresh install works with no setup.

Do not commit your own `tenants.json`. A map of every client tenant GUID to its
admin UPN is not a credential, but it is a target list.

---

## Notes

- Forcing a real sign-in prompt on every switch is what makes the guarantee
  real. That tradeoff is deliberate.
- Success prints two lines. The full status block only appears on `pvc`.
- A mismatch is loud and leaves you disconnected. That is the intended outcome.

---

Built and maintained by [Steve the Killer](https://thekiller.net).
