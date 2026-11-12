module MsiRemoteUninstall.Tests.DomainTests

open System
open System.IO
open Xunit
open MsiRemoteUninstall.Domain

let product name publisher code =
    { ProductCode = code
      DisplayName = name
      Version = "1.0"
      Publisher = publisher
      InstallDate = ""
      Architecture = "64-bit" }

let agent = product "Contoso Monitoring Agent" "Contoso Ltd" "{11111111-1111-1111-1111-111111111111}"
let vcpp = product "Microsoft Visual C++ 2019 X64 Minimum Runtime" "Microsoft Corporation" "{22222222-2222-2222-2222-222222222222}"
let backup = product "Fabrikam Backup Client" "Fabrikam" "{33333333-3333-3333-3333-333333333333}"
let installed = [ agent; vcpp; backup ]

[<Theory>]
[<InlineData("{12345678-90ab-cdef-1234-567890abcdef}", "{12345678-90AB-CDEF-1234-567890ABCDEF}")>]
[<InlineData("12345678-90AB-CDEF-1234-567890ABCDEF", "{12345678-90AB-CDEF-1234-567890ABCDEF}")>]
let ``tryProductCode normalises GUIDs`` (input: string, expected: string) =
    Assert.Equal(Some expected, tryProductCode input)

[<Theory>]
[<InlineData("Contoso Agent")>]
[<InlineData("{1234}")>]
[<InlineData("12345678-90AB-CDEF-1234-567890ABCDEFX")>]
let ``tryProductCode rejects non-GUIDs`` (input: string) = Assert.Equal(None, tryProductCode input)

[<Theory>]
[<InlineData("*agent*", "Contoso Monitoring Agent", true)>]
[<InlineData("agent*", "Contoso Monitoring Agent", false)>]
[<InlineData("contoso*", "Contoso Monitoring Agent", true)>]
[<InlineData("Microsoft Visual C++ 20?? *", "Microsoft Visual C++ 2019 X64 Minimum Runtime", true)>]
[<InlineData("Microsoft Visual C++ 20? *", "Microsoft Visual C++ 2019 X64 Minimum Runtime", false)>]
[<InlineData("[a-c]ontoso*", "Contoso Monitoring Agent", true)>]
[<InlineData("[xyz]ontoso*", "Contoso Monitoring Agent", false)>]
[<InlineData("Price (USD)", "Price (USD)", true)>]
[<InlineData("a.b", "axb", false)>]
[<InlineData("50`%", "50%", true)>]
[<InlineData("what`?", "what?", true)>]
[<InlineData("what`?", "whatx", false)>]
let ``wildcard matching behaves like PowerShell -like`` (pattern: string, text: string, expected: bool) =
    Assert.Equal(expected, createMatcher FilterMode.Wildcard pattern text)

[<Theory>]
[<InlineData("agent", "Contoso Monitoring Agent", true)>]
[<InlineData("C++", "Microsoft Visual C++ 2019", true)>]
[<InlineData("*", "Contoso", false)>]
let ``substring matching is literal and case-insensitive`` (pattern: string, text: string, expected: bool) =
    Assert.Equal(expected, createMatcher FilterMode.Substring pattern text)

[<Theory>]
[<InlineData("^contoso (agent|monitoring)", "Contoso Monitoring Agent", true)>]
[<InlineData("agent$", "Contoso Monitoring Agent", true)>]
[<InlineData("^agent", "Contoso Monitoring Agent", false)>]
[<InlineData(@"C\+\+ 20(19|22)", "Microsoft Visual C++ 2019", true)>]
let ``regex matching is case-insensitive and unanchored`` (pattern: string, text: string, expected: bool) =
    Assert.Equal(expected, createMatcher FilterMode.Regex pattern text)

[<Theory>]
[<InlineData("Regex", "(foo")>]
[<InlineData("Wildcard", "[abc")>]
[<InlineData("Wildcard", "[]")>]
let ``invalid patterns throw`` (mode: string, pattern: string) =
    let m = if mode = "Regex" then FilterMode.Regex else FilterMode.Wildcard
    Assert.ThrowsAny<exn>(fun () -> createMatcher m pattern |> ignore) |> ignore

[<Fact>]
let ``filters match name, publisher or product code with OR semantics`` () =
    let matchers = [ createMatcher FilterMode.Substring "fabrikam"; createMatcher FilterMode.Substring "2222" ]
    let result = installed |> List.filter (productPassesFilters matchers)
    Assert.Equal<InstalledProduct list>([ vcpp; backup ], result)

[<Fact>]
let ``filter by publisher`` () =
    let matchers = [ createMatcher FilterMode.Regex "^Microsoft Corp" ]
    Assert.Equal<InstalledProduct list>([ vcpp ], installed |> List.filter (productPassesFilters matchers))

[<Fact>]
let ``no filters lists everything`` () =
    Assert.Equal<InstalledProduct list>(installed, installed |> List.filter (productPassesFilters []))

[<Fact>]
let ``findProducts by product code without braces and lower case`` () =
    Assert.Equal<InstalledProduct list>([ backup ], findProducts installed "33333333-3333-3333-3333-333333333333")

[<Fact>]
let ``findProducts by exact display name is case-insensitive`` () =
    Assert.Equal<InstalledProduct list>([ agent ], findProducts installed "contoso monitoring agent")

[<Fact>]
let ``findProducts by name without wildcard does not match partial names`` () =
    Assert.Empty(findProducts installed "Contoso")

[<Fact>]
let ``findProducts by wildcard`` () =
    Assert.Equal<InstalledProduct list>([ agent ], findProducts installed "*agent")
    Assert.Equal<InstalledProduct list>(installed, findProducts installed "*")
    Assert.Equal<InstalledProduct list>([ backup ], findProducts installed "Fabrikam*")

[<Fact>]
let ``mergeList combines inline values and files, skips comments and duplicates`` () =
    let file = Path.GetTempFileName()
    try
        File.WriteAllLines(file, [| "# comment"; ""; "  SRV02  "; "srv01"; "SRV03" |])
        let result = mergeList [ "SRV01"; " "; "srv02" ] [ file ]
        Assert.Equal<string list>([ "SRV01"; "srv02"; "SRV03" ], result)
    finally
        File.Delete file

[<Fact>]
let ``readListFile fails for missing file`` () =
    Assert.ThrowsAny<exn>(fun () -> readListFile "does-not-exist.txt" |> ignore) |> ignore

[<Theory>]
[<InlineData(0, "Success")>]
[<InlineData(3010, "SuccessRebootRequired")>]
[<InlineData(1641, "SuccessRebootInitiated")>]
[<InlineData(1605, "NotInstalled")>]
[<InlineData(1603, "Failed")>]
[<InlineData(1618, "Failed")>]
let ``msiexec exit codes are interpreted`` (code: int, expected: string) =
    let status, _ = interpretExitCode code
    Assert.Equal(expected, string status)
