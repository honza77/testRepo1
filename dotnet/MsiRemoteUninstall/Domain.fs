/// Core types and pure logic shared by all transports: list files, product
/// matching, search filters (substring / wildcard / regex) and status codes.
module MsiRemoteUninstall.Domain

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open MsiRemoteUninstall.Errors

[<RequireQualifiedAccess>]
type FilterMode =
    | Substring
    | Wildcard
    | Regex

/// An MSI product found in the registry Uninstall keys of a server.
type InstalledProduct =
    { ProductCode: string
      DisplayName: string
      Version: string
      Publisher: string
      InstallDate: string
      Architecture: string }

type UninstallStatus =
    | Success
    | SuccessRebootRequired
    | SuccessRebootInitiated
    | NotInstalled
    | NotFound
    | WouldUninstall
    | Failed
    | ExecutionError
    | ConnectionFailed

/// One row of the uninstall report.
type UninstallResult =
    { ComputerName: string
      Msi: string
      DisplayName: string
      Version: string
      ProductCode: string
      Status: UninstallStatus
      ExitCode: int option
      ErrorCode: string
      Message: string
      LogFile: string }

/// One row of the inventory (-list) report.
type InventoryRow =
    { ComputerName: string
      Product: InstalledProduct option
      /// (error code, message) when the server could not be queried
      Error: (string * string) option }

// ---------------------------------------------------------------- list files

/// Reads a list file: one entry per line, blank lines and '#' comments ignored.
let readListFile (path: string) : string list =
    if not (File.Exists path) then
        raiseMru "MRU104" (sprintf "%s (current directory: %s)" path Environment.CurrentDirectory)
    let lines =
        try
            File.ReadAllLines path
        with ex ->
            raiseMru "MRU105" (sprintf "%s: %s" path ex.Message)
    lines
    |> Seq.map (fun l -> l.Trim())
    |> Seq.filter (fun l -> l <> "" && not (l.StartsWith "#"))
    |> List.ofSeq

/// Merges inline values and file contents, case-insensitive de-duplication, order preserved.
let mergeList (inline': string list) (files: string list) : string list =
    let all = (inline' |> List.map (fun s -> s.Trim())) @ (files |> List.collect readListFile)
    let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    all |> List.filter (fun s -> s <> "" && seen.Add s)

// ---------------------------------------------------------------- product codes

let private guidRegex =
    Regex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$")

/// Returns the canonical "{XXXXXXXX-...}" form if the text is a product code GUID.
let tryProductCode (s: string) : string option =
    let t = s.Trim()
    if guidRegex.IsMatch t then Some("{" + t.Trim('{', '}').ToUpperInvariant() + "}") else None

let isProductCode (s: string) = (tryProductCode s).IsSome

// ---------------------------------------------------------------- wildcard / filters

/// Converts a PowerShell-style wildcard (* ? [abc] [a-z], ` escapes) to an anchored regex.
let wildcardToRegex (pattern: string) : string =
    let sb = StringBuilder("^")
    let mutable i = 0
    while i < pattern.Length do
        match pattern.[i] with
        | '*' -> sb.Append(".*") |> ignore
        | '?' -> sb.Append('.') |> ignore
        | '`' when i + 1 < pattern.Length ->
            i <- i + 1
            sb.Append(Regex.Escape(string pattern.[i])) |> ignore
        | '[' ->
            let close = pattern.IndexOf(']', i + 1)
            if close < 0 then
                failwithf "Unterminated '[' in wildcard pattern '%s'" pattern
            let inner = pattern.Substring(i + 1, close - i - 1)
            if inner = "" then
                failwithf "Empty '[]' in wildcard pattern '%s'" pattern
            let escaped = inner.Replace(@"\", @"\\").Replace("^", @"\^").Replace("[", @"\[")
            sb.Append('[').Append(escaped).Append(']') |> ignore
            i <- close
        | c -> sb.Append(Regex.Escape(string c)) |> ignore
        i <- i + 1
    sb.Append('$').ToString()

let private regexOptions = RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant

/// Builds a case-insensitive matcher. Throws (ArgumentException / Failure) on invalid patterns.
let createMatcher (mode: FilterMode) (pattern: string) : string -> bool =
    match mode with
    | FilterMode.Substring -> fun s -> s.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0
    | FilterMode.Wildcard ->
        let r = Regex(wildcardToRegex pattern, regexOptions)
        fun s -> r.IsMatch s
    | FilterMode.Regex ->
        let r = Regex(pattern, regexOptions)
        fun s -> r.IsMatch s

/// List-mode filter: a product passes if DisplayName, Publisher or ProductCode matches ANY matcher.
let productPassesFilters (matchers: (string -> bool) list) (p: InstalledProduct) =
    List.isEmpty matchers
    || matchers
       |> List.exists (fun m ->
           [ p.DisplayName; p.Publisher; p.ProductCode ]
           |> List.exists (fun f -> not (String.IsNullOrEmpty f) && m f))

/// Uninstall-mode matching: an MSI entry is either a product code (exact) or a
/// display name wildcard pattern (like PowerShell -like; no wildcard = exact name).
let findProducts (installed: InstalledProduct list) (msi: string) : InstalledProduct list =
    match tryProductCode msi with
    | Some code -> installed |> List.filter (fun p -> p.ProductCode = code)
    | None ->
        let m = createMatcher FilterMode.Wildcard msi
        installed |> List.filter (fun p -> m p.DisplayName)

// ---------------------------------------------------------------- msiexec exit codes

/// Maps an msiexec exit code to status, message and error-catalog code ("" for plain success).
let interpretExitCode (code: int) : UninstallStatus * string * string =
    let describe () = (Errors.get (Errors.msiCode code)).Title
    match code with
    | 0 -> Success, "Uninstalled successfully.", ""
    | 3010 -> SuccessRebootRequired, "Uninstalled; reboot required.", "MSI3010"
    | 1641 -> SuccessRebootInitiated, "Uninstalled; reboot initiated.", "MSI1641"
    | 1605 -> NotInstalled, "Product is not installed.", "MSI1605"
    | c -> Failed, sprintf "msiexec exit code %d: %s." c (describe ()), Errors.msiCode c

let isFailure status =
    match status with
    | Failed | ExecutionError | ConnectionFailed -> true
    | _ -> false
