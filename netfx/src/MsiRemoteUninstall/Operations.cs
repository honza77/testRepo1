using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MsiRemoteUninstall
{
    /// <summary>Per-server list / uninstall / install logic.</summary>
    public sealed class Operations
    {
        private readonly Func<string, IRemoteSession> _connect;
        private readonly bool _preflight;
        private readonly int _port;
        private readonly Options _opts;
        private readonly IReadOnlyCollection<KeyValuePair<string, string>> _props;

        public Operations(Func<string, IRemoteSession> connect, bool preflight, int port, Options opts,
                          IReadOnlyCollection<KeyValuePair<string, string>> props)
        {
            _connect = connect;
            _preflight = preflight;
            _port = port;
            _opts = opts;
            _props = props;
        }

        /// <summary>Runs <paramref name="work"/> for every server, at most <paramref name="throttle"/> at a time; order preserved.</summary>
        public static List<T> RunParallel<T>(IReadOnlyList<string> servers, int throttle, Func<string, List<T>> work)
        {
            var results = new List<T>[servers.Count];
            Parallel.For(0, servers.Count, new ParallelOptions { MaxDegreeOfParallelism = throttle }, i => results[i] = work(servers[i]));
            return results.SelectMany(r => r).ToList();
        }

        /// <summary>Preflight + connect; on failure returns null and sets (code, detail).</summary>
        private IRemoteSession TryConnect(string computer, out (string Code, string Detail) error)
        {
            error = default;
            try
            {
                if (_preflight) Preflight.Check(computer, _port, TimeSpan.FromSeconds(5));
                return _connect(computer);
            }
            catch (Exception ex)
            {
                error = ErrorClassifier.Classify("MRU399", ex);
                return null;
            }
        }

        private static string LogFileName(string folder, string id) =>
            $@"{folder}\{id.Trim('{', '}')}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log";

        /// <summary>Runs msiexec, retrying while another installation is in progress (1618).</summary>
        private static int RunMsiexecWithRetry(IRemoteSession session, string name, string arguments, string logFolder)
        {
            for (var attempt = 1; ; attempt++)
            {
                var code = session.RunMsiexec(arguments, logFolder);
                if (code != 1618 || attempt >= 5) return code;
                Report.Warn($"[{name}] Another installation in progress (MSI1618), retry {attempt}/4 in 30 s...");
                Thread.Sleep(TimeSpan.FromSeconds(30));
            }
        }

        // ------------------------------------------------------------ list

        public List<InventoryRow> Inventory(IReadOnlyList<Func<string, bool>> matchers, string computer)
        {
            Report.Info($"[{computer}] Connecting...");
            var session = TryConnect(computer, out var err);
            if (session == null)
            {
                Report.LogCode(computer, err.Code, err.Detail);
                return new List<InventoryRow> { new InventoryRow { ComputerName = computer, ErrorCode = err.Code, ErrorMessage = err.Detail } };
            }
            using (session)
            {
                try
                {
                    var products = session.GetInstalledProducts().Where(p => Matching.PassesFilters(matchers, p)).ToList();
                    Report.Info($"[{session.ComputerName}] {products.Count} matching MSI product(s).");
                    return products.Select(p => new InventoryRow { ComputerName = session.ComputerName, Product = p }).ToList();
                }
                catch (Exception ex)
                {
                    var (code, detail) = ErrorClassifier.Classify("MRU401", ex);
                    Report.LogCode(computer, code, detail);
                    return new List<InventoryRow> { new InventoryRow { ComputerName = computer, ErrorCode = code, ErrorMessage = detail } };
                }
            }
        }

        // ------------------------------------------------------------ uninstall

        public List<OperationResult> Uninstall(IReadOnlyList<string> msis, string computer)
        {
            var allMsis = string.Join("; ", msis);
            Report.Info($"[{computer}] Connecting...");
            var session = TryConnect(computer, out var err);
            if (session == null)
            {
                Report.LogCode(computer, err.Code, err.Detail);
                return new List<OperationResult> { OperationResult.Create(computer, allMsis, null, OperationStatus.ConnectionFailed, null, err.Code, err.Detail, "") };
            }

            using (session)
            {
                var name = session.ComputerName;
                List<InstalledProduct> installed;
                try
                {
                    installed = session.GetInstalledProducts();
                }
                catch (Exception ex)
                {
                    var (code, detail) = ErrorClassifier.Classify("MRU401", ex);
                    Report.LogCode(name, code, detail);
                    return new List<OperationResult> { OperationResult.Create(name, allMsis, null, OperationStatus.ExecutionError, null, code, detail, "") };
                }

                var results = new List<OperationResult>();
                var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var msi in msis)
                {
                    var found = Matching.FindProducts(installed, msi);
                    if (found.Count == 0)
                    {
                        Report.Log(null, $"[{name}] '{msi}': not installed.");
                        results.Add(OperationResult.Create(name, msi, null, OperationStatus.NotFound, null, "", "No matching MSI product installed.", ""));
                        continue;
                    }
                    foreach (var p in found)
                    {
                        if (!handled.Add(p.ProductCode))
                        {
                            results.Add(OperationResult.Create(name, msi, p, OperationStatus.NotInstalled, null, "", "Already handled by an earlier MSI entry.", ""));
                            continue;
                        }
                        if (_opts.WhatIf)
                        {
                            Report.Log(null, $"[{name}] WhatIf: would uninstall {p.DisplayName} {p.Version} {p.ProductCode}");
                            results.Add(OperationResult.Create(name, msi, p, OperationStatus.WouldUninstall, null, "", "WhatIf: not uninstalled.", ""));
                            continue;
                        }
                        var logFile = LogFileName(_opts.RemoteLogFolder, p.ProductCode);
                        Report.Log(null, $"[{name}] Uninstalling {p.DisplayName} {p.Version} {p.ProductCode} ...");
                        try
                        {
                            var exit = RunMsiexecWithRetry(session, name, Msiexec.Arguments("/x", p.ProductCode, logFile, _props), _opts.RemoteLogFolder);
                            var o = MsiExitCodes.InterpretUninstall(exit);
                            if (MsiExitCodes.IsFailure(o.Status)) Report.LogCode(name, o.ErrorCode, $"{p.DisplayName} - {o.Message}");
                            else Report.Ok($"[{name}] {p.DisplayName}: {o.Message}");
                            results.Add(OperationResult.Create(name, msi, p, o.Status, exit, o.ErrorCode, o.Message, logFile));
                        }
                        catch (Exception ex)
                        {
                            var (code, detail) = ErrorClassifier.Classify("MRU405", ex);
                            Report.LogCode(name, code, $"{p.DisplayName} - {detail}");
                            results.Add(OperationResult.Create(name, msi, p, OperationStatus.ExecutionError, null, code, detail, logFile));
                        }
                    }
                }
                return results;
            }
        }

        // ------------------------------------------------------------ install

        public List<OperationResult> Install(IReadOnlyList<ResolvedPackage> packages, string computer)
        {
            InstalledProduct ProductOf(ResolvedPackage pkg) =>
                pkg.Info == null ? null : new InstalledProduct
                {
                    ProductCode = pkg.Info.ProductCode,
                    DisplayName = pkg.Info.ProductName,
                    Version = pkg.Info.ProductVersion,
                    Publisher = pkg.Info.Manufacturer
                };

            var allPackages = string.Join("; ", packages.Select(p => p.FileName));
            Report.Info($"[{computer}] Connecting...");
            var session = TryConnect(computer, out var err);
            if (session == null)
            {
                Report.LogCode(computer, err.Code, err.Detail);
                return new List<OperationResult> { OperationResult.Create(computer, allPackages, null, OperationStatus.ConnectionFailed, null, err.Code, err.Detail, "") };
            }

            using (session)
            {
                var name = session.ComputerName;
                HashSet<string> installedCodes;
                try
                {
                    installedCodes = new HashSet<string>(session.GetInstalledProducts().Select(p => p.ProductCode), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    var (code, detail) = ErrorClassifier.Classify("MRU401", ex);
                    Report.LogCode(name, code, detail + " - continuing without the already-installed check");
                    installedCodes = new HashSet<string>();
                }

                var results = new List<OperationResult>();
                foreach (var pkg in packages)
                {
                    var product = ProductOf(pkg);
                    var productCode = pkg.Info?.ProductCode ?? "";
                    if (productCode.Length > 0 && installedCodes.Contains(productCode))
                    {
                        Report.Log(null, $"[{name}] {pkg.FileName} is already installed ({productCode}) - skipped.");
                        results.Add(OperationResult.Create(name, pkg.FileName, product, OperationStatus.AlreadyInstalled, null, "", "Already installed - skipped.", ""));
                        continue;
                    }
                    if (_opts.WhatIf)
                    {
                        Report.Log(null, $"[{name}] WhatIf: would install {pkg.FileName}");
                        results.Add(OperationResult.Create(name, pkg.FileName, product, OperationStatus.WouldInstall, null, "", "WhatIf: not installed.", ""));
                        continue;
                    }

                    // 1. stage the package on the server (copy mode)
                    var target = pkg.Path;
                    if (pkg.Copy)
                    {
                        target = $@"{_opts.RemoteStagingFolder}\{pkg.FileName}";
                        Report.Log(null, $"[{name}] Copying {pkg.FileName} to {target} ...");
                        try
                        {
                            session.UploadFile(pkg.Path, target);
                        }
                        catch (Exception ex)
                        {
                            var (code, detail) = ErrorClassifier.Classify("MRU406", ex);
                            Report.LogCode(name, code, $"{pkg.FileName} - {detail}");
                            results.Add(OperationResult.Create(name, pkg.FileName, product, OperationStatus.ExecutionError, null, code, detail, ""));
                            continue;
                        }
                    }

                    // 2. install
                    var logFile = LogFileName(_opts.RemoteLogFolder, "install_" + Path.GetFileNameWithoutExtension(pkg.FileName));
                    Report.Log(null, $"[{name}] Installing {pkg.FileName} ...");
                    OperationResult row;
                    try
                    {
                        var exit = RunMsiexecWithRetry(session, name, Msiexec.Arguments("/i", target, logFile, _props), _opts.RemoteLogFolder);
                        var o = MsiExitCodes.InterpretInstall(exit);
                        if (MsiExitCodes.IsFailure(o.Status)) Report.LogCode(name, o.ErrorCode, $"{pkg.FileName} - {o.Message}");
                        else Report.Ok($"[{name}] {pkg.FileName}: {o.Message}");
                        row = OperationResult.Create(name, pkg.FileName, product, o.Status, exit, o.ErrorCode, o.Message, logFile);
                    }
                    catch (Exception ex)
                    {
                        var (code, detail) = ErrorClassifier.Classify("MRU405", ex);
                        Report.LogCode(name, code, $"{pkg.FileName} - {detail}");
                        row = OperationResult.Create(name, pkg.FileName, product, OperationStatus.ExecutionError, null, code, detail, logFile);
                    }

                    // 3. remove the staged copy
                    if (pkg.Copy && !_opts.KeepStaged)
                    {
                        try
                        {
                            session.DeleteFile(target);
                        }
                        catch (Exception ex)
                        {
                            var (code, detail) = ErrorClassifier.Classify("MRU407", ex);
                            Report.LogCode(name, code, detail);
                        }
                    }
                    results.Add(row);
                }
                return results;
            }
        }
    }
}
