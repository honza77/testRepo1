/// Maps exceptions thrown by .NET / PowerShell SDK / WMI to catalog codes.
module MsiRemoteUninstall.ErrorClassifier

open System
open System.Management
open System.Management.Automation
open System.Management.Automation.Remoting
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open MsiRemoteUninstall.Errors

let private has (s: string) (sub: string) =
    not (isNull s) && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0

let private codePrefix = Regex(@"^(MRU\d{3}):\s*(.*)$", RegexOptions.Singleline)

/// WinRM / PowerShell Remoting transport errors. `code` is the WinRM / Win32 error code.
let classifyWinRm (code: int) (msg: string) : string =
    if has msg "WSMan client library" then "MRU503"
    elif code = 5 || has msg "Access is denied" then "MRU303"
    elif code = 1326 || code = 1327 || code = 1330 || code = 1331 || code = 1909
         || has msg "user name or password is incorrect" || has msg "logon failure"
         || has msg "account is locked" || has msg "account is disabled" || has msg "password has expired" then
        "MRU304"
    elif code = 12175 || has msg "certificate" then "MRU306"
    elif code = -2144108316 (* 0x803380E4 TrustedHosts *)
         || has msg "TrustedHosts" || has msg "Kerberos" || has msg "CredSSP" || has msg "authentication" then
        "MRU307"
    elif code = -2144108250 (* 0x80338126 timeout *) || has msg "timed out" || has msg "timeout" then "MRU310"
    elif code = -2144108103 || has msg "cannot be resolved" || has msg "could not be resolved" then "MRU301"
    elif code = -2144108526 (* 0x80338012 cannot connect *) || has msg "WinRM" || has msg "WS-Management" then "MRU305"
    else "MRU399"

/// COM / DCOM HRESULTs seen with WMI.
let classifyHResult (hr: uint32) : string option =
    match hr with
    | 0x800706BAu (* RPC server unavailable *)
    | 0x800706BEu (* remote procedure call failed *)
    | 0x800706BFu
    | 0x800706D9u (* no more endpoints *) -> Some "MRU308"
    | 0x8007052Eu (* logon failure *)
    | 0x80070532u (* password expired *)
    | 0x80070533u (* account disabled *)
    | 0x80070775u (* account locked *) -> Some "MRU304"
    | 0x80070005u -> Some "MRU303"
    | 0x80070721u (* security package error *)
    | 0x80090322u (* wrong principal *) -> Some "MRU307"
    | 0x80041003u -> Some "MRU303" // WBEM_E_ACCESS_DENIED
    | 0x8004100Eu // invalid namespace
    | 0x80041010u // invalid class
    | 0x80041013u // provider load failure
    | 0x80041014u -> Some "MRU309" // component failed to initialise
    | _ -> None

/// Returns (code, detail). `fallback` is used when nothing more specific is known.
let rec classify (fallback: string) (ex: exn) : string * string =
    let detail = firstLine ex.Message
    match ex with
    | MruException(code, d) -> code, d
    | :? AggregateException as a when not (isNull a.InnerException) -> classify fallback a.InnerException
    | :? Reflection.TargetInvocationException as t when not (isNull t.InnerException) -> classify fallback t.InnerException
    | :? PSRemotingTransportException as t -> classifyWinRm t.ErrorCode t.Message, detail
    | :? PlatformNotSupportedException -> "MRU503", detail
    | :? UnauthorizedAccessException -> "MRU303", detail
    | :? ManagementException as m ->
        let code =
            match m.ErrorCode with
            | ManagementStatus.AccessDenied -> "MRU303"
            | ManagementStatus.InvalidNamespace
            | ManagementStatus.InvalidClass
            | ManagementStatus.ProviderLoadFailure
            | ManagementStatus.ProviderFailure
            | ManagementStatus.InvalidQuery -> "MRU309"
            | _ -> fallback
        code, detail
    | :? COMException as c -> (classifyHResult (uint32 c.HResult) |> Option.defaultValue fallback), detail
    | :? TimeoutException -> "MRU310", detail
    | :? Net.Sockets.SocketException -> "MRU302", detail
    | :? RuntimeException as r ->
        // Remote script errors thrown as "MRU###: detail"
        let m = codePrefix.Match(detail)
        if m.Success then m.Groups.[1].Value, m.Groups.[2].Value
        elif has r.Message "WSMan client library" then "MRU503", detail
        else fallback, detail
    | _ when has ex.Message "WSMan client library" || has ex.Message "only supported for Windows" -> "MRU503", detail
    | _ when (classifyHResult (uint32 ex.HResult)).IsSome -> (classifyHResult (uint32 ex.HResult)).Value, detail
    | _ -> fallback, detail
