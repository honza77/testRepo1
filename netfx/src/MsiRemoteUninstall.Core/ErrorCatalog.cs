using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MsiRemoteUninstall
{
    /// <summary>One entry of the error catalog (code, explanation, remediation).</summary>
    public sealed class ErrorInfo
    {
        public string Code { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public string Title { get; set; }
        public string AppliesTo { get; set; }
        public string Explanation { get; set; }
        public IReadOnlyList<string> Remediation { get; set; }

        public bool IsError => Severity == "Error";
        public bool IsWarning => Severity == "Warning";
    }

    public sealed class ExitCodeInfo
    {
        public int ExitCode { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Error catalog shared by all tools. The data is generated from the F# catalog
    /// (dotnet/MsiRemoteUninstall/Errors.fs) into shared/error-catalog.json and embedded here.
    /// </summary>
    public static class ErrorCatalog
    {
        private sealed class Data
        {
            public List<ErrorInfo> ErrorCodes { get; set; }
            public List<ExitCodeInfo> ExitCodes { get; set; }
        }

        private static readonly Lazy<string> RawJson = new Lazy<string>(() =>
        {
            using (var stream = typeof(ErrorCatalog).Assembly.GetManifestResourceStream("error-catalog.json"))
            {
                if (stream == null) throw new InvalidOperationException("Embedded resource error-catalog.json is missing.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        });

        private static readonly Lazy<Data> Loaded = new Lazy<Data>(() =>
            JsonSerializer.Deserialize<Data>(RawJson.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

        private static readonly Lazy<Dictionary<string, ErrorInfo>> ByCode = new Lazy<Dictionary<string, ErrorInfo>>(() =>
            Loaded.Value.ErrorCodes.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase));

        public static IReadOnlyList<ErrorInfo> All => Loaded.Value.ErrorCodes;
        public static IReadOnlyList<ExitCodeInfo> ExitCodes => Loaded.Value.ExitCodes;
        public static string Json => RawJson.Value;

        /// <summary>Finds an entry; any case, and bare msiexec numbers ("1603" = MSI1603).</summary>
        public static ErrorInfo TryFind(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var c = code.Trim().ToUpperInvariant();
            if (c.All(char.IsDigit)) c = "MSI" + c;
            return ByCode.Value.TryGetValue(c, out var e) ? e : null;
        }

        public static ErrorInfo Get(string code) => TryFind(code) ?? ByCode.Value["MRU999"];

        /// <summary>Catalog code for an msiexec exit code.</summary>
        public static string MsiCode(int exitCode)
        {
            var c = "MSI" + exitCode;
            return ByCode.Value.ContainsKey(c) ? c : "MSI-OTHER";
        }

        // ------------------------------------------------------------ rendering

        public static string ExplainText(ErrorInfo e)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{e.Code}  {e.Title}");
            sb.AppendLine($"Category: {e.Category}   Severity: {e.Severity}   Applies to: {e.AppliesTo}");
            sb.AppendLine();
            sb.AppendLine("What it means:");
            sb.AppendLine(TextUtil.Wrap(e.Explanation, "  ", 100));
            sb.AppendLine();
            sb.AppendLine("How to fix it:");
            for (var i = 0; i < e.Remediation.Count; i++)
                sb.AppendLine(TextUtil.Wrap(e.Remediation[i], $"  {i + 1}. ", 100));
            return sb.ToString();
        }

        public static string ListText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Error codes (use --explain <code> for details and remediation):");
            foreach (var group in All.GroupBy(e => e.Category))
            {
                sb.AppendLine();
                sb.AppendLine(group.Key);
                foreach (var e in group)
                    sb.AppendLine($"  {e.Code,-10} {e.Severity,-8} {e.Title}");
            }
            sb.AppendLine();
            sb.AppendLine("Process exit codes:");
            foreach (var x in ExitCodes)
                sb.AppendLine($"  {x.ExitCode,-3} {x.Title,-25} {x.Description}");
            return sb.ToString();
        }

        public static string Markdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Error codes").AppendLine();
            sb.AppendLine("| Exit code | Meaning | Description |").AppendLine("|---|---|---|");
            foreach (var x in ExitCodes) sb.AppendLine($"| {x.ExitCode} | {x.Title} | {x.Description} |");
            foreach (var group in All.GroupBy(e => e.Category))
            {
                sb.AppendLine().AppendLine($"## {group.Key}");
                foreach (var e in group)
                {
                    sb.AppendLine().AppendLine($"### {e.Code}").AppendLine();
                    sb.AppendLine($"**{e.Title}** - severity: {e.Severity}, applies to: {e.AppliesTo}").AppendLine();
                    sb.AppendLine(e.Explanation).AppendLine().AppendLine("How to fix it:").AppendLine();
                    for (var i = 0; i < e.Remediation.Count; i++) sb.AppendLine($"{i + 1}. {e.Remediation[i]}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Exception carrying a catalog code and a detail message.</summary>
    public sealed class MruException : Exception
    {
        public MruException(string code, string detail) : base(Format(code, detail))
        {
            Code = code;
            Detail = detail ?? "";
        }

        public string Code { get; }
        public string Detail { get; }

        private static string Format(string code, string detail)
        {
            var title = ErrorCatalog.Get(code).Title;
            return string.IsNullOrWhiteSpace(detail) ? $"[{code}] {title}" : $"[{code}] {title}: {detail}";
        }
    }

    public static class TextUtil
    {
        /// <summary>First lines of a (multi-line) Windows error message joined into one line.</summary>
        public static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        /// <summary>Word-wraps text; continuation lines are indented to the width of the first indent.</summary>
        public static string Wrap(string text, string indent, int width)
        {
            var lines = new List<string>();
            var line = indent;
            var cont = new string(' ', indent.Length);
            foreach (var word in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length + 1 > width && line.Trim().Length > 0)
                {
                    lines.Add(line.TrimEnd());
                    line = cont;
                }
                line += (line.EndsWith(" ") ? "" : " ") + word;
            }
            lines.Add(line.TrimEnd());
            return string.Join(Environment.NewLine, lines);
        }
    }
}
