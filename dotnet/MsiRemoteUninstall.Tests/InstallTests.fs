module MsiRemoteUninstall.Tests.InstallTests

open System
open System.IO
open Xunit
open MsiRemoteUninstall.Errors
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Packages
open MsiRemoteUninstall.Cli

let private codeOf (f: unit -> obj) =
    match Assert.ThrowsAny<exn>(fun () -> f () |> ignore) with
    | MruException(code, _) -> code
    | other -> failwithf "Unexpected exception %O" other

/// Temporary folder with a few fake .msi files.
type PackageFolder() =
    let dir = Path.Combine(Path.GetTempPath(), "mru-tests-" + Guid.NewGuid().ToString("N"))
    do
        Directory.CreateDirectory dir |> ignore
        for f in [ "HelloWorld.msi"; "Contoso.Agent.msi"; "readme.txt" ] do
            File.WriteAllText(Path.Combine(dir, f), "x")
    member _.Path = dir
    interface IDisposable with
        member _.Dispose() = Directory.Delete(dir, true)

[<Fact>]
let ``resolve finds files by name, adds .msi and expands wildcards`` () =
    use folder = new PackageFolder()
    let r = resolve (Some folder.Path) SourceMode.Copy "unused" [ "HelloWorld"; "*.msi" ]
    // HelloWorld.msi only once (de-duplicated), then Contoso.Agent.msi
    Assert.Equal<string list>([ "HelloWorld.msi"; "Contoso.Agent.msi" ], r |> List.map (fun p -> p.FileName))
    Assert.All(r, fun p -> Assert.True p.Copy)
    Assert.All(r, fun p -> Assert.True(File.Exists p.Path))

[<Fact>]
let ``resolve accepts absolute paths regardless of source`` () =
    use folder = new PackageFolder()
    let full = Path.Combine(folder.Path, "HelloWorld.msi")
    let r = resolve (Some "/does/not/matter") SourceMode.Copy "unused" [ full ]
    Assert.Equal(full, (List.exactlyOne r).Path)

[<Fact>]
let ``missing package is MRU110`` () =
    use folder = new PackageFolder()
    Assert.Equal("MRU110", codeOf (fun () -> box (resolve (Some folder.Path) SourceMode.Copy "unused" [ "Nope" ])))
    Assert.Equal("MRU110", codeOf (fun () -> box (resolve (Some folder.Path) SourceMode.Copy "unused" [ "z*.msi" ])))

[<Fact>]
let ``missing source folder is MRU111`` () =
    Assert.Equal("MRU111", codeOf (fun () -> box (resolve (Some "/no/such/folder") SourceMode.Copy "unused" [ "x" ])))

[<Fact>]
let ``direct mode passes server paths through without checking`` () =
    let r = resolve None SourceMode.Direct "unused" [ @"D:\Packages\Hello" ]
    let p = List.exactlyOne r
    Assert.Equal(@"D:\Packages\Hello.msi", p.Path)
    Assert.False p.Copy
    Assert.Equal(None, p.Info)

[<Fact>]
let ``direct mode rejects wildcards it cannot expand`` () =
    Assert.Equal("MRU110", codeOf (fun () -> box (resolve None SourceMode.Direct "unused" [ @"D:\Packages\*.msi" ])))

[<Fact>]
let ``URL wildcards are rejected`` () =
    Assert.Equal("MRU110", codeOf (fun () -> box (resolve (Some "https://repo.example/msi/") SourceMode.Copy "unused" [ "*.msi" ])))

[<Fact>]
let ``unreachable URL is MRU111`` () =
    let dir = Path.Combine(Path.GetTempPath(), "mru-dl-" + Guid.NewGuid().ToString("N"))
    Assert.Equal("MRU111", codeOf (fun () -> box (resolve (Some "http://127.0.0.1:1/msi") SourceMode.Copy dir [ "x" ])))

[<Fact>]
let ``msi info cannot be read from a fake file`` () =
    use folder = new PackageFolder()
    let p = resolve (Some folder.Path) SourceMode.Copy "unused" [ "HelloWorld" ] |> List.exactlyOne
    Assert.True p.InfoError.IsSome

// ---------------------------------------------------------------- msiexec properties / arguments

[<Fact>]
let ``properties are parsed and formatted`` () =
    Assert.Equal(("INSTALLFOLDER", @"D:\Apps\Hello World"), parseMsiProperty false @"INSTALLFOLDER=D:\Apps\Hello World")
    Assert.Equal(("EMPTY", ""), parseMsiProperty false "EMPTY=")
    Assert.Equal("A=\"1\" B=\"x y\"", formatMsiProperties [ "A", "1"; "B", "x y" ])

[<Theory>]
[<InlineData(false, "NOVALUE")>]
[<InlineData(false, "=x")>]
[<InlineData(false, "1BAD=x")>]
[<InlineData(false, "A=say \"hi\"")>]
[<InlineData(true, "A=50%")>]
[<InlineData(true, "A=hi!")>]
let ``invalid properties are MRU112`` (strict: bool, text: string) =
    Assert.Equal("MRU112", codeOf (fun () -> box (parseMsiProperty strict text)))

[<Fact>]
let ``percent and exclamation are fine without strict mode`` () =
    Assert.Equal(("A", "50%!"), parseMsiProperty false "A=50%!")

[<Fact>]
let ``msiexec arguments`` () =
    Assert.Equal(
        "/i \"C:\\Temp\\a.msi\" /qn /norestart /l*v \"C:\\Logs\\a.log\" X=\"1\"",
        msiexecArguments "/i" @"C:\Temp\a.msi" @"C:\Logs\a.log" [ "X", "1" ])
    Assert.Equal(
        "/x \"{1}\" /qn /norestart /l*v \"l.log\"",
        msiexecArguments "/x" "{1}" "l.log" [])

[<Theory>]
[<InlineData(0, "Success", "")>]
[<InlineData(3010, "SuccessRebootRequired", "MSI3010")>]
[<InlineData(1638, "Failed", "MSI1638")>]
[<InlineData(1603, "Failed", "MSI1603")>]
let ``install exit codes`` (code: int, status: string, errorCode: string) =
    let s, _, e = interpretInstallExitCode code
    Assert.Equal(status, string s)
    Assert.Equal(errorCode, e)

// ---------------------------------------------------------------- CLI

[<Fact>]
let ``install options are parsed`` () =
    let o =
        parse
            [| "-c"; "SRV01"; "-i"; "HelloWorld"; "--install"; "*.msi"; "--install-list"; "p.txt"
               "-s"; @"\\fs\msi"; "--source-mode"; "direct"; "-p"; "A=1"; "--msi-property"; "B=2"
               "--remote-staging-folder"; @"D:\Stage\"; "--keep-staged" |]
    Assert.Equal<string list>([ "HelloWorld"; "*.msi" ], o.InstallPackages)
    Assert.Equal<string list>([ "p.txt" ], o.InstallListFiles)
    Assert.Equal(Some @"\\fs\msi", o.PackageSource)
    Assert.Equal(SourceMode.Direct, o.SourceMode)
    Assert.Equal<string list>([ "A=1"; "B=2" ], o.MsiProperties)
    Assert.Equal(@"D:\Stage", o.RemoteStagingFolder)
    Assert.True o.KeepStaged

[<Fact>]
let ``install defaults`` () =
    let o = parse [| "-i"; "x" |]
    Assert.Equal(SourceMode.Copy, o.SourceMode)
    Assert.Equal(@"C:\Windows\Temp\MsiRemoteInstall", o.RemoteStagingFolder)
    Assert.False o.KeepStaged
