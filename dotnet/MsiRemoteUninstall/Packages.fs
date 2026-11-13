/// Install mode: resolving MSI packages from a package source (local folder,
/// UNC share or HTTP(S) URL) and reading their properties with msi.dll.
module MsiRemoteUninstall.Packages

open System
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open MsiRemoteUninstall.Errors

[<RequireQualifiedAccess>]
type SourceMode =
    /// The tool copies the package to a staging folder on each server (default).
    | Copy
    /// msiexec on the server opens the given path itself (UNC share or a path local to the server).
    | Direct

type MsiInfo =
    { ProductName: string
      ProductVersion: string
      ProductCode: string
      Manufacturer: string }

type ResolvedPackage =
    {
      /// Entry as given by the user (--install / --install-list).
      Entry: string
      /// File name, e.g. HelloWorld.msi.
      FileName: string
      /// Copy: file on this computer. Otherwise: path msiexec opens on the server.
      Path: string
      /// True when the file must be copied to the server (copy mode, or downloaded from a URL).
      Copy: bool
      /// Properties read from the MSI (None if they could not be read).
      Info: MsiInfo option
      /// Why Info could not be read (MRU504 warning).
      InfoError: string option }

// ------------------------------------------------------------------ msi.dll

module private Native =
    [<DllImport("msi.dll", CharSet = CharSet.Unicode)>]
    extern uint32 MsiOpenDatabase(string szDatabasePath, nativeint szPersist, nativeint& phDatabase)

    [<DllImport("msi.dll", CharSet = CharSet.Unicode)>]
    extern uint32 MsiDatabaseOpenView(nativeint hDatabase, string szQuery, nativeint& phView)

    [<DllImport("msi.dll")>]
    extern uint32 MsiViewExecute(nativeint hView, nativeint hRecord)

    [<DllImport("msi.dll")>]
    extern uint32 MsiViewFetch(nativeint hView, nativeint& phRecord)

    [<DllImport("msi.dll", CharSet = CharSet.Unicode)>]
    extern uint32 MsiRecordGetString(nativeint hRecord, uint32 iField, StringBuilder szValueBuf, uint32& pcchValueBuf)

    [<DllImport("msi.dll")>]
    extern uint32 MsiCloseHandle(nativeint hAny)

let private readProperty (db: nativeint) (name: string) : string =
    let mutable view = 0n
    let query = sprintf "SELECT `Value` FROM `Property` WHERE `Property`='%s'" name
    if Native.MsiDatabaseOpenView(db, query, &view) <> 0u then
        ""
    else
        try
            let mutable record = 0n
            if Native.MsiViewExecute(view, 0n) <> 0u || Native.MsiViewFetch(view, &record) <> 0u then
                ""
            else
                try
                    let mutable len = 1024u
                    let sb = StringBuilder(int len)
                    if Native.MsiRecordGetString(record, 1u, sb, &len) = 0u then sb.ToString() else ""
                finally
                    Native.MsiCloseHandle record |> ignore
        finally
            Native.MsiCloseHandle view |> ignore

/// Reads ProductName / ProductVersion / ProductCode / Manufacturer from an MSI file (Windows only).
let tryReadMsiInfo (path: string) : Result<MsiInfo, string> =
    if not (OperatingSystem.IsWindows()) then
        Result.Error "msi.dll is only available on Windows."
    else
        try
            let mutable db = 0n
            let rc = Native.MsiOpenDatabase(path, 0n (* MSIDBOPEN_READONLY *), &db)
            if rc <> 0u then
                Result.Error(sprintf "MsiOpenDatabase returned %d (not a valid MSI or not readable)." rc)
            else
                try
                    Ok
                        { ProductName = readProperty db "ProductName"
                          ProductVersion = readProperty db "ProductVersion"
                          ProductCode = (readProperty db "ProductCode").ToUpperInvariant()
                          Manufacturer = readProperty db "Manufacturer" }
                finally
                    Native.MsiCloseHandle db |> ignore
        with ex ->
            Result.Error ex.Message

// ------------------------------------------------------------------ resolution

let isUrl (s: string) =
    s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
    || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

let isUnc (s: string) = s.StartsWith @"\\"

/// C:\x or C:/x - checked explicitly so behaviour does not depend on the OS the tool runs on.
let private isDrivePath (s: string) =
    s.Length > 2 && Char.IsLetter s.[0] && s.[1] = ':' && (s.[2] = '\\' || s.[2] = '/')

let private isAbsolute (s: string) = isUnc s || isDrivePath s || Path.IsPathRooted s

let private hasWildcard (s: string) = s.IndexOfAny([| '*'; '?' |]) >= 0

let private withMsiExtension (name: string) =
    if hasWildcard name || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) then name else name + ".msi"

