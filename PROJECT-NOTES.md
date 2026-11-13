# Project notes / hand-off

Read this first when you continue the work in a new session. Everything is committed on the branch
**`claude/powershell-msi-uninstall-vttbi0`** of `honza77/testRepo1`. Nothing is merged and no pull
request has been opened.

## What the project is

Tools to **uninstall MSI products on remote Windows servers** and to **list / search** the MSIs
installed there. The two .NET tools can also **install** MSIs, to prepare test servers.

| Tool | Path | Uninstall | List / search | Install | Error codes |
|---|---|---|---|---|---|
| PowerShell v1 | `Uninstall-RemoteMsi.ps1` | yes | yes | - | - |
| PowerShell v2 | `Uninstall-RemoteMsi.v2.ps1` | yes | yes | - (on purpose) | yes |
| F# / .NET 8 | `dotnet/` (`MsiRemoteUninstall.sln`) | yes | yes | yes | yes |
| C# / .NET Framework 4.8.1 | `netfx/` (`MsiRemoteUninstall.NetFx.sln`) | yes | yes | yes | yes |
| Test MSIs (WiX v5) | `samples/` | | | | |

Per-tool documentation: `README.md`, `dotnet/README.md`, `netfx/README.md`, `samples/README.md`,
`ERROR-CODES.md`.

## Decisions made (keep these unless there is a reason to change them)

- **Leave the v1 script alone.** `Uninstall-RemoteMsi.ps1` is kept unchanged. Error-code work went into a
  separate `Uninstall-RemoteMsi.v2.ps1`, as requested.
- **No install option in PowerShell.** It is only in the F# and C# tools, as requested.
- **Finding installed products:** done by reading the registry Uninstall keys (64-bit and
  WOW6432Node). Never `Win32_Product`, because querying it triggers an MSI repair of every product.
- **Transports:**
  - `psremoting` (default) is WinRM over HTTP 5985 or HTTPS 5986.
  - `wmi` is WMI over DCOM. The msiexec exit code comes back through a temporary registry value
    under `HKLM\SOFTWARE\MsiRemoteUninstall\ExitCodes`, written by `cmd /v:on ... !errorlevel!`.
  - Install-mode uploads: chunked base64 through the WinRM session (psremoting), or the
    `\\server\C$` admin share via `WNetAddConnection2` (wmi).
- **Checks before connecting:** a DNS lookup and a TCP port check run first (`--skip-preflight` /
  `-SkipPreflight` turns them off). This is what tells MRU301 / MRU302 apart from authentication errors.
- **MSI name matching:** a product code GUID is matched exactly. Anything else is a PowerShell `-like`
  wildcard, so a name without wildcards must match exactly.
- **List filters:** substring (default), wildcard or regex, all case-insensitive. A product is shown
  if its name, publisher or product code matches any filter.
- **Exit codes (all tools):**

  | Code | Meaning |
  |---|---|
  | 0 | OK |
  | 1 | Partial failure |
  | 2 | Invalid input |
  | 3 | Credentials |
  | 4 | Aborted |
  | 5 | No server reachable |
  | 6 | Internal error |
  | 10 | OK, reboot required |

- **Credentials:**
  - PowerShell: a pop-up by default, or `-ConsoleCredential`.
  - .NET tools: a masked console prompt, `--password-env VAR`, or `--current-user`.

## The error catalog has one source

`dotnet/MsiRemoteUninstall/Errors.fs` is the **only** place to edit error codes and texts.
Everything below is generated from it:

| Generated file | Used by |
|---|---|
| `ERROR-CODES.md` | People |
| `shared/error-catalog.json` | Embedded into the C# Core library |
| JSON block inside `Uninstall-RemoteMsi.v2.ps1` (between `# <error-catalog>` markers) | The v2 script |

After editing `Errors.fs`, regenerate with `dotnet/Update-ErrorDocs.ps1` (Windows / pwsh), then
rebuild `netfx`. The unit tests fail when any generated copy is out of date, or when source code
uses a code that is not in the catalog.

Without PowerShell (e.g. on Linux), the same regeneration is:

