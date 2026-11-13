using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MsiRemoteUninstall
{
    public enum FilterMode { Substring, Wildcard, Regex }

    public enum OperationStatus
    {
        Success,
        SuccessRebootRequired,
        SuccessRebootInitiated,
        NotInstalled,
        NotFound,
        WouldUninstall,
        WouldInstall,
        AlreadyInstalled,
        Failed,
        ExecutionError,
        ConnectionFailed
    }

    /// <summary>An MSI product found in the registry Uninstall keys of a server.</summary>
    public sealed class InstalledProduct
    {
        public string ProductCode { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public string Architecture { get; set; } = "";
    }

    /// <summary>One row of the uninstall / install report.</summary>
    public sealed class OperationResult
    {
        public string ComputerName { get; set; } = "";
        public string Msi { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Version { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public OperationStatus Status { get; set; }
        public int? ExitCode { get; set; }
        public string ErrorCode { get; set; } = "";
        public string Message { get; set; } = "";
        public string LogFile { get; set; } = "";

        public static OperationResult Create(string computer, string msi, InstalledProduct product, OperationStatus status,
                                             int? exitCode, string errorCode, string message, string logFile) =>
            new OperationResult
            {
                ComputerName = computer,
                Msi = msi,
                DisplayName = product?.DisplayName ?? "",
                Version = product?.Version ?? "",
                ProductCode = product?.ProductCode ?? "",
                Status = status,
                ExitCode = exitCode,
                ErrorCode = errorCode ?? "",
                Message = message ?? "",
                LogFile = logFile ?? ""
            };
    }

    /// <summary>One row of the inventory (--list) report.</summary>
    public sealed class InventoryRow
    {
        public string ComputerName { get; set; }
        public InstalledProduct Product { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public bool IsError => ErrorCode != null;
    }

    /// <summary>Status, message and catalog code for an msiexec exit code.</summary>
    public sealed class Outcome
    {
        public Outcome(OperationStatus status, string message, string errorCode)
        {
            Status = status;
            Message = message;
            ErrorCode = errorCode;
        }

        public OperationStatus Status { get; }
        public string Message { get; }
        public string ErrorCode { get; }
    }

    public static class ListFiles
    {
        /// <summary>One entry per line; blank lines and '#' comments are ignored.</summary>
        public static List<string> Read(string path)
        {
            if (!File.Exists(path))
                throw new MruException("MRU104", $"{path} (current directory: {Environment.CurrentDirectory})");
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                throw new MruException("MRU105", $"{path}: {ex.Message}");
            }
            return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
        }

        /// <summary>Inline values + file contents, case-insensitive de-duplication, order preserved.</summary>
        public static List<string> Merge(IEnumerable<string> inline, IEnumerable<string> files)
        {
            var all = (inline ?? Enumerable.Empty<string>()).Select(s => s.Trim())
                .Concat((files ?? Enumerable.Empty<string>()).SelectMany(Read));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return all.Where(s => s.Length > 0 && seen.Add(s)).ToList();
        }
    }

    public static class ProductCodes
    {
        private static readonly Regex Guid =
            new Regex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$");

        /// <summary>Canonical "{XXXXXXXX-...}" form, or null if the text is not a product code.</summary>
        public static string TryNormalize(string s)
        {
            var t = (s ?? "").Trim();
            return Guid.IsMatch(t) ? "{" + t.Trim('{', '}').ToUpperInvariant() + "}" : null;
        }

        public static bool IsProductCode(string s) => TryNormalize(s) != null;
    }

    public static class Matching
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>PowerShell-style wildcard (* ? [abc] [a-z], ` escapes) to an anchored regex.</summary>
        public static string WildcardToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.'); break;
                    case '`' when i + 1 < pattern.Length:
                        i++;
                        sb.Append(Regex.Escape(pattern[i].ToString()));
                        break;
                    case '[':
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0) throw new ArgumentException($"Unterminated '[' in wildcard pattern '{pattern}'");
                        var inner = pattern.Substring(i + 1, close - i - 1);
                        if (inner.Length == 0) throw new ArgumentException($"Empty '[]' in wildcard pattern '{pattern}'");
                        sb.Append('[').Append(inner.Replace(@"\", @"\\").Replace("^", @"\^").Replace("[", @"\[")).Append(']');
                        i = close;
                        break;
                    default: sb.Append(Regex.Escape(c.ToString())); break;
                }
            }
            return sb.Append('$').ToString();
        }

        /// <summary>Case-insensitive matcher. Throws ArgumentException on invalid patterns.</summary>
        public static Func<string, bool> CreateMatcher(FilterMode mode, string pattern)
        {
            switch (mode)
            {
                case FilterMode.Substring:
                    return s => s.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                case FilterMode.Wildcard:
                    var w = new Regex(WildcardToRegex(pattern), Options);
                    return s => w.IsMatch(s);
                default:
                    var r = new Regex(pattern, Options);
                    return s => r.IsMatch(s);
            }
        }

        /// <summary>List mode: DisplayName, Publisher or ProductCode matches ANY matcher.</summary>
        public static bool PassesFilters(IReadOnlyList<Func<string, bool>> matchers, InstalledProduct p) =>
            matchers.Count == 0 ||
            matchers.Any(m => new[] { p.DisplayName, p.Publisher, p.ProductCode }.Any(f => !string.IsNullOrEmpty(f) && m(f)));

        /// <summary>Uninstall mode: product code (exact) or display-name wildcard.</summary>
        public static List<InstalledProduct> FindProducts(IEnumerable<InstalledProduct> installed, string msi)
        {
            var code = ProductCodes.TryNormalize(msi);
            if (code != null) return installed.Where(p => p.ProductCode == code).ToList();
            var m = CreateMatcher(FilterMode.Wildcard, msi);
            return installed.Where(p => m(p.DisplayName)).ToList();
        }
    }

    public static class MsiExitCodes
    {
        public static Outcome InterpretUninstall(int code)
        {
            switch (code)
            {
                case 0: return new Outcome(OperationStatus.Success, "Uninstalled successfully.", "");
                case 3010: return new Outcome(OperationStatus.SuccessRebootRequired, "Uninstalled; reboot required.", "MSI3010");
                case 1641: return new Outcome(OperationStatus.SuccessRebootInitiated, "Uninstalled; reboot initiated.", "MSI1641");
                case 1605: return new Outcome(OperationStatus.NotInstalled, "Product is not installed.", "MSI1605");
                default: return Failure(code);
            }
        }

        public static Outcome InterpretInstall(int code)
        {
            switch (code)
            {
                case 0: return new Outcome(OperationStatus.Success, "Installed successfully.", "");
                case 3010: return new Outcome(OperationStatus.SuccessRebootRequired, "Installed; reboot required.", "MSI3010");
                case 1641: return new Outcome(OperationStatus.SuccessRebootInitiated, "Installed; reboot initiated.", "MSI1641");
                default: return Failure(code);
            }
        }

        private static Outcome Failure(int code)
        {
            var errorCode = ErrorCatalog.MsiCode(code);
            return new Outcome(OperationStatus.Failed, $"msiexec exit code {code}: {ErrorCatalog.Get(errorCode).Title}.", errorCode);
        }

        public static bool IsFailure(OperationStatus s) =>
            s == OperationStatus.Failed || s == OperationStatus.ExecutionError || s == OperationStatus.ConnectionFailed;
    }

    public static class Msiexec
    {
        private static readonly Regex PropertyName = new Regex(@"^[A-Za-z_][A-Za-z0-9_.]*$");

        /// <summary>Parses NAME=VALUE (MRU112 when invalid). strict also rejects ! and % (cmd.exe / WMI).</summary>
        public static KeyValuePair<string, string> ParseProperty(bool strict, string text)
        {
            var i = text?.IndexOf('=') ?? -1;
            if (i <= 0) throw new MruException("MRU112", $"'{text}' is not in the form NAME=VALUE.");
            var name = text.Substring(0, i).Trim();
            var value = text.Substring(i + 1);
            if (!PropertyName.IsMatch(name)) throw new MruException("MRU112", $"'{name}' is not a valid property name.");
            if (value.IndexOf('"') >= 0) throw new MruException("MRU112", $"The value of {name} contains a double quote.");
            if (strict && (value.IndexOf('!') >= 0 || value.IndexOf('%') >= 0))
                throw new MruException("MRU112", $"The value of {name} contains ! or %, which cannot be passed through the WMI transport.");
            return new KeyValuePair<string, string>(name, value);
        }

        public static string FormatProperties(IEnumerable<KeyValuePair<string, string>> props) =>
            string.Join(" ", props.Select(p => $"{p.Key}=\"{p.Value}\""));

        public static string Arguments(string action, string target, string logFile, IReadOnlyCollection<KeyValuePair<string, string>> props)
        {
            var baseArgs = $"{action} \"{target}\" /qn /norestart /l*v \"{logFile}\"";
            return props.Count == 0 ? baseArgs : baseArgs + " " + FormatProperties(props);
        }
    }
}
