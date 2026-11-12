/// Command-line parsing (no external dependencies).
module MsiRemoteUninstall.Cli

open System
open MsiRemoteUninstall.Domain

[<RequireQualifiedAccess>]
type Transport =
    /// PowerShell Remoting over WinRM (WS-Management), HTTP 5985 or HTTPS 5986.
    | PsRemoting
    /// WMI over DCOM (System.Management) - no PowerShell / WinRM needed on the server.
    | Wmi

[<RequireQualifiedAccess>]
type AuthMode =
    | Default
    | Negotiate
    | Kerberos
    | CredSsp
    | Basic

type Options =
    { Computers: string list
      ComputerListFiles: string list
      Msis: string list
      MsiListFiles: string list
      ListInstalled: bool
      Filters: string list
      FilterMode: FilterMode
      Transport: Transport
      UseSsl: bool
      SkipCertCheck: bool
      Port: int option
      Auth: AuthMode
      UserName: string option
      PasswordEnvVar: string option
      CurrentUser: bool
      WhatIf: bool
      Yes: bool
      Throttle: int
      ReportPath: string option
      RemoteLogFolder: string
      TimeoutMinutes: int
      ShowHelp: bool }

let defaults =
    { Computers = []
      ComputerListFiles = []
      Msis = []
      MsiListFiles = []
      ListInstalled = false
      Filters = []
      FilterMode = FilterMode.Substring
      Transport = Transport.PsRemoting
      UseSsl = false
      SkipCertCheck = false
      Port = None
      Auth = AuthMode.Default
      UserName = None
      PasswordEnvVar = None
      CurrentUser = false
      WhatIf = false
      Yes = false
      Throttle = 16
      ReportPath = None
      RemoteLogFolder = @"C:\Windows\Temp\MsiUninstallLogs"
      TimeoutMinutes = 60
      ShowHelp = false }

let usage =
    """
MsiRemoteUninstall - uninstall (or list) MSI products on remote Windows servers.

USAGE
  MsiRemoteUninstall [servers] [msis] [options]
  MsiRemoteUninstall [servers] --list [--filter <pattern>...] [options]

SERVERS (at least one; combined and de-duplicated)
  -c, --computer <name>        Server host name. Repeatable; comma-separated allowed.
      --computer-list <file>   Text file, one server per line.

MSIS (required unless --list; combined and de-duplicated)
  -m, --msi <name|guid>        Display name (wildcards * ? [a-z] allowed) or product code GUID. Repeatable.
      --msi-list <file>        Text file, one MSI name / product code per line.

LIST / SEARCH MODE (nothing is uninstalled)
  -l, --list                   List installed MSI products instead of uninstalling.
  -f, --filter <pattern>       Search pattern. Repeatable; a product is shown if its name,
                               publisher or product code matches ANY pattern. Case-insensitive.
      --filter-mode <mode>     substring (default) | wildcard | regex
                                 substring: field must contain the text
                                 wildcard : * ? [a-z], must match the WHOLE field (use *text*)
                                 regex    : .NET regular expression, matches anywhere unless anchored

CONNECTION
  -t, --transport <name>       psremoting (default) | wmi
                                 psremoting: PowerShell Remoting over WinRM (HTTP 5985 / HTTPS 5986)
                                 wmi       : WMI over DCOM (RPC 135 + dynamic ports), no WinRM needed
      --ssl                    psremoting only: use HTTPS (WinRM over SSL).
      --skip-cert-check        psremoting + --ssl only: accept self-signed / mismatched certificates.
      --port <n>               psremoting only: override WinRM port.
      --auth <mode>            psremoting only: default | negotiate | kerberos | credssp | basic
      --throttle <n>           Servers processed in parallel (default 16).
      --timeout <minutes>      Max time to wait for one msiexec run (default 60).

CREDENTIALS (prompted in the console by default)
  -u, --user <DOMAIN\user>     User name; only the password is then prompted.
      --password-env <VAR>     Read the password from environment variable VAR (no prompt).
      --current-user           Use the current Windows logon (no prompt).

BEHAVIOUR
      --what-if                Connect and report what WOULD be uninstalled; remove nothing.
  -y, --yes                    Do not ask for confirmation before uninstalling.
      --report <file.csv>      CSV report path (default .\MsiUninstallReport_<ts>.csv / MsiInventory_<ts>.csv).
      --remote-log-folder <p>  msiexec log folder on servers (default C:\Windows\Temp\MsiUninstallLogs).
  -h, --help                   Show this help.

List files: blank lines and lines starting with '#' are ignored.
Exit code: 0 = everything OK, 1 = at least one failure, 2 = invalid arguments.

EXAMPLES
  MsiRemoteUninstall --computer-list servers.txt --msi-list msis.txt
  MsiRemoteUninstall -c SRV01,SRV02 -m "Contoso Agent*" --what-if
  MsiRemoteUninstall -c SRV01 --list --filter-mode regex -f "^Contoso (Agent|Monitor)" -u CONTOSO\admin
  MsiRemoteUninstall --computer-list servers.txt --msi-list msis.txt -t psremoting --ssl -y
  MsiRemoteUninstall --computer-list servers.txt -m {12345678-90AB-CDEF-1234-567890ABCDEF} -t wmi
"""

