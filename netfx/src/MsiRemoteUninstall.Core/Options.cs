using System;
using System.Collections.Generic;
using System.Linq;

namespace MsiRemoteUninstall
{
    public enum Transport { PsRemoting, Wmi }

    public enum AuthMode { Default, Negotiate, Kerberos, CredSsp, Basic }

    public enum SourceMode
    {
        /// <summary>Copy the package to each server, then install (default).</summary>
        Copy,
        /// <summary>msiexec on the server opens the path itself (UNC or server-local path).</summary>
        Direct
    }

    public sealed class Options
    {
        public List<string> Computers { get; } = new List<string>();
        public List<string> ComputerListFiles { get; } = new List<string>();
        public List<string> Msis { get; } = new List<string>();
        public List<string> MsiListFiles { get; } = new List<string>();
        public bool ListInstalled { get; set; }
        public List<string> InstallPackages { get; } = new List<string>();
        public List<string> InstallListFiles { get; } = new List<string>();
        public string PackageSource { get; set; }
        public SourceMode SourceMode { get; set; } = SourceMode.Copy;
        public List<string> MsiProperties { get; } = new List<string>();
        public string RemoteStagingFolder { get; set; } = @"C:\Windows\Temp\MsiRemoteInstall";
        public bool KeepStaged { get; set; }
        public List<string> Filters { get; } = new List<string>();
        public FilterMode FilterMode { get; set; } = FilterMode.Substring;
        public Transport Transport { get; set; } = Transport.PsRemoting;
        public bool UseSsl { get; set; }
        public bool SkipCertCheck { get; set; }
        public int? Port { get; set; }
        public AuthMode Auth { get; set; } = AuthMode.Default;
        public string UserName { get; set; }
        public string PasswordEnvVar { get; set; }
        public bool CurrentUser { get; set; }
        public bool WhatIf { get; set; }
        public bool Yes { get; set; }
        public int Throttle { get; set; } = 16;
        public string ReportPath { get; set; }
        public string RemoteLogFolder { get; set; } = @"C:\Windows\Temp\MsiUninstallLogs";
        public int TimeoutMinutes { get; set; } = 60;
        public bool SkipPreflight { get; set; }
        public bool ShowHelp { get; set; }
        /// <summary>--errors [text|markdown|json]</summary>
        public string ListErrors { get; set; }
        /// <summary>--explain &lt;code&gt;</summary>
        public string Explain { get; set; }
    }

