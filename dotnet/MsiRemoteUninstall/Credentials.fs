/// Console credential input (masked password, kept in a SecureString).
module MsiRemoteUninstall.Credentials

open System
open System.Security
open MsiRemoteUninstall.Errors

/// User name + password. None = use the current Windows logon.
type Credential = { UserName: string; Password: SecureString }

let private toSecure (s: string) =
    let ss = new SecureString()
    for c in s do
        ss.AppendChar c
    ss.MakeReadOnly()
    ss

/// Reads a password from the console, echoing '*'. Falls back to a plain line
/// read when stdin is redirected (e.g. piped input).
let readPassword (prompt: string) : SecureString =
    Console.Write(prompt + ": ")
    if Console.IsInputRedirected then
        let line = Console.In.ReadLine()
        Console.WriteLine()
        toSecure (if isNull line then "" else line)
    else
        let pwd = new SecureString()
        let mutable fin = false
        while not fin do
            let k = Console.ReadKey(true)
            match k.Key with
            | ConsoleKey.Enter ->
                Console.WriteLine()
                fin <- true
            | ConsoleKey.Backspace ->
                if pwd.Length > 0 then
                    pwd.RemoveAt(pwd.Length - 1)
                    Console.Write("\b \b")
            | ConsoleKey.Escape ->
                // clear everything typed so far
                while pwd.Length > 0 do
                    pwd.RemoveAt(pwd.Length - 1)
                    Console.Write("\b \b")
            | _ when k.KeyChar <> '\000' && not (Char.IsControl k.KeyChar) ->
                pwd.AppendChar k.KeyChar
                Console.Write('*')
            | _ -> ()
        pwd.MakeReadOnly()
        pwd

let private obtainCore (userName: string option) (passwordEnvVar: string option) (currentUser: bool) : Credential option =
    if currentUser then
        None
    else
        Console.ForegroundColor <- ConsoleColor.Cyan
        Console.WriteLine "Enter credentials with local admin rights on the target servers"
        Console.ResetColor()
        let user =
            match userName with
            | Some u ->
                Console.WriteLine("User name: " + u)
                u
            | None ->
                Console.Write "User name (e.g. DOMAIN\\user): "
                let l = Console.ReadLine()
                if isNull l then "" else l.Trim()
        if user = "" then
            raiseMru "MRU201" "The user name was empty."
        let password =
            match passwordEnvVar with
            | Some var ->
                match Environment.GetEnvironmentVariable var with
                | null
                | "" -> raiseMru "MRU203" (sprintf "Environment variable '%s' is not set or empty." var)
                | v -> toSecure v
            | None -> readPassword (sprintf "Password for %s" user)
        if password.Length = 0 then
            raiseMru "MRU202" "The password was empty."
        Some { UserName = user; Password = password }

/// Resolves credentials from options: current user, env var, or console prompt.
let obtain (userName: string option) (passwordEnvVar: string option) (currentUser: bool) : Credential option =
    try
        obtainCore userName passwordEnvVar currentUser
    with
    | :? InvalidOperationException as ex -> raiseMru "MRU204" ex.Message // Console.ReadKey without a console
    | :? IO.IOException as ex -> raiseMru "MRU204" ex.Message