```bash
B=dotnet/MsiRemoteUninstall/bin/Release/net8.0/MsiRemoteUninstall
$B --errors markdown > ERROR-CODES.md
$B --errors json > shared/error-catalog.json
# then replace the JSON between  $script:ErrorCatalogJson = @'  and  '@  in Uninstall-RemoteMsi.v2.ps1
```

Code ranges:

| Range | Area |
|---|---|
| MRU1xx | Input |
| MRU2xx | Credentials |
| MRU3xx | Connection |
| MRU4xx | Remote operation |
| MRU5xx | Local |
| MSIxxxx | msiexec exit codes |
| MRU999 | Internal |

## Build and test

```bash
# F#
cd dotnet && dotnet build -c Release && dotnet test
# C#  (the net481 exe builds on Linux too, thanks to the Microsoft.NETFramework.ReferenceAssemblies package)
cd netfx && dotnet build -c Release && dotnet test
# Sample apps / MSIs (MSIs need Windows)
cd samples && ./Build-SampleMsis.ps1
```

Last known state: **F# 113 tests and C# 70 tests pass**. Both solutions build with no warnings.

Cloud-session environment notes:
- The container is Linux. The .NET 8 SDK was installed with `apt-get install dotnet-sdk-8.0` and has
  to be installed again in a new session.
- PowerShell (`pwsh`) is **not** available. Downloading it from GitHub was blocked, so no `.ps1` file
  has ever been run or syntax-checked.
- WiX runs on Linux only far enough to compile the source: it accepted the `Package.wxs` XML and all
  four variants, then failed at a Windows-only check. MSIs must be built on Windows.

## What has NOT been verified (do this first on Windows)

1. **Nothing has run against a real Windows server.** That covers all four tools: WinRM, WMI,
   uninstall, install, upload, admin share and the exit-code registry trick.
2. **The C# `.exe` has never been started.** It only builds. Expected runtime dependency:
   `System.Management.Automation` 3.0.0.0 from the GAC (Windows PowerShell 5.1).
3. **The `.ps1` scripts have never been run:** `Uninstall-RemoteMsi.ps1`, `Uninstall-RemoteMsi.v2.ps1`,
   `dotnet/Update-ErrorDocs.ps1`, `samples/Build-SampleMsis.ps1` and `samples/Start-PackageServer.ps1`.
   Points worth a close look in v2:
   - the PowerShell class `MruError` and its use in `catch [MruError]`
   - the closures in list-mode filtering (`GetNewClosure`)
   - the `Test-Preflight` task handling
4. **The sample MSIs have never been built or installed.** Points worth a close look:
   - the `<Files>` element (WiX v5)
   - `ScheduleReboot` on uninstall should give 3010
   - the `Error` custom action should give 1603, with `FORCE_UNINSTALL=1` as the escape
   - the `[%ComSpec]` ping custom action should take about 150 s
5. **Error classification of real Windows errors** is only unit-tested with sample messages. The
   message texts and WinRM error codes in `classifyWinRm` / `ErrorRules.ClassifyWinRm` /
   `Get-WinRmErrorCode` may need tuning once real errors are seen.

Suggested first test: follow `samples/README.md` → "Test walkthrough" against one test server.

## Known limitations / possible next steps

- **Platform:** the .NET tools only work on Windows; on other OSes they report MRU503.
- **WMI and special characters:** the msiexec command line goes through `cmd.exe`, so MSI property
  values must not contain `!`, `%` or `"` (rejected with MRU112).
- **Direct source mode:** reading from a share hits the "double hop" problem; `MSI1619` explains it.
- **Timeouts:** a timed-out msiexec (MRU403) is left running on the server; nothing kills it.
- **Not implemented (ideas only):**
  - PowerShell over SSH
  - a CIM/WSMan transport
  - `--reinstall` for packages that are already installed
  - a CI pipeline (GitHub Actions on `windows-latest` could build the MSIs and run all tests)
  - opening a PR to the default branch

## Prompt to continue in a new session

> Continue the MSI remote uninstall project on branch `claude/powershell-msi-uninstall-vttbi0`
> of honza77/testRepo1. Read `PROJECT-NOTES.md` first. Then: <your task>.