exception CliError of string

let private parseEnum name (map: (string * 'T) list) (value: string) =
    match map |> List.tryFind (fun (k, _) -> String.Equals(k, value, StringComparison.OrdinalIgnoreCase)) with
    | Some(_, v) -> v
    | None -> raise (CliError(sprintf "Invalid value '%s' for %s. Allowed: %s" value name (map |> List.map fst |> String.concat ", ")))

let private parseInt name (value: string) min max =
    match Int32.TryParse value with
    | true, v when v >= min && v <= max -> v
    | _ -> raise (CliError(sprintf "Invalid value '%s' for %s (expected %d-%d)." value name min max))

let private splitComma (s: string) =
    s.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray

/// Parses argv. Raises CliError on invalid input.
let parse (argv: string[]) : Options =
    let rec loop (o: Options) (args: string list) =
        let needValue name rest =
            match rest with
            | v :: tail when not (String.IsNullOrEmpty v) -> v, tail
            | _ -> raise (CliError(sprintf "Missing value for %s." name))
        match args with
        | [] -> o
        | a :: rest ->
            match a.ToLowerInvariant() with
            | "-h" | "--help" | "-?" | "/?" -> loop { o with ShowHelp = true } rest
            | "-c" | "--computer" ->
                let v, t = needValue a rest
                loop { o with Computers = o.Computers @ splitComma v } t
            | "--computer-list" ->
                let v, t = needValue a rest
                loop { o with ComputerListFiles = o.ComputerListFiles @ [ v ] } t
            | "-m" | "--msi" ->
                let v, t = needValue a rest
                loop { o with Msis = o.Msis @ [ v ] } t
            | "--msi-list" ->
                let v, t = needValue a rest
                loop { o with MsiListFiles = o.MsiListFiles @ [ v ] } t
            | "-l" | "--list" -> loop { o with ListInstalled = true } rest
            | "-f" | "--filter" ->
                let v, t = needValue a rest
                loop { o with Filters = o.Filters @ [ v ] } t
            | "--filter-mode" ->
                let v, t = needValue a rest
                let m =
                    parseEnum a [ "substring", FilterMode.Substring; "wildcard", FilterMode.Wildcard; "regex", FilterMode.Regex ] v
                loop { o with FilterMode = m } t
            | "-t" | "--transport" ->
                let v, t = needValue a rest
                let tr = parseEnum a [ "psremoting", Transport.PsRemoting; "winrm", Transport.PsRemoting; "wmi", Transport.Wmi; "dcom", Transport.Wmi ] v
                loop { o with Transport = tr } t
            | "--ssl" -> loop { o with UseSsl = true } rest
            | "--skip-cert-check" -> loop { o with SkipCertCheck = true } rest
            | "--port" ->
                let v, t = needValue a rest
                loop { o with Port = Some(parseInt a v 1 65535) } t
            | "--auth" ->
                let v, t = needValue a rest
                let m =
                    parseEnum a
                        [ "default", AuthMode.Default
                          "negotiate", AuthMode.Negotiate
                          "kerberos", AuthMode.Kerberos
                          "credssp", AuthMode.CredSsp
                          "basic", AuthMode.Basic ] v
                loop { o with Auth = m } t
            | "-u" | "--user" ->
                let v, t = needValue a rest
                loop { o with UserName = Some v } t
            | "--password-env" ->
                let v, t = needValue a rest
                loop { o with PasswordEnvVar = Some v } t
            | "--current-user" -> loop { o with CurrentUser = true } rest
            | "--what-if" | "--whatif" -> loop { o with WhatIf = true } rest
            | "-y" | "--yes" -> loop { o with Yes = true } rest
            | "--throttle" ->
                let v, t = needValue a rest
                loop { o with Throttle = parseInt a v 1 256 } t
            | "--timeout" ->
                let v, t = needValue a rest
                loop { o with TimeoutMinutes = parseInt a v 1 1440 } t
            | "--report" ->
                let v, t = needValue a rest
                loop { o with ReportPath = Some v } t
            | "--remote-log-folder" ->
                let v, t = needValue a rest
                loop { o with RemoteLogFolder = v.TrimEnd('\\') } t
            | _ -> raise (CliError(sprintf "Unknown argument '%s'. Use --help for usage." a))

    loop defaults (List.ofArray argv)
