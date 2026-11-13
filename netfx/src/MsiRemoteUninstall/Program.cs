using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MsiRemoteUninstall
{
    public static class Program
    {
        public const int ExitOk = 0;
        public const int ExitPartialFailure = 1;
        public const int ExitInvalidInput = 2;
        public const int ExitCredentials = 3;
        public const int ExitAborted = 4;
        public const int ExitNoServerReachable = 5;
        public const int ExitInternal = 6;
        public const int ExitOkRebootRequired = 10;

        /// <summary>Exit code for an error that stops the whole run.</summary>
        public static int ExitCodeForFatal(string code)
        {
            if (code == "MRU502") return ExitAborted;
            switch (ErrorCatalog.Get(code).Category)
            {
                case "Input": return ExitInvalidInput;
                case "Credentials": return ExitCredentials;
                case "Internal": return ExitInternal;
                default: return ExitPartialFailure;
            }
        }

        private static void ReportFatal(string code, string detail)
        {
            var e = ErrorCatalog.Get(code);
            Report.Error($"[{code}] {e.Title}" + (string.IsNullOrWhiteSpace(detail) ? "" : ": " + detail));
            Console.WriteLine();
            Console.WriteLine(ErrorCatalog.ExplainText(e));
        }

        private static int ExitCodeFor(IReadOnlyCollection<string> servers, IReadOnlyCollection<OperationResult> results)
        {
            var unreachable = results.Where(r => r.Status == OperationStatus.ConnectionFailed).Select(r => r.ComputerName).Distinct().Count();
            if (unreachable == servers.Count) return ExitNoServerReachable;
            if (results.Any(r => MsiExitCodes.IsFailure(r.Status))) return ExitPartialFailure;
            if (results.Any(r => r.Status == OperationStatus.SuccessRebootRequired || r.Status == OperationStatus.SuccessRebootInitiated))
                return ExitOkRebootRequired;
            return ExitOk;
        }

        private static void Confirm(Options opts, string question)
        {
            if (opts.WhatIf)
            {
                Report.Warn("WhatIf mode: nothing will be changed on the servers.");
                return;
            }
            if (opts.Yes) return;
            Console.Write(question + " [y/N] ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer != "y" && answer != "yes") throw new MruException("MRU502", "Nothing was changed.");
        }

        private static int Run(Options opts)
        {
            var servers = ListFiles.Merge(opts.Computers, opts.ComputerListFiles);
            var msis = ListFiles.Merge(opts.Msis, opts.MsiListFiles);
            var installEntries = ListFiles.Merge(opts.InstallPackages, opts.InstallListFiles);
            var installMode = installEntries.Count > 0;

            // exactly one operation per run
            var modes = new[] { msis.Count > 0, opts.ListInstalled, installMode }.Count(m => m);
            if (modes > 1) throw new MruException("MRU109", "Use only one of --msi/--msi-list, --list, --install/--install-list.");
            if (servers.Count == 0) throw new MruException("MRU102", "Use --computer and/or --computer-list.");
            if (modes == 0) throw new MruException("MRU103", "Use --msi and/or --msi-list, --list to list installed MSIs, or --install to install packages.");

            // option combinations without effect
            if (opts.Filters.Count > 0 && !opts.ListInstalled)
                Report.WarnCode("MRU108", "--filter is only used together with --list.");
            if (opts.Transport == Transport.Wmi && (opts.UseSsl || opts.Port.HasValue || opts.Auth != AuthMode.Default || opts.SkipCertCheck))
                Report.WarnCode("MRU108", "--ssl / --port / --auth / --skip-cert-check apply to the psremoting transport only.");
            if (opts.Transport == Transport.PsRemoting && opts.SkipCertCheck && !opts.UseSsl)
                Report.WarnCode("MRU108", "--skip-cert-check has no effect without --ssl.");
            if (opts.CurrentUser && (opts.UserName != null || opts.PasswordEnvVar != null))
                Report.WarnCode("MRU108", "--current-user given; --user / --password-env are ignored.");
            if (!installMode && (opts.PackageSource != null || opts.SourceMode != SourceMode.Copy || opts.KeepStaged))
                Report.WarnCode("MRU108", "--package-source / --source-mode / --keep-staged are only used with --install.");
            if (opts.ListInstalled && opts.MsiProperties.Count > 0)
                Report.WarnCode("MRU108", "--msi-property is not used with --list.");

            var props = opts.MsiProperties.Select(p => Msiexec.ParseProperty(opts.Transport == Transport.Wmi, p)).ToList();

            // validate patterns before contacting anything
            var matchers = new List<Func<string, bool>>();
            if (opts.ListInstalled)
            {
                foreach (var f in opts.Filters.Select(s => s.Trim()).Where(s => s.Length > 0))
                {
                    try { matchers.Add(Matching.CreateMatcher(opts.FilterMode, f)); }
                    catch (ArgumentException ex) { throw new MruException("MRU106", $"{opts.FilterMode} filter '{f}': {ex.Message}"); }
                }
            }
            foreach (var m in msis.Where(m => !ProductCodes.IsProductCode(m)))
            {
                try { Matching.CreateMatcher(FilterMode.Wildcard, m); }
                catch (ArgumentException ex) { throw new MruException("MRU107", $"'{m}': {ex.Message}"); }
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Report.WarnCode("MRU503", "Not running on Windows - connections are expected to fail.");

            // resolve packages before asking for credentials (fails fast on typos)
            var packages = new List<ResolvedPackage>();
            if (installMode)
            {
                var sourceMode = opts.SourceMode;
                if (sourceMode == SourceMode.Direct && opts.PackageSource != null && Packages.IsUrl(opts.PackageSource))
                {
                    Report.WarnCode("MRU108", "--source-mode direct cannot be used with a URL source; packages are downloaded and copied.");
                    sourceMode = SourceMode.Copy;
                }
                var downloadDir = Path.Combine(Path.GetTempPath(), "MsiRemoteUninstall", Guid.NewGuid().ToString("N"));
                packages = Packages.Resolve(opts.PackageSource, sourceMode, downloadDir, installEntries);
                foreach (var p in packages.Where(p => p.InfoError != null && (p.Copy || Packages.IsUnc(p.Path))))
                    Report.WarnCode("MRU504", $"{p.FileName}: {p.InfoError}");
            }

            var reportPath = opts.ReportPath ?? Path.Combine(Environment.CurrentDirectory,
                $"{(opts.ListInstalled ? "MsiInventory" : installMode ? "MsiInstallReport" : "MsiUninstallReport")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            Console.WriteLine();
            Report.Info($"Transport     : {(opts.Transport == Transport.Wmi ? "WMI (DCOM)" : "PowerShell Remoting (WinRM)")}" +
                        (opts.Transport == Transport.PsRemoting && opts.UseSsl ? " over HTTPS" : ""));
            Report.Info($"Servers ({servers.Count})   : {string.Join(", ", servers)}");
            if (opts.ListInstalled)
                Report.Info(matchers.Count == 0 ? "Filter        : none (listing all MSI products)"
                                                : $"Filter ({opts.FilterMode}, any of): {string.Join(", ", opts.Filters)}");
            else if (installMode)
            {
                Report.Info($"Packages ({packages.Count})  :");
                foreach (var p in packages)
                {
                    var details = p.Info == null ? "" : $"{p.Info.ProductName} {p.Info.ProductVersion} {p.Info.ProductCode}";
                    Report.Info($"  {p.Path}  {details}{(p.Copy ? "" : "  (opened by the server)")}");
                }
            }
            else
                Report.Info($"MSIs ({msis.Count})      : {string.Join(", ", msis)}");
            if (props.Count > 0) Report.Info($"MSI properties: {Msiexec.FormatProperties(props)}");
            Console.WriteLine();

            var settings = new ConnectionSettings
            {
                Credential = ConsoleCredentials.Obtain(opts.UserName, opts.PasswordEnvVar, opts.CurrentUser),
                UseSsl = opts.UseSsl,
                SkipCertCheck = opts.SkipCertCheck,
                Port = opts.Port,
                Auth = opts.Auth,
                Timeout = TimeSpan.FromMinutes(opts.TimeoutMinutes)
            };
            Func<string, IRemoteSession> connect = opts.Transport == Transport.Wmi
                ? (Func<string, IRemoteSession>)(c => new WmiSession(c, settings))
                : c => new PsRemotingSession(c, settings);
            var ops = new Operations(connect, !opts.SkipPreflight, Preflight.ManagementPort(opts.Transport, opts.UseSsl, opts.Port), opts, props);

            if (opts.ListInstalled)
            {
                var rows = Operations.RunParallel(servers, opts.Throttle, s => ops.Inventory(matchers, s));
                Report.ShowInventory(rows, reportPath);
                var failed = rows.Count(r => r.IsError);
                return failed == 0 ? ExitOk : failed == servers.Count ? ExitNoServerReachable : ExitPartialFailure;
            }
            if (installMode)
            {
                Confirm(opts, $"Install {packages.Count} package(s) on {servers.Count} server(s)?");
                var results = Operations.RunParallel(servers, opts.Throttle, s => ops.Install(packages, s));
                Report.ShowOperationResults(results, reportPath);
                return ExitCodeFor(servers, results);
            }
            Confirm(opts, $"Uninstall {msis.Count} MSI(s) on {servers.Count} server(s)?");
            var uninstallResults = Operations.RunParallel(servers, opts.Throttle, s => ops.Uninstall(msis, s));
            Report.ShowOperationResults(uninstallResults, reportPath);
            return ExitCodeFor(servers, uninstallResults);
        }

        public static int Main(string[] args)
        {
            try
            {
                var opts = CommandLine.Parse(args);
                if (opts.ShowHelp || args.Length == 0)
                {
                    Console.WriteLine(CommandLine.Usage);
                    return ExitOk;
                }
                if (opts.ListErrors != null)
                {
                    switch (opts.ListErrors)
                    {
                        case "json": Console.WriteLine(ErrorCatalog.Json); break;
                        case "markdown":
                        case "md": Console.Write(ErrorCatalog.Markdown()); break;
                        default: Console.Write(ErrorCatalog.ListText()); break;
                    }
                    return ExitOk;
                }
                if (opts.Explain != null)
                {
                    var e = ErrorCatalog.TryFind(opts.Explain);
                    if (e == null)
                    {
                        Report.Error($"Unknown error code '{opts.Explain}'. Use --errors to list all codes.");
                        return ExitInvalidInput;
                    }
                    Console.WriteLine(ErrorCatalog.ExplainText(e));
                    return ExitOk;
                }
                return Run(opts);
            }
            catch (MruException ex)
            {
                if (ex.Code == "MRU502") Report.Warn($"[{ex.Code}] {ErrorCatalog.Get(ex.Code).Title}: {ex.Detail}");
                else ReportFatal(ex.Code, ex.Detail);
                return ExitCodeForFatal(ex.Code);
            }
            catch (Exception ex)
            {
                var (code, detail) = ErrorClassifier.Classify("MRU999", ex);
                ReportFatal(code, detail);
                return code == "MRU999" ? ExitInternal : ExitCodeForFatal(code);
            }
        }
    }
}
