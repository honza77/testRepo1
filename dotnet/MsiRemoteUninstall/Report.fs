/// Console tables and CSV reports.
module MsiRemoteUninstall.Report

open System
open System.IO
open System.Text
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

let writeCsv (path: string) (headers: string list) (rows: string list list) =
    try
        let sb = StringBuilder()
        let append (cells: string list) = sb.AppendLine(cells |> List.map csvEscape |> String.concat ",") |> ignore
        append headers
        rows |> List.iter append
        File.WriteAllText(path, sb.ToString(), UTF8Encoding(true))
        ok (sprintf "Report saved to: %s" (Path.GetFullPath path))
    with ex ->
        warn (sprintf "Could not write report to '%s': %s" path ex.Message)

// ------------------------------------------------------------- uninstall report

let private uninstallHeaders =
    [ "ComputerName"; "Msi"; "DisplayName"; "Version"; "ProductCode"; "Status"; "ExitCode"; "Message"; "LogFile" ]

let private uninstallRow (r: UninstallResult) =
    [ r.ComputerName
      r.Msi
      r.DisplayName
      r.Version
      r.ProductCode
      string r.Status
      (match r.ExitCode with
       | Some c -> string c
       | None -> "")
      r.Message
      r.LogFile ]

let showUninstallResults (results: UninstallResult list) (reportPath: string) =
    let sorted = results |> List.sortBy (fun r -> r.ComputerName.ToUpperInvariant(), r.Msi)
    printTable
        [ "ComputerName"; "Msi"; "DisplayName"; "Version"; "Status"; "ExitCode"; "Message" ]
        (sorted
         |> List.map (fun r ->
             let row = uninstallRow r
             [ row.[0]; row.[1]; row.[2]; row.[3]; row.[5]; row.[6]; row.[7] ]))
        45
    writeCsv reportPath uninstallHeaders (sorted |> List.map uninstallRow)
    printTable
        [ "Status"; "Count" ]
        (results
         |> List.countBy (fun r -> string r.Status)
         |> List.sortBy fst
         |> List.map (fun (s, c) -> [ s; string c ]))
        40
    if results |> List.exists (fun r -> r.Status = SuccessRebootRequired || r.Status = SuccessRebootInitiated) then
        warn "One or more servers require a reboot to complete the uninstall."

// ------------------------------------------------------------- inventory report

let private inventoryHeaders =
    [ "ComputerName"; "DisplayName"; "Version"; "Publisher"; "InstallDate"; "Architecture"; "ProductCode" ]

let private inventoryRow (r: InventoryRow) =
    match r.Product, r.Error with
    | Some p, _ -> [ r.ComputerName; p.DisplayName; p.Version; p.Publisher; p.InstallDate; p.Architecture; p.ProductCode ]
    | None, Some e -> [ r.ComputerName; "ERROR: " + e; ""; ""; ""; ""; "" ]
    | None, None -> [ r.ComputerName; ""; ""; ""; ""; ""; "" ]

let showInventory (rows: InventoryRow list) (reportPath: string) =
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
        printTable [ "ComputerName"; "Message" ] (failed |> List.map (fun r -> [ r.ComputerName; r.Error.Value ])) 120
    if not (List.isEmpty products) then
        printTable
            [ "ComputerName"; "MatchingMsis" ]
            (products
             |> List.countBy (fun r -> r.ComputerName)
             |> List.sortBy fst
             |> List.map (fun (c, n) -> [ c; string n ]))
            60
    if not (List.isEmpty rows) then
        writeCsv reportPath inventoryHeaders ((sorted @ failed) |> List.map inventoryRow)
