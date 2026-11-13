using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MsiRemoteUninstall;
using Xunit;

namespace MsiRemoteUninstall.Tests
{
    internal static class Repo
    {
        public static readonly string Root = Find(new DirectoryInfo(AppContext.BaseDirectory));

        private static string Find(DirectoryInfo d)
        {
            if (d == null) throw new InvalidOperationException("Repository root not found");
            return File.Exists(Path.Combine(d.FullName, "ERROR-CODES.md")) ? d.FullName : Find(d.Parent);
        }

        public static string CodeOf(Action a) => Assert.Throws<MruException>(a).Code;
    }

    public class CatalogTests
    {
        [Fact]
        public void Catalog_loads_and_matches_shared_json()
        {
            Assert.True(ErrorCatalog.All.Count > 50);
            var shared = File.ReadAllText(Path.Combine(Repo.Root, "shared", "error-catalog.json"));
            Assert.Equal(JsonDocument.Parse(shared).RootElement.ToString(), JsonDocument.Parse(ErrorCatalog.Json).RootElement.ToString());
        }

        [Fact]
        public void Every_entry_has_explanation_and_remediation()
        {
            foreach (var e in ErrorCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Explanation), e.Code);
                Assert.NotEmpty(e.Remediation);
            }
        }

        [Fact]
        public void Every_code_used_in_the_CSharp_sources_exists()
        {
            var src = Path.Combine(Repo.Root, "netfx", "src");
            var used = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(MRU\\d{3}|MSI\\d{4})[\":]").Select(m => m.Groups[1].Value))
                .Distinct().ToList();
            Assert.NotEmpty(used);
            foreach (var c in used) Assert.True(ErrorCatalog.TryFind(c) != null, c + " is used but not in the catalog");
        }

        [Theory]
        [InlineData("mru303", "MRU303")]
        [InlineData("1603", "MSI1603")]
        [InlineData(" msi3010 ", "MSI3010")]
        public void TryFind_normalises(string input, string expected) => Assert.Equal(expected, ErrorCatalog.TryFind(input)?.Code);

        [Fact]
        public void Unknown_codes()
        {
            Assert.Null(ErrorCatalog.TryFind("MRU000"));
            Assert.Equal("MRU999", ErrorCatalog.Get("nope").Code);
            Assert.Equal("MSI-OTHER", ErrorCatalog.MsiCode(4242));
            Assert.Equal("MSI1603", ErrorCatalog.MsiCode(1603));
        }

        [Fact]
        public void Explain_text_contains_remediation()
        {
            var text = ErrorCatalog.ExplainText(ErrorCatalog.Get("MRU307"));
            Assert.Contains("How to fix it:", text);
            Assert.Contains("  1. ", text);
        }

        [Fact]
        public void MruException_message()
        {
            var ex = new MruException("MRU301", "srv01");
            Assert.Equal("[MRU301] Server name cannot be resolved: srv01", ex.Message);
        }
    }

    public class MatchingTests
    {
        private static readonly InstalledProduct Agent = new InstalledProduct { DisplayName = "Contoso Monitoring Agent", Publisher = "Contoso Ltd", ProductCode = "{11111111-1111-1111-1111-111111111111}" };
        private static readonly InstalledProduct Vcpp = new InstalledProduct { DisplayName = "Microsoft Visual C++ 2019 X64 Minimum Runtime", Publisher = "Microsoft Corporation", ProductCode = "{22222222-2222-2222-2222-222222222222}" };
        private static readonly InstalledProduct Backup = new InstalledProduct { DisplayName = "Fabrikam Backup Client", Publisher = "Fabrikam", ProductCode = "{33333333-3333-3333-3333-333333333333}" };
        private static readonly List<InstalledProduct> Installed = new List<InstalledProduct> { Agent, Vcpp, Backup };

        [Theory]
        [InlineData("*agent*", "Contoso Monitoring Agent", true)]
        [InlineData("agent*", "Contoso Monitoring Agent", false)]
        [InlineData("Microsoft Visual C++ 20?? *", "Microsoft Visual C++ 2019 X64 Minimum Runtime", true)]
        [InlineData("[a-c]ontoso*", "Contoso Monitoring Agent", true)]
        [InlineData("Price (USD)", "Price (USD)", true)]
        [InlineData("a.b", "axb", false)]
        [InlineData("what`?", "what?", true)]
        [InlineData("what`?", "whatx", false)]
        public void Wildcard(string pattern, string text, bool expected) =>
            Assert.Equal(expected, Matching.CreateMatcher(FilterMode.Wildcard, pattern)(text));

        [Theory]
        [InlineData("agent", "Contoso Monitoring Agent", true)]
        [InlineData("*", "Contoso", false)]
        public void Substring(string pattern, string text, bool expected) =>
            Assert.Equal(expected, Matching.CreateMatcher(FilterMode.Substring, pattern)(text));

        [Theory]
        [InlineData("^contoso (agent|monitoring)", "Contoso Monitoring Agent", true)]
        [InlineData("^agent", "Contoso Monitoring Agent", false)]
        public void Regex_(string pattern, string text, bool expected) =>
            Assert.Equal(expected, Matching.CreateMatcher(FilterMode.Regex, pattern)(text));

        [Theory]
        [InlineData(FilterMode.Regex, "(foo")]
        [InlineData(FilterMode.Wildcard, "[abc")]
        [InlineData(FilterMode.Wildcard, "[]")]
        public void Invalid_patterns_throw(FilterMode mode, string pattern) =>
            Assert.ThrowsAny<ArgumentException>(() => Matching.CreateMatcher(mode, pattern));

        [Fact]
        public void Filters_or_semantics()
        {
            var matchers = new List<Func<string, bool>> { Matching.CreateMatcher(FilterMode.Substring, "fabrikam"), Matching.CreateMatcher(FilterMode.Substring, "2222") };
            Assert.Equal(new[] { Vcpp, Backup }, Installed.Where(p => Matching.PassesFilters(matchers, p)));
        }

        [Fact]
        public void FindProducts()
        {
            Assert.Equal(new[] { Backup }, Matching.FindProducts(Installed, "33333333-3333-3333-3333-333333333333"));
            Assert.Equal(new[] { Agent }, Matching.FindProducts(Installed, "contoso monitoring agent"));
            Assert.Empty(Matching.FindProducts(Installed, "Contoso"));
            Assert.Equal(Installed, Matching.FindProducts(Installed, "*"));
        }

        [Fact]
        public void Product_codes()
        {
            Assert.Equal("{12345678-90AB-CDEF-1234-567890ABCDEF}", ProductCodes.TryNormalize("12345678-90ab-cdef-1234-567890abcdef"));
            Assert.Null(ProductCodes.TryNormalize("Contoso"));
        }

        [Fact]
        public void List_files_merge()
        {
            var file = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(file, new[] { "# comment", "", "  SRV02  ", "srv01", "SRV03" });
                Assert.Equal(new[] { "SRV01", "srv02", "SRV03" }, ListFiles.Merge(new[] { "SRV01", " ", "srv02" }, new[] { file }));
            }
            finally { File.Delete(file); }
            Assert.Equal("MRU104", Repo.CodeOf(() => ListFiles.Read("does-not-exist.txt")));
        }

        [Theory]
        [InlineData(0, OperationStatus.Success, "")]
        [InlineData(3010, OperationStatus.SuccessRebootRequired, "MSI3010")]
        [InlineData(1605, OperationStatus.NotInstalled, "MSI1605")]
        [InlineData(1603, OperationStatus.Failed, "MSI1603")]
        [InlineData(4242, OperationStatus.Failed, "MSI-OTHER")]
        public void Uninstall_exit_codes(int code, OperationStatus status, string errorCode)
        {
            var o = MsiExitCodes.InterpretUninstall(code);
            Assert.Equal(status, o.Status);
            Assert.Equal(errorCode, o.ErrorCode);
        }

        [Theory]
        [InlineData(0, OperationStatus.Success, "")]
        [InlineData(1638, OperationStatus.Failed, "MSI1638")]
        [InlineData(1605, OperationStatus.Failed, "MSI1605")]
        public void Install_exit_codes(int code, OperationStatus status, string errorCode)
        {
            var o = MsiExitCodes.InterpretInstall(code);
            Assert.Equal(status, o.Status);
            Assert.Equal(errorCode, o.ErrorCode);
        }
    }

    public class MsiexecTests
    {
        [Fact]
        public void Properties_parse_and_format()
        {
            var p = Msiexec.ParseProperty(false, @"INSTALLFOLDER=D:\Apps\Hello World");
            Assert.Equal("INSTALLFOLDER", p.Key);
            Assert.Equal(@"D:\Apps\Hello World", p.Value);
            Assert.Equal("A=\"1\" B=\"x y\"", Msiexec.FormatProperties(new Dictionary<string, string> { ["A"] = "1", ["B"] = "x y" }));
            Assert.Equal("50%!", Msiexec.ParseProperty(false, "A=50%!").Value);
        }

        [Theory]
        [InlineData(false, "NOVALUE")]
        [InlineData(false, "=x")]
        [InlineData(false, "1BAD=x")]
        [InlineData(false, "A=say \"hi\"")]
        [InlineData(true, "A=50%")]
        [InlineData(true, "A=hi!")]
        public void Invalid_properties(bool strict, string text) =>
            Assert.Equal("MRU112", Repo.CodeOf(() => Msiexec.ParseProperty(strict, text)));

        [Fact]
        public void Arguments()
        {
            Assert.Equal("/i \"C:\\Temp\\a.msi\" /qn /norestart /l*v \"C:\\Logs\\a.log\" X=\"1\"",
                         Msiexec.Arguments("/i", @"C:\Temp\a.msi", @"C:\Logs\a.log", new[] { new KeyValuePair<string, string>("X", "1") }));
            Assert.Equal("/x \"{1}\" /qn /norestart /l*v \"l.log\"", Msiexec.Arguments("/x", "{1}", "l.log", new KeyValuePair<string, string>[0]));
        }
    }

    public sealed class PackageFolder : IDisposable
    {
        public PackageFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mru-cs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            foreach (var f in new[] { "HelloWorld.msi", "Contoso.Agent.msi", "readme.txt" })
                File.WriteAllText(System.IO.Path.Combine(Path, f), "x");
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }

    public class PackageTests
    {
        [Fact]
        public void Resolves_names_extensions_and_wildcards()
        {
            using (var folder = new PackageFolder())
            {
                var r = Packages.Resolve(folder.Path, SourceMode.Copy, "unused", new[] { "HelloWorld", "*.msi" });
                Assert.Equal(new[] { "HelloWorld.msi", "Contoso.Agent.msi" }, r.Select(p => p.FileName));
                Assert.All(r, p => Assert.True(p.Copy));
                Assert.All(r, p => Assert.NotNull(p.InfoError)); // fake files are not valid MSIs
            }
        }

        [Fact]
        public void Missing_packages_and_sources()
        {
            using (var folder = new PackageFolder())
            {
                Assert.Equal("MRU110", Repo.CodeOf(() => Packages.Resolve(folder.Path, SourceMode.Copy, "unused", new[] { "Nope" })));
                Assert.Equal("MRU110", Repo.CodeOf(() => Packages.Resolve(folder.Path, SourceMode.Copy, "unused", new[] { "z*.msi" })));
            }
            Assert.Equal("MRU111", Repo.CodeOf(() => Packages.Resolve("/no/such/folder", SourceMode.Copy, "unused", new[] { "x" })));
        }

        [Fact]
        public void Direct_mode_passes_server_paths()
        {
            var p = Packages.Resolve(null, SourceMode.Direct, "unused", new[] { @"D:\Packages\Hello" }).Single();
            Assert.Equal(@"D:\Packages\Hello.msi", p.Path);
            Assert.False(p.Copy);
            Assert.Equal("MRU110", Repo.CodeOf(() => Packages.Resolve(null, SourceMode.Direct, "unused", new[] { @"D:\Packages\*.msi" })));
        }

        [Fact]
        public void Url_sources()
        {
            Assert.Equal("MRU110", Repo.CodeOf(() => Packages.Resolve("https://repo.example/msi/", SourceMode.Copy, "unused", new[] { "*.msi" })));
            var dir = Path.Combine(Path.GetTempPath(), "mru-cs-dl-" + Guid.NewGuid().ToString("N"));
            Assert.Equal("MRU111", Repo.CodeOf(() => Packages.Resolve("http://127.0.0.1:1/msi", SourceMode.Copy, dir, new[] { "x" })));
        }
    }

    public class CommandLineTests
    {
        [Fact]
        public void Defaults()
        {
            var o = CommandLine.Parse(new[] { "-c", "SRV01", "-m", "App" });
            Assert.Equal(new[] { "SRV01" }, o.Computers);
            Assert.Equal(Transport.PsRemoting, o.Transport);
            Assert.Equal(16, o.Throttle);
            Assert.Equal(SourceMode.Copy, o.SourceMode);
            Assert.Equal(@"C:\Windows\Temp\MsiRemoteInstall", o.RemoteStagingFolder);
        }

        [Fact]
        public void All_options()
        {
            var o = CommandLine.Parse(new[]
            {
                "-c", "A, B", "--computer-list", "s.txt", "--list", "-f", "x", "--filter-mode", "REGEX",
                "-t", "wmi", "--ssl", "--skip-cert-check", "--port", "5999", "--auth", "kerberos",
                "-u", @"CONTOSO\admin", "--password-env", "PW", "--what-if", "-y", "--throttle", "4", "--timeout", "10",
                "--report", "r.csv", "--remote-log-folder", @"D:\Logs\", "--skip-preflight",
                "-i", "HelloWorld", "--install-list", "p.txt", "-s", @"\\fs\msi", "--source-mode", "direct",
                "-p", "A=1", "--remote-staging-folder", @"D:\Stage\", "--keep-staged"
            });
            Assert.Equal(new[] { "A", "B" }, o.Computers);
            Assert.True(o.ListInstalled);
            Assert.Equal(FilterMode.Regex, o.FilterMode);
            Assert.Equal(Transport.Wmi, o.Transport);
            Assert.True(o.UseSsl && o.SkipCertCheck && o.WhatIf && o.Yes && o.SkipPreflight && o.KeepStaged);
            Assert.Equal(5999, o.Port);
            Assert.Equal(AuthMode.Kerberos, o.Auth);
            Assert.Equal(@"D:\Logs", o.RemoteLogFolder);
            Assert.Equal(@"D:\Stage", o.RemoteStagingFolder);
            Assert.Equal(SourceMode.Direct, o.SourceMode);
            Assert.Equal(new[] { "HelloWorld" }, o.InstallPackages);
            Assert.Equal(new[] { "A=1" }, o.MsiProperties);
        }

        [Fact]
        public void Help_options()
        {
            Assert.Equal("text", CommandLine.Parse(new[] { "--errors" }).ListErrors);
            Assert.Equal("json", CommandLine.Parse(new[] { "--errors", "json" }).ListErrors);
            Assert.Equal("MRU303", CommandLine.Parse(new[] { "--explain", "MRU303" }).Explain);
        }

        [Theory]
        [InlineData("--bogus")]
        [InlineData("--port")]
        [InlineData("--port|0")]
        [InlineData("--throttle|abc")]
        [InlineData("--transport|ssh")]
        [InlineData("--source-mode|ftp")]
        public void Invalid_arguments(string args) =>
            Assert.Equal("MRU101", Repo.CodeOf(() => CommandLine.Parse(args.Split('|'))));
    }

    public class ErrorRuleTests
    {
        [Theory]
        [InlineData(5, "Access is denied.", "MRU303")]
        [InlineData(1326, "The user name or password is incorrect.", "MRU304")]
        [InlineData(0, "The SSL certificate ...", "MRU306")]
        [InlineData(-2144108316, "... TrustedHosts ...", "MRU307")]
        [InlineData(-2144108250, "The operation timed out", "MRU310")]
        [InlineData(-2144108526, "The client cannot connect to the destination specified in the request.", "MRU305")]
        [InlineData(0, "Something else", "MRU399")]
        public void WinRm(int code, string msg, string expected) => Assert.Equal(expected, ErrorRules.ClassifyWinRm(code, msg));

        [Theory]
        [InlineData(0x800706BAu, "MRU308")]
        [InlineData(0x8007052Eu, "MRU304")]
        [InlineData(0x80070005u, "MRU303")]
        [InlineData(0x8004100Eu, "MRU309")]
        [InlineData(0x80070721u, "MRU307")]
        public void HResults(uint hr, string expected) => Assert.Equal(expected, ErrorRules.ClassifyHResult(hr));

        [Fact]
        public void Preflight_errors()
        {
            Assert.Equal("MRU301", Repo.CodeOf(() => Preflight.Check("no-such-host.invalid", 5985, TimeSpan.FromSeconds(2))));
            Assert.Equal("MRU302", Repo.CodeOf(() => Preflight.Check("localhost", 1, TimeSpan.FromSeconds(2))));
            Assert.Equal(135, Preflight.ManagementPort(Transport.Wmi, true, 1));
            Assert.Equal(5986, Preflight.ManagementPort(Transport.PsRemoting, true, null));
        }
    }
}
