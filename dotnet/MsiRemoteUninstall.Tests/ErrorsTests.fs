module MsiRemoteUninstall.Tests.ErrorsTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Runtime.InteropServices
open Xunit
open MsiRemoteUninstall.Errors
open MsiRemoteUninstall.ErrorClassifier

/// Repository root (folder containing ERROR-CODES.md).
let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "Repository root not found"
        elif File.Exists(Path.Combine(d.FullName, "ERROR-CODES.md")) then d.FullName
        else up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

[<Fact>]
let ``codes are unique and well formed`` () =
    let codes = catalog |> List.map (fun e -> e.Code)
    Assert.Equal(codes.Length, (List.distinct codes).Length)
    for c in codes do
        Assert.Matches(Regex(@"^(MRU\d{3}|MSI\d{4}|MSI-OTHER)$"), c)

[<Fact>]
let ``every entry has explanation and remediation`` () =
    for e in catalog do
        Assert.False(String.IsNullOrWhiteSpace e.Title, e.Code)
        Assert.False(String.IsNullOrWhiteSpace e.Explanation, e.Code)
        Assert.NotEmpty(e.Remediation)

[<Fact>]
let ``every code raised in the sources exists in the catalog`` () =
    let src = Path.Combine(repoRoot, "dotnet", "MsiRemoteUninstall")
    let ps = Path.Combine(repoRoot, "Uninstall-RemoteMsi.v2.ps1")
    let files = Seq.append (Directory.GetFiles(src, "*.fs")) [ ps ]
    let used =
        files
        |> Seq.filter (fun f -> not (f.EndsWith "Errors.fs"))
        |> Seq.collect (fun f -> Regex.Matches(File.ReadAllText f, @"[""'](MRU\d{3}|MSI\d{4})[""']") |> Seq.map (fun m -> m.Groups.[1].Value))
        |> Set.ofSeq
    for c in used do
        Assert.True((tryFind c).IsSome, sprintf "%s is used but not in the catalog" c)

[<Theory>]
[<InlineData("mru303", "MRU303")>]
[<InlineData("1603", "MSI1603")>]
[<InlineData(" msi3010 ", "MSI3010")>]
let ``tryFind normalises codes`` (input: string, expected: string) =
    Assert.Equal(Some expected, tryFind input |> Option.map (fun e -> e.Code))

[<Fact>]
let ``unknown codes are not found`` () =
    Assert.Equal(None, tryFind "MRU000")
    Assert.Equal("MSI-OTHER", msiCode 4242)

[<Fact>]
let ``ERROR-CODES.md is up to date with the catalog`` () =
    let file = File.ReadAllText(Path.Combine(repoRoot, "ERROR-CODES.md")).Replace("\r\n", "\n")
    Assert.Equal(markdown().Replace("\r\n", "\n"), file)

[<Fact>]
let ``PowerShell v2 embedded catalog equals the .NET catalog`` () =
    let script = File.ReadAllText(Path.Combine(repoRoot, "Uninstall-RemoteMsi.v2.ps1")).Replace("\r\n", "\n")
    let m = Regex.Match(script, @"\$script:ErrorCatalogJson = @'\n(.*?)\n'@", RegexOptions.Singleline)
    Assert.True(m.Success, "catalog block not found")
    let normalise (s: string) = JsonDocument.Parse(s).RootElement.ToString()
    Assert.Equal(normalise (json ()), normalise m.Groups.[1].Value)

// ---------------------------------------------------------------- classifier

[<Theory>]
[<InlineData(5, "Access is denied.", "MRU303")>]
[<InlineData(1326, "The user name or password is incorrect.", "MRU304")>]
[<InlineData(0, "The SSL certificate contains a common name (CN) that does not match the hostname.", "MRU306")>]
[<InlineData(-2144108316, "The WinRM client cannot process the request. ... TrustedHosts ...", "MRU307")>]
[<InlineData(0, "WinRM cannot process the request. The following error occurred while using Kerberos authentication", "MRU307")>]
[<InlineData(-2144108250, "The WS-Management service cannot complete the operation within the time specified", "MRU310")>]
[<InlineData(-2144108526, "The client cannot connect to the destination specified in the request.", "MRU305")>]
[<InlineData(0, "This parameter set requires WSMan, and no supported WSMan client library was found.", "MRU503")>]
[<InlineData(0, "Something completely different", "MRU399")>]
let ``WinRM errors are classified`` (code: int, msg: string, expected: string) =
    Assert.Equal(expected, classifyWinRm code msg)

[<Theory>]
[<InlineData(0x800706BAu, "MRU308")>]
[<InlineData(0x8007052Eu, "MRU304")>]
[<InlineData(0x80070005u, "MRU303")>]
[<InlineData(0x80041003u, "MRU303")>]
[<InlineData(0x8004100Eu, "MRU309")>]
[<InlineData(0x80070721u, "MRU307")>]
let ``COM HRESULTs are classified`` (hr: uint32, expected: string) =
    Assert.Equal(Some expected, classifyHResult hr)

[<Fact>]
let ``classify unwraps and maps exceptions`` () =
    Assert.Equal(("MRU303", "nope"), classify "MRU399" (UnauthorizedAccessException "nope"))
    Assert.Equal(("MRU308", "rpc"), classify "MRU399" (AggregateException(COMException("rpc", int 0x800706BAu))))
    Assert.Equal(("MRU404", "x"), classify "MRU399" (MruException("MRU404", "x")))
    Assert.Equal(("MRU402", "boom"), classify "MRU405" (System.Management.Automation.RuntimeException "MRU402: boom"))
    Assert.Equal(("MRU405", "other"), classify "MRU405" (InvalidOperationException "other"))
    Assert.Equal(("MRU310", "t"), classify "MRU399" (TimeoutException "t"))

[<Fact>]
let ``preflight reports unresolvable names as MRU301`` () =
    match Assert.ThrowsAny<exn>(fun () -> MsiRemoteUninstall.Preflight.check "no-such-host.invalid" 5985 (TimeSpan.FromSeconds 2.0)) with
    | MruException(code, _) -> Assert.Equal("MRU301", code)
    | other -> failwithf "Unexpected %O" other

[<Fact>]
let ``preflight reports closed ports as MRU302`` () =
    // port 1 on localhost is closed on any normal machine
    match Assert.ThrowsAny<exn>(fun () -> MsiRemoteUninstall.Preflight.check "localhost" 1 (TimeSpan.FromSeconds 2.0)) with
    | MruException(code, _) -> Assert.Equal("MRU302", code)
    | other -> failwithf "Unexpected %O" other
