/// Quick checks before connecting: DNS resolution and a TCP connect to the
/// management port. They turn vague remoting errors into precise ones
/// (MRU301 name not resolved, MRU302 unreachable / port closed).
module MsiRemoteUninstall.Preflight

open System
open System.Net
open System.Net.Sockets
open MsiRemoteUninstall.Cli
open MsiRemoteUninstall.Errors

/// Port the chosen transport connects to first.
let managementPort (transport: Transport) (useSsl: bool) (port: int option) =
    match transport with
    | Transport.Wmi -> 135 // RPC endpoint mapper; WMI then continues on a dynamic port
    | Transport.PsRemoting -> defaultArg port (if useSsl then 5986 else 5985)

let private inner (ex: exn) =
    match ex with
    | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException
    | e -> e

/// Raises MruException MRU301 / MRU302 if the server cannot be reached.
let check (computer: string) (port: int) (timeout: TimeSpan) =
    let addresses =
        try
            Dns.GetHostAddresses computer
        with ex ->
            raiseMru "MRU301" (sprintf "'%s': %s" computer (firstLine (inner ex).Message))
    if addresses.Length = 0 then
        raiseMru "MRU301" (sprintf "'%s' resolved to no IP address." computer)

    use client = new TcpClient()
    let connected =
        try
            client.ConnectAsync(computer, port).Wait(timeout)
        with ex ->
            let e = inner ex
            let reason =
                match e with
                | :? SocketException as se when se.SocketErrorCode = SocketError.ConnectionRefused ->
                    "connection refused (nothing listening, or blocked by a firewall)"
                | :? SocketException as se when se.SocketErrorCode = SocketError.HostUnreachable || se.SocketErrorCode = SocketError.NetworkUnreachable ->
                    "host / network unreachable"
                | _ -> firstLine e.Message
            raiseMru "MRU302" (sprintf "TCP port %d on %s: %s." port computer reason)
    if not connected then
        raiseMru "MRU302" (sprintf "TCP port %d on %s did not answer within %.0f s (server offline or port filtered by a firewall)." port computer timeout.TotalSeconds)
