module MsiRemoteUninstall.Tests.CliTests

open Xunit
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Cli

[<Fact>]
let ``defaults`` () =
    let o = parse [| "-c"; "SRV01"; "-m"; "App" |]
    Assert.Equal<string list>([ "SRV01" ], o.Computers)
    Assert.Equal<string list>([ "App" ], o.Msis)
    Assert.Equal(Transport.PsRemoting, o.Transport)
    Assert.False(o.UseSsl)
    Assert.Equal(16, o.Throttle)
    Assert.Equal(FilterMode.Substring, o.FilterMode)

[<Fact>]
let ``computers can be repeated and comma separated`` () =
    let o = parse [| "-c"; "A, B"; "--computer"; "C"; "--computer-list"; "s.txt" |]
    Assert.Equal<string list>([ "A"; "B"; "C" ], o.Computers)
    Assert.Equal<string list>([ "s.txt" ], o.ComputerListFiles)

[<Fact>]
let ``msi names are not split on commas`` () =
    let o = parse [| "-m"; "Contoso, Inc. Agent" |]
    Assert.Equal<string list>([ "Contoso, Inc. Agent" ], o.Msis)

[<Fact>]
let ``list mode with filters`` () =
    let o = parse [| "--list"; "-f"; "a"; "--filter"; "b"; "--filter-mode"; "REGEX" |]
    Assert.True o.ListInstalled
    Assert.Equal<string list>([ "a"; "b" ], o.Filters)
    Assert.Equal(FilterMode.Regex, o.FilterMode)

[<Fact>]
let ``connection options`` () =
    let o =
        parse [| "-t"; "wmi"; "--ssl"; "--port"; "5999"; "--auth"; "kerberos"; "--skip-cert-check"; "--throttle"; "4"; "--timeout"; "10" |]
    Assert.Equal(Transport.Wmi, o.Transport)
    Assert.True o.UseSsl
    Assert.True o.SkipCertCheck
    Assert.Equal(Some 5999, o.Port)
    Assert.Equal(AuthMode.Kerberos, o.Auth)
    Assert.Equal(4, o.Throttle)
    Assert.Equal(10, o.TimeoutMinutes)

[<Fact>]
let ``credential and behaviour options`` () =
    let o =
        parse [| "-u"; @"CONTOSO\admin"; "--password-env"; "PW"; "--what-if"; "-y"; "--report"; "r.csv"; "--remote-log-folder"; @"D:\Logs\" |]
    Assert.Equal(Some @"CONTOSO\admin", o.UserName)
    Assert.Equal(Some "PW", o.PasswordEnvVar)
    Assert.True o.WhatIf
    Assert.True o.Yes
    Assert.Equal(Some "r.csv", o.ReportPath)
    Assert.Equal(@"D:\Logs", o.RemoteLogFolder)

[<Theory>]
[<InlineData("--bogus")>]
[<InlineData("--port")>]
[<InlineData("--port|0")>]
[<InlineData("--throttle|abc")>]
[<InlineData("--transport|ssh")>]
[<InlineData("--filter-mode|glob")>]
let ``invalid arguments raise CliError`` (args: string) =
    Assert.ThrowsAny<exn>(fun () -> parse (args.Split '|') |> ignore)
    |> fun ex -> Assert.IsType<CliError>(ex) |> ignore
