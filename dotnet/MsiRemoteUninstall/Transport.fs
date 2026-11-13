/// Abstraction over the remoting technology. Each transport opens a session to
/// one server that can enumerate installed MSIs and run an msiexec uninstall.
module MsiRemoteUninstall.Transport

open System
open MsiRemoteUninstall.Domain
open MsiRemoteUninstall.Cli
open MsiRemoteUninstall.Credentials

type ConnectionSettings =
    { Credential: Credential option
      UseSsl: bool
      SkipCertCheck: bool
      Port: int option
      Auth: AuthMode
      Timeout: TimeSpan }

type IRemoteSession =
    inherit IDisposable
    /// Actual name reported by the server (falls back to the requested host name).
    abstract ComputerName: string
    /// MSI products from the 64-bit and 32-bit registry Uninstall keys.
    abstract GetInstalledProducts: unit -> InstalledProduct list
    /// Runs msiexec.exe with the given argument string on the server (creating logFolder first)
    /// and returns its exit code.
    abstract RunMsiexec: arguments: string * logFolder: string -> int
    /// Copies a local file to an absolute local path on the server (e.g. C:\Windows\Temp\x.msi).
    abstract UploadFile: localPath: string * remotePath: string -> unit
    /// Deletes a file on the server (no error if it does not exist).
    abstract DeleteFile: remotePath: string -> unit

/// Registry paths (relative to HKLM) scanned for installed products.
let uninstallKeys =
    [ @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "64-bit"
      @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "32-bit" ]

/// Keeps one entry per product code (a product can show in both views on odd setups).
let distinctProducts (products: InstalledProduct seq) =
    products |> Seq.distinctBy (fun p -> p.ProductCode) |> List.ofSeq