    public static class CommandLine
    {
        public const string Usage = @"
MsiRemoteUninstall (.NET Framework 4.8.1) - uninstall, list or install MSI products on remote Windows servers.

USAGE
  MsiRemoteUninstall [servers] [msis] [options]                            uninstall
  MsiRemoteUninstall [servers] --list [--filter <pattern>...] [options]    list / search
  MsiRemoteUninstall [servers] --install <package>... [options]            install

SERVERS (at least one; combined and de-duplicated)
  -c, --computer <name>        Server host name. Repeatable; comma-separated allowed.
      --computer-list <file>   Text file, one server per line.

MSIS (uninstall mode; combined and de-duplicated)
  -m, --msi <name|guid>        Display name (wildcards * ? [a-z] allowed) or product code GUID. Repeatable.
      --msi-list <file>        Text file, one MSI name / product code per line.

LIST / SEARCH MODE (nothing is changed)
  -l, --list                   List installed MSI products.
  -f, --filter <pattern>       Search pattern. Repeatable; a product is shown if its name,
                               publisher or product code matches ANY pattern. Case-insensitive.
      --filter-mode <mode>     substring (default) | wildcard | regex

INSTALL MODE (for preparing test servers)
  -i, --install <package>      MSI to install: file name (looked up in --package-source, .msi
                               optional, wildcards like *.msi allowed), full path, UNC path or
                               http(s) URL. Repeatable; installed in the given order.
      --install-list <file>    Text file, one package per line.
  -s, --package-source <src>   Local folder, UNC share (\\server\share\msi) or base URL
                               (https://host/msi/). Default: current directory.
      --source-mode <mode>     copy (default): copy the package to each server, then install.
                               direct: msiexec on the server opens the path itself.
      --remote-staging-folder <p>  Where packages are copied on the server
                               (default C:\Windows\Temp\MsiRemoteInstall).
      --keep-staged            Do not delete the copied package after installing.
  -p, --msi-property <N=V>     Public MSI property for msiexec, e.g. INSTALLFOLDER=D:\Apps. Repeatable.
                               Also applied when uninstalling.
  Products that are already installed (same ProductCode) are skipped (status AlreadyInstalled).

CONNECTION
  -t, --transport <name>       psremoting (default) | wmi
      --ssl                    psremoting only: use HTTPS (WinRM over SSL).
      --skip-cert-check        psremoting + --ssl only: accept self-signed / mismatched certificates.
      --port <n>               psremoting only: override WinRM port.
      --auth <mode>            psremoting only: default | negotiate | kerberos | credssp | basic
      --throttle <n>           Servers processed in parallel (default 16).
      --timeout <minutes>      Max time to wait for one msiexec run (default 60).
      --skip-preflight         Skip the DNS + TCP port check done before connecting.

CREDENTIALS (prompted in the console by default)
  -u, --user <DOMAIN\user>     User name; only the password is then prompted.
      --password-env <VAR>     Read the password from environment variable VAR (no prompt).
      --current-user           Use the current Windows logon (no prompt).

BEHAVIOUR
      --what-if                Connect and report what WOULD be done; change nothing.
  -y, --yes                    Do not ask for confirmation.
      --report <file.csv>      CSV report path.
      --remote-log-folder <p>  msiexec log folder on servers (default C:\Windows\Temp\MsiUninstallLogs).

HELP
  -h, --help                   Show this help.
      --errors [format]        List all error codes. format: text (default) | markdown | json
      --explain <code>         Explain an error code and how to fix it (e.g. MRU303 or 1603).

EXIT CODES
  0  success                      4  aborted at the confirmation question
  1  at least one failure         5  no server could be reached
  2  invalid arguments / input    6  unexpected internal error
  3  credential problem           10 success, but a reboot is required

EXAMPLES
  MsiRemoteUninstall --computer-list servers.txt --msi-list msis.txt
  MsiRemoteUninstall -c SRV01,SRV02 -m ""Contoso HelloWorld*"" --what-if
  MsiRemoteUninstall -c SRV01 --list --filter-mode regex -f ""^Contoso"" -u CONTOSO\admin
  MsiRemoteUninstall -c SRV01 --install ""*.msi"" -s \\fileserver\packages\test --yes
  MsiRemoteUninstall -c SRV01 -i Contoso.HelloWorld-1.0.0 -s http://localhost:8080/msi/
  MsiRemoteUninstall --explain MRU307
";

        private static T ParseEnum<T>(string option, string value, params (string Name, T Value)[] map)
        {
            foreach (var (name, v) in map)
                if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                    return v;
            throw new MruException("MRU101", $"Invalid value '{value}' for {option}. Allowed: {string.Join(", ", map.Select(m => m.Name))}");
        }

        private static int ParseInt(string option, string value, int min, int max)
        {
            if (int.TryParse(value, out var v) && v >= min && v <= max) return v;
            throw new MruException("MRU101", $"Invalid value '{value}' for {option} (expected {min}-{max}).");
        }

        /// <summary>Parses the command line. Throws MruException MRU101 on invalid input.</summary>
        public static Options Parse(string[] args)
        {
            var o = new Options();
            var i = 0;

            string Value(string option)
            {
                if (i + 1 >= args.Length || string.IsNullOrEmpty(args[i + 1]))
                    throw new MruException("MRU101", $"Missing value for {option}.");
                i++;
                return args[i];
            }

            for (; i < args.Length; i++)
            {
                var a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "-h": case "--help": case "-?": case "/?": o.ShowHelp = true; break;
                    case "-c": case "--computer":
                        o.Computers.AddRange(Value(a).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(s => s.Trim()).Where(s => s.Length > 0));
                        break;
                    case "--computer-list": o.ComputerListFiles.Add(Value(a)); break;
                    case "-m": case "--msi": o.Msis.Add(Value(a)); break;
                    case "--msi-list": o.MsiListFiles.Add(Value(a)); break;
                    case "-l": case "--list": o.ListInstalled = true; break;
                    case "-i": case "--install": o.InstallPackages.Add(Value(a)); break;
                    case "--install-list": o.InstallListFiles.Add(Value(a)); break;
                    case "-s": case "--package-source": o.PackageSource = Value(a); break;
                    case "--source-mode":
                        o.SourceMode = ParseEnum(a, Value(a), ("copy", SourceMode.Copy), ("direct", SourceMode.Direct));
                        break;
                    case "-p": case "--msi-property": o.MsiProperties.Add(Value(a)); break;
                    case "--remote-staging-folder": o.RemoteStagingFolder = Value(a).TrimEnd('\\'); break;
                    case "--keep-staged": o.KeepStaged = true; break;
                    case "-f": case "--filter": o.Filters.Add(Value(a)); break;
                    case "--filter-mode":
                        o.FilterMode = ParseEnum(a, Value(a), ("substring", FilterMode.Substring), ("wildcard", FilterMode.Wildcard), ("regex", FilterMode.Regex));
                        break;
                    case "-t": case "--transport":
                        o.Transport = ParseEnum(a, Value(a), ("psremoting", Transport.PsRemoting), ("winrm", Transport.PsRemoting),
                                                ("wmi", Transport.Wmi), ("dcom", Transport.Wmi));
                        break;
                    case "--ssl": o.UseSsl = true; break;
                    case "--skip-cert-check": o.SkipCertCheck = true; break;
                    case "--port": o.Port = ParseInt(a, Value(a), 1, 65535); break;
                    case "--auth":
                        o.Auth = ParseEnum(a, Value(a), ("default", AuthMode.Default), ("negotiate", AuthMode.Negotiate),
                                           ("kerberos", AuthMode.Kerberos), ("credssp", AuthMode.CredSsp), ("basic", AuthMode.Basic));
                        break;
                    case "-u": case "--user": o.UserName = Value(a); break;
                    case "--password-env": o.PasswordEnvVar = Value(a); break;
                    case "--current-user": o.CurrentUser = true; break;
                    case "--what-if": case "--whatif": o.WhatIf = true; break;
                    case "-y": case "--yes": o.Yes = true; break;
                    case "--throttle": o.Throttle = ParseInt(a, Value(a), 1, 256); break;
                    case "--timeout": o.TimeoutMinutes = ParseInt(a, Value(a), 1, 1440); break;
                    case "--skip-preflight": o.SkipPreflight = true; break;
                    case "--report": o.ReportPath = Value(a); break;
                    case "--remote-log-folder": o.RemoteLogFolder = Value(a).TrimEnd('\\'); break;
                    case "--errors":
                        var next = i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : null;
                        if (next == "text" || next == "markdown" || next == "md" || next == "json") { o.ListErrors = next; i++; }
                        else o.ListErrors = "text";
                        break;
                    case "--explain": o.Explain = Value(a); break;
                    default:
                        throw new MruException("MRU101", $"Unknown argument '{a}'. Use --help for usage.");
                }
            }
            return o;
        }
    }
}