let private httpClient = lazy (new HttpClient(Timeout = TimeSpan.FromMinutes 30.0))

/// Downloads a URL into `downloadDir`. Raises MRU110 (HTTP 404) or MRU111.
let private download (url: string) (downloadDir: string) : string =
    let fileName =
        let segment = Uri(url).Segments |> Array.tryLast |> Option.defaultValue "package.msi"
        let n = Uri.UnescapeDataString(segment).Trim('/')
        if n = "" then "package.msi" else n
    let target = Path.Combine(downloadDir, fileName)
    try
        Directory.CreateDirectory downloadDir |> ignore
        use response = httpClient.Value.GetAsync(url).GetAwaiter().GetResult()
        if response.StatusCode = Net.HttpStatusCode.NotFound then
            raiseMru "MRU110" (sprintf "%s returned HTTP 404 (not found)." url)
        if not response.IsSuccessStatusCode then
            raiseMru "MRU111" (sprintf "%s returned HTTP %d %s." url (int response.StatusCode) response.ReasonPhrase)
        use input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        use output = File.Create target
        input.CopyTo output
        target
    with
    | MruException _ -> reraise ()
    | ex -> raiseMru "MRU111" (sprintf "Download of %s failed: %s" url (firstLine ex.Message))

/// Finds the files for one entry in a folder (wildcards allowed, .msi added if missing).
let private findInFolder (fullPath: string) : string list =
    let dir = Path.GetDirectoryName fullPath
    let pattern = Path.GetFileName fullPath
    let dir = if String.IsNullOrEmpty dir then Environment.CurrentDirectory else dir
    try
        if not (Directory.Exists dir) then
            raiseMru "MRU111" (sprintf "Folder '%s' does not exist or is not accessible." dir)
        if hasWildcard pattern then
            match Directory.GetFiles(dir, pattern) |> Array.sort |> List.ofArray with
            | [] -> raiseMru "MRU110" (sprintf "No file matches '%s' in %s." pattern dir)
            | files -> files
        elif File.Exists fullPath then
            [ fullPath ]
        elif File.Exists(fullPath + ".msi") then
            [ fullPath + ".msi" ]
        else
            raiseMru "MRU110" (sprintf "'%s' not found in %s." pattern dir)
    with
    | MruException _ -> reraise ()
    | ex -> raiseMru "MRU111" (sprintf "%s: %s" dir (firstLine ex.Message))

/// Resolves --install entries against the package source.
///  * absolute paths and URLs are used as they are,
///  * relative names are looked up in `source` (default: current directory),
///  * URLs are downloaded to `downloadDir`,
///  * Direct mode passes paths to the server unchanged (checked from here only if they are UNC paths).
let resolve (source: string option) (mode: SourceMode) (downloadDir: string) (entries: string list) : ResolvedPackage list =
    let source = source |> Option.map (fun s -> s.Trim()) |> Option.filter (fun s -> s <> "")
    let resolveEntry (entry: string) : (string * bool * bool) list = // (path, readable here, must copy)
        let entry = entry.Trim()
        let sourceIsUrl = source |> Option.exists isUrl
        if isUrl entry || (sourceIsUrl && not (isAbsolute entry)) then
            let url =
                if isUrl entry then entry
                else source.Value.TrimEnd('/') + "/" + Uri.EscapeDataString(withMsiExtension entry)
            if hasWildcard entry then
                raiseMru "MRU110" (sprintf "Wildcards are not supported for URL sources ('%s')." entry)
            [ download url downloadDir, true, true ] // downloaded files are always copied
        else
            let full =
                if isAbsolute entry then entry
                else Path.Combine(defaultArg source Environment.CurrentDirectory, entry)
            match mode with
            | SourceMode.Direct when not (isUnc full) ->
                // a path on the server - cannot be checked from here
                if hasWildcard full then
                    raiseMru "MRU110" (sprintf "Wildcards need a folder this computer can read; '%s' is not a UNC path." full)
                [ withMsiExtension full, false, false ]
            | _ -> findInFolder full |> List.map (fun p -> (if isUnc p then p else Path.GetFullPath p), true, (mode = SourceMode.Copy))

    let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
    [ for entry in entries do
          for (path, readable, copy) in resolveEntry entry do
              if seen.Add path then
                  let info, infoError =
                      if readable then
                          match tryReadMsiInfo path with
                          | Ok i -> Some i, None
                          | Result.Error e -> None, Some e
                      else
                          None, Some "Direct mode: the package is opened by the server, not read here."
                  yield
                      { Entry = entry
                        FileName = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar))
                        Path = path
                        Copy = copy
                        Info = info
                        InfoError = infoError } ]
