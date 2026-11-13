/// Console output, tables, CSV reports and the error-code summary.
module MsiRemoteUninstall.Report

open System
open System.IO
open System.Text
open MsiRemoteUninstall.Errors
open MsiRemoteUninstall.Domain

let private consoleLock = obj ()

/// Thread-safe coloured console line.
let log (color: ConsoleColor option) (text: string) =
    lock consoleLock (fun () ->
        match color with
        | Some c ->
            Console.ForegroundColor <- c
            Console.WriteLine text
            Console.ResetColor()
        | None -> Console.WriteLine text)

let info = log (Some ConsoleColor.Cyan)
let warn text = log (Some ConsoleColor.Yellow) ("WARNING: " + text)
let error text = log (Some ConsoleColor.Red) ("ERROR: " + text)
let ok = log (Some ConsoleColor.Green)

/// Warning with a catalog code, e.g. "WARNING [MRU108] Option ignored: ...".
let warnCode code (detail: string) =
    log (Some ConsoleColor.Yellow) (sprintf "WARNING [%s] %s: %s" code (Errors.get code).Title detail)

/// Prints a simple fixed-width table. Long cells are truncated to maxWidth.
let printTable (headers: string list) (rows: string list list) (maxWidth: int) =
    let clip (s: string) =
        let s = if isNull s then "" else s
        if s.Length > maxWidth then s.Substring(0, maxWidth - 3) + "..." else s
    let rows = rows |> List.map (List.map clip)
    let widths =
        headers
        |> List.mapi (fun i h -> max h.Length (rows |> List.fold (fun m r -> max m (r.[i].Length)) 0))
    let line (cells: string list) =
        List.zip cells widths |> List.map (fun (c, w) -> c.PadRight w) |> String.concat "  " |> fun s -> s.TrimEnd()
    lock consoleLock (fun () ->
        Console.WriteLine()
        Console.WriteLine(line headers)
        Console.WriteLine(line (widths |> List.map (fun w -> String('-', w))))
        rows |> List.iter (line >> Console.WriteLine)
        Console.WriteLine())

let private csvEscape (s: string) =
    let s = if isNull s then "" else s
    if s.IndexOfAny([| ','; '"'; '\r'; '\n' |]) >= 0 then "\"" + s.Replace("\"", "\"\"") + "\"" else s

/// Writes a CSV file. Returns false (and warns with MRU501) if it fails.
let writeCsv (path: string) (headers: string list) (rows: string list list) : bool =
    try
        let sb = StringBuilder()
        let append (cells: string list) = sb.AppendLine(cells |> List.map csvEscape |> String.concat ",") |> ignore
        append headers
        rows |> List.iter append
        File.WriteAllText(path, sb.ToString(), UTF8Encoding(true))
        ok (sprintf "Report saved to: %s" (Path.GetFullPath path))
        true
    with ex ->
        warnCode "MRU501" (sprintf "%s: %s" path (firstLine ex.Message))
        false

/// Prints every distinct error code that occurred, with title, count and the remediation steps.
let printCodeSummary (codes: string list) =
    let distinct =
        codes
        |> List.filter (fun c -> not (String.IsNullOrEmpty c))
        |> List.countBy id
        |> List.sortBy fst
    if not (List.isEmpty distinct) then
        lock consoleLock (fun () ->
            Console.WriteLine()
            Console.ForegroundColor <- ConsoleColor.Yellow
            Console.WriteLine "Problems / notices found in this run:"
            Console.ResetColor()
            for (code, count) in distinct do
                let e = Errors.get code
                Console.WriteLine()
                Console.ForegroundColor <-
                    (match e.Severity with
                     | Severity.Error -> ConsoleColor.Red
                     | Severity.Warning -> ConsoleColor.Yellow
                     | Severity.Info -> ConsoleColor.Gray)
                Console.WriteLine(sprintf "[%s] %s  (%d occurrence(s))" code e.Title count)
                Console.ResetColor()
                e.Remediation |> List.iteri (fun i r -> Console.WriteLine(sprintf "   %d. %s" (i + 1) r))
            Console.WriteLine()
            Console.WriteLine "Full explanations: MsiRemoteUninstall --explain <code>  (or see ERROR-CODES.md)")

