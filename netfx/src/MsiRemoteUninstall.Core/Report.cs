using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MsiRemoteUninstall
{
    /// <summary>Console output, tables, CSV reports and the error-code summary.</summary>
    public static class Report
    {
        private static readonly object ConsoleLock = new object();

        public static void Log(ConsoleColor? color, string text)
        {
            lock (ConsoleLock)
            {
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.WriteLine(text);
                if (color.HasValue) Console.ResetColor();
            }
        }

        public static void Info(string text) => Log(ConsoleColor.Cyan, text);
        public static void Warn(string text) => Log(ConsoleColor.Yellow, "WARNING: " + text);
        public static void Error(string text) => Log(ConsoleColor.Red, "ERROR: " + text);
        public static void Ok(string text) => Log(ConsoleColor.Green, text);

        public static void WarnCode(string code, string detail) =>
            Log(ConsoleColor.Yellow, $"WARNING [{code}] {ErrorCatalog.Get(code).Title}: {detail}");

        /// <summary>"[server] [CODE] Title: detail", coloured by severity.</summary>
        public static void LogCode(string computer, string code, string detail)
        {
            var e = ErrorCatalog.Get(code);
            var text = $"[{computer}] [{code}] {e.Title}: {detail}";
            if (e.IsError) Error(text);
            else if (e.IsWarning) Warn(text);
            else Log(null, text);
        }

        public static void PrintTable(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int maxWidth)
        {
            string Clip(string s)
            {
                s = s ?? "";
                return s.Length > maxWidth ? s.Substring(0, maxWidth - 3) + "..." : s;
            }

            var clipped = rows.Select(r => r.Select(Clip).ToList()).ToList();
            var widths = headers.Select((h, i) => Math.Max(h.Length, clipped.Count == 0 ? 0 : clipped.Max(r => r[i].Length))).ToList();
            string Line(IEnumerable<string> cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();

            lock (ConsoleLock)
            {
                Console.WriteLine();
                Console.WriteLine(Line(headers));
                Console.WriteLine(Line(widths.Select(w => new string('-', w))));
                foreach (var r in clipped) Console.WriteLine(Line(r));
                Console.WriteLine();
            }
        }

        private static string CsvEscape(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>Writes a CSV; returns false (warning MRU501) on failure.</summary>
        public static bool WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));
                foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(CsvEscape)));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                Ok("Report saved to: " + Path.GetFullPath(path));
                return true;
            }
            catch (Exception ex)
            {
                WarnCode("MRU501", $"{path}: {TextUtil.FirstLine(ex.Message)}");
                return false;
            }
        }

        /// <summary>Every distinct code of the run with count and remediation steps.</summary>
        public static void PrintCodeSummary(IEnumerable<string> codes)
        {
            var distinct = codes.Where(c => !string.IsNullOrEmpty(c)).GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) return;
            lock (ConsoleLock)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Problems / notices found in this run:");
                Console.ResetColor();
                foreach (var g in distinct)
                {
                    var e = ErrorCatalog.Get(g.Key);
                    Console.WriteLine();
                    Console.ForegroundColor = e.IsError ? ConsoleColor.Red : e.IsWarning ? ConsoleColor.Yellow : ConsoleColor.Gray;
                    Console.WriteLine($"[{g.Key}] {e.Title}  ({g.Count()} occurrence(s))");
                    Console.ResetColor();
                    for (var i = 0; i < e.Remediation.Count; i++) Console.WriteLine($"   {i + 1}. {e.Remediation[i]}");
                }
                Console.WriteLine();
                Console.WriteLine("Full explanations: MsiRemoteUninstall --explain <code>  (or see ERROR-CODES.md)");
            }
        }

        // ------------------------------------------------------------ operation (install / uninstall) report

        private static readonly string[] OperationHeaders =
            { "ComputerName", "Msi", "DisplayName", "Version", "ProductCode", "Status", "ExitCode", "ErrorCode", "Message", "LogFile" };

        private static IReadOnlyList<string> Row(OperationResult r) => new[]
        {
            r.ComputerName, r.Msi, r.DisplayName, r.Version, r.ProductCode, r.Status.ToString(),
            r.ExitCode?.ToString() ?? "", r.ErrorCode, r.Message, r.LogFile
        };

        public static bool ShowOperationResults(IReadOnlyList<OperationResult> results, string reportPath)
        {
            var sorted = results.OrderBy(r => r.ComputerName.ToUpperInvariant(), StringComparer.Ordinal).ThenBy(r => r.Msi).ToList();
            PrintTable(new[] { "ComputerName", "Msi", "DisplayName", "Version", "Status", "ExitCode", "ErrorCode", "Message" },
                       sorted.Select(r => { var x = Row(r); return (IReadOnlyList<string>)new[] { x[0], x[1], x[2], x[3], x[5], x[6], x[7], x[8] }; }),
                       45);
            var written = WriteCsv(reportPath, OperationHeaders, sorted.Select(Row));
            PrintTable(new[] { "Status", "Count" },
                       results.GroupBy(r => r.Status.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal)
                              .Select(g => (IReadOnlyList<string>)new[] { g.Key, g.Count().ToString() }),
                       40);
            PrintCodeSummary(results.Select(r => r.ErrorCode).Concat(written ? new string[0] : new[] { "MRU501" }));
            return written;
        }

        // ------------------------------------------------------------ inventory report

        private static readonly string[] InventoryHeaders =
            { "ComputerName", "DisplayName", "Version", "Publisher", "InstallDate", "Architecture", "ProductCode", "ErrorCode", "Message" };

        private static IReadOnlyList<string> Row(InventoryRow r) =>
            r.Product != null
                ? new[] { r.ComputerName, r.Product.DisplayName, r.Product.Version, r.Product.Publisher, r.Product.InstallDate, r.Product.Architecture, r.Product.ProductCode, "", "" }
                : new[] { r.ComputerName, "", "", "", "", "", "", r.ErrorCode ?? "", r.ErrorMessage ?? "" };

        public static bool ShowInventory(IReadOnlyList<InventoryRow> rows, string reportPath)
        {
            var products = rows.Where(r => r.Product != null)
                               .OrderBy(r => r.ComputerName.ToUpperInvariant(), StringComparer.Ordinal)
                               .ThenBy(r => r.Product.DisplayName).ToList();
            var failed = rows.Where(r => r.IsError).ToList();
            if (products.Count == 0)
                Warn("No matching MSI products found.");
            else
                PrintTable(new[] { "ComputerName", "DisplayName", "Version", "Publisher", "ProductCode" },
                           products.Select(r => (IReadOnlyList<string>)new[] { r.ComputerName, r.Product.DisplayName, r.Product.Version, r.Product.Publisher, r.Product.ProductCode }),
                           60);
            if (failed.Count > 0)
            {
                Warn($"{failed.Count} server(s) could not be queried:");
                PrintTable(new[] { "ComputerName", "ErrorCode", "Message" },
                           failed.Select(r => (IReadOnlyList<string>)new[] { r.ComputerName, r.ErrorCode, r.ErrorMessage }), 110);
            }
            if (products.Count > 0)
                PrintTable(new[] { "ComputerName", "MatchingMsis" },
                           products.GroupBy(r => r.ComputerName).OrderBy(g => g.Key, StringComparer.Ordinal)
                                   .Select(g => (IReadOnlyList<string>)new[] { g.Key, g.Count().ToString() }),
                           60);
            var written = rows.Count == 0 || WriteCsv(reportPath, InventoryHeaders, products.Concat(failed).Select(Row));
            PrintCodeSummary(failed.Select(r => r.ErrorCode).Concat(written ? new string[0] : new[] { "MRU501" }));
            return written;
        }
    }
}