// ------------------------------------------------------------- uninstall report

let private uninstallHeaders =
    [ "ComputerName"; "Msi"; "DisplayName"; "Version"; "ProductCode"; "Status"; "ExitCode"; "ErrorCode"; "Message"; "LogFile" ]

let private uninstallRow (r: OperationResult) =
    [ r.ComputerName
      r.Msi
      r.DisplayName
      r.Version
      r.ProductCode
      string r.Status
      (match r.ExitCode with
       | Some c -> string c
       | None -> "")
      r.ErrorCode
      r.Message
      r.LogFile ]

/// Shows the results table, writes the CSV, prints the status and error-code summaries.
/// Returns false if the CSV could not be written.
let showOperationResults (results: OperationResult list) (reportPath: string) : bool =
    let sorted = results |> List.sortBy (fun r -> r.ComputerName.ToUpperInvariant(), r.Msi)
    printTable
        [ "ComputerName"; "Msi"; "DisplayName"; "Version"; "Status"; "ExitCode"; "ErrorCode"; "Message" ]
        (sorted
         |> List.map (fun r ->
             let row = uninstallRow r
             [ row.[0]; row.[1]; row.[2]; row.[3]; row.[5]; row.[6]; row.[7]; row.[8] ]))
        45
    let written = writeCsv reportPath uninstallHeaders (sorted |> List.map uninstallRow)
    printTable
        [ "Status"; "Count" ]
        (results
         |> List.countBy (fun r -> string r.Status)
         |> List.sortBy fst
         |> List.map (fun (s, c) -> [ s; string c ]))
        40
    printCodeSummary ((results |> List.map (fun r -> r.ErrorCode)) @ (if written then [] else [ "MRU501" ]))
    written

// ------------------------------------------------------------- inventory report

let private inventoryHeaders =
    [ "ComputerName"; "DisplayName"; "Version"; "Publisher"; "InstallDate"; "Architecture"; "ProductCode"; "ErrorCode"; "Message" ]

let private inventoryRow (r: InventoryRow) =
    match r.Product, r.Error with
    | Some p, _ -> [ r.ComputerName; p.DisplayName; p.Version; p.Publisher; p.InstallDate; p.Architecture; p.ProductCode; ""; "" ]
    | None, Some(code, msg) -> [ r.ComputerName; ""; ""; ""; ""; ""; ""; code; msg ]
    | None, None -> [ r.ComputerName; ""; ""; ""; ""; ""; ""; ""; "" ]

let showInventory (rows: InventoryRow list) (reportPath: string) : bool =
    let products = rows |> List.filter (fun r -> r.Product.IsSome)
    let failed = rows |> List.filter (fun r -> r.Error.IsSome)
    let sorted =
        products |> List.sortBy (fun r -> r.ComputerName.ToUpperInvariant(), r.Product.Value.DisplayName)
    if List.isEmpty products then
        warn "No matching MSI products found."
    else
        printTable
            [ "ComputerName"; "DisplayName"; "Version"; "Publisher"; "ProductCode" ]
            (sorted
             |> List.map (fun r ->
                 let p = r.Product.Value
                 [ r.ComputerName; p.DisplayName; p.Version; p.Publisher; p.ProductCode ]))
            60
    if not (List.isEmpty failed) then
        warn (sprintf "%d server(s) could not be queried:" failed.Length)
        printTable
            [ "ComputerName"; "ErrorCode"; "Message" ]
            (failed |> List.map (fun r -> let c, m = r.Error.Value in [ r.ComputerName; c; m ]))
            110
    if not (List.isEmpty products) then
        printTable
            [ "ComputerName"; "MatchingMsis" ]
            (products
             |> List.countBy (fun r -> r.ComputerName)
             |> List.sortBy fst
             |> List.map (fun (c, n) -> [ c; string n ]))
            60
    let written =
        if List.isEmpty rows then true
        else writeCsv reportPath inventoryHeaders ((sorted @ failed) |> List.map inventoryRow)
    printCodeSummary ((failed |> List.map (fun r -> fst r.Error.Value)) @ (if written then [] else [ "MRU501" ]))
    written
