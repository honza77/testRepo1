using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace MsiRemoteUninstall
{
    public sealed class MsiInfo
    {
        public string ProductName { get; set; }
        public string ProductVersion { get; set; }
        public string ProductCode { get; set; }
        public string Manufacturer { get; set; }
    }

    public sealed class ResolvedPackage
    {
        /// <summary>Entry as given by the user.</summary>
        public string Entry { get; set; }
        /// <summary>File name, e.g. HelloWorld.msi.</summary>
        public string FileName { get; set; }
        /// <summary>Copy: file on this computer. Otherwise: path msiexec opens on the server.</summary>
        public string Path { get; set; }
        /// <summary>True when the file must be copied to the server (copy mode or downloaded).</summary>
        public bool Copy { get; set; }
        public MsiInfo Info { get; set; }
        /// <summary>Why Info could not be read (MRU504).</summary>
        public string InfoError { get; set; }
    }

    /// <summary>Reads MSI properties with msi.dll (Windows only).</summary>
    public static class MsiDatabase
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiOpenDatabase(string szDatabasePath, IntPtr szPersist, out IntPtr phDatabase);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiDatabaseOpenView(IntPtr hDatabase, string szQuery, out IntPtr phView);

        [DllImport("msi.dll")]
        private static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

        [DllImport("msi.dll")]
        private static extern uint MsiViewFetch(IntPtr hView, out IntPtr phRecord);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiRecordGetString(IntPtr hRecord, uint iField, StringBuilder szValueBuf, ref uint pcchValueBuf);

        [DllImport("msi.dll")]
        private static extern uint MsiCloseHandle(IntPtr hAny);

        private static string ReadProperty(IntPtr db, string name)
        {
            if (MsiDatabaseOpenView(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view) != 0) return "";
            try
            {
                if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var record) != 0) return "";
                try
                {
                    uint len = 1024;
                    var sb = new StringBuilder((int)len);
                    return MsiRecordGetString(record, 1, sb, ref len) == 0 ? sb.ToString() : "";
                }
                finally { MsiCloseHandle(record); }
            }
            finally { MsiCloseHandle(view); }
        }

        /// <summary>Returns the info, or null with the reason in <paramref name="error"/>.</summary>
        public static MsiInfo TryRead(string path, out string error)
        {
            error = null;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                error = "msi.dll is only available on Windows.";
                return null;
            }
            try
            {
                var rc = MsiOpenDatabase(path, IntPtr.Zero /* MSIDBOPEN_READONLY */, out var db);
                if (rc != 0)
                {
                    error = $"MsiOpenDatabase returned {rc} (not a valid MSI or not readable).";
                    return null;
                }
                try
                {
                    return new MsiInfo
                    {
                        ProductName = ReadProperty(db, "ProductName"),
                        ProductVersion = ReadProperty(db, "ProductVersion"),
                        ProductCode = ReadProperty(db, "ProductCode").ToUpperInvariant(),
                        Manufacturer = ReadProperty(db, "Manufacturer")
                    };
                }
                finally { MsiCloseHandle(db); }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>Resolves --install entries against a package source (folder, UNC share or URL).</summary>
    public static class Packages
    {
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() =>
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // older .NET Framework defaults
            return new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        });

        public static bool IsUrl(string s) =>
            s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        public static bool IsUnc(string s) => s.StartsWith(@"\\");

        private static bool IsDrivePath(string s) => s.Length > 2 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/');

        private static bool IsAbsolute(string s) => IsUnc(s) || IsDrivePath(s) || System.IO.Path.IsPathRooted(s);

        private static bool HasWildcard(string s) => s.IndexOfAny(new[] { '*', '?' }) >= 0;

        private static string WithMsiExtension(string name) =>
            HasWildcard(name) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? name : name + ".msi";

        private static string Download(string url, string downloadDir)
        {
            var segment = new Uri(url).Segments.LastOrDefault() ?? "package.msi";
            var fileName = Uri.UnescapeDataString(segment).Trim('/');
            if (fileName.Length == 0) fileName = "package.msi";
            var target = System.IO.Path.Combine(downloadDir, fileName);
            try
            {
                Directory.CreateDirectory(downloadDir);
                using (var response = Http.Value.GetAsync(url).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new MruException("MRU110", $"{url} returned HTTP 404 (not found).");
                    if (!response.IsSuccessStatusCode)
                        throw new MruException("MRU111", $"{url} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                }
                return target;
            }
            catch (MruException) { throw; }
            catch (Exception ex)
            {
                var inner = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
                var detail = inner.InnerException != null ? inner.Message + " " + inner.InnerException.Message : inner.Message;
                throw new MruException("MRU111", $"Download of {url} failed: {TextUtil.FirstLine(detail)}");
            }
        }

        private static List<string> FindInFolder(string fullPath)
        {
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            var pattern = System.IO.Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            try
            {
                if (!Directory.Exists(dir))
                    throw new MruException("MRU111", $"Folder '{dir}' does not exist or is not accessible.");
                if (HasWildcard(pattern))
                {
                    var files = Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                    if (files.Count == 0) throw new MruException("MRU110", $"No file matches '{pattern}' in {dir}.");
                    return files;
                }
                if (File.Exists(fullPath)) return new List<string> { fullPath };
                if (File.Exists(fullPath + ".msi")) return new List<string> { fullPath + ".msi" };
                throw new MruException("MRU110", $"'{pattern}' not found in {dir}.");
            }
            catch (MruException) { throw; }
            catch (Exception ex)
            {
                throw new MruException("MRU111", $"{dir}: {TextUtil.FirstLine(ex.Message)}");
            }
        }

        /// <summary>
        /// Resolves entries: absolute paths and URLs as they are, relative names in <paramref name="source"/>
        /// (default: current directory); URLs are downloaded; Direct mode passes server paths unchanged.
        /// </summary>
        public static List<ResolvedPackage> Resolve(string source, SourceMode mode, string downloadDir, IEnumerable<string> entries)
        {
            source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
            var sourceIsUrl = source != null && IsUrl(source);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ResolvedPackage>();

            foreach (var raw in entries)
            {
                var entry = raw.Trim();
                var found = new List<(string Path, bool Readable, bool Copy)>();

                if (IsUrl(entry) || (sourceIsUrl && !IsAbsolute(entry)))
                {
                    if (HasWildcard(entry))
                        throw new MruException("MRU110", $"Wildcards are not supported for URL sources ('{entry}').");
                    var url = IsUrl(entry) ? entry : source.TrimEnd('/') + "/" + Uri.EscapeDataString(WithMsiExtension(entry));
                    found.Add((Download(url, downloadDir), true, true));
                }
                else
                {
                    var full = IsAbsolute(entry) ? entry : System.IO.Path.Combine(source ?? Environment.CurrentDirectory, entry);
                    if (mode == SourceMode.Direct && !IsUnc(full))
                    {
                        if (HasWildcard(full))
                            throw new MruException("MRU110", $"Wildcards need a folder this computer can read; '{full}' is not a UNC path.");
                        found.Add((WithMsiExtension(full), false, false));
                    }
                    else
                    {
                        found.AddRange(FindInFolder(full).Select(p => (IsUnc(p) ? p : System.IO.Path.GetFullPath(p), true, mode == SourceMode.Copy)));
                    }
                }

                foreach (var (path, readable, copy) in found)
                {
                    if (!seen.Add(path)) continue;
                    string infoError = null;
                    var info = readable ? MsiDatabase.TryRead(path, out infoError) : null;
                    if (!readable) infoError = "Direct mode: the package is opened by the server, not read here.";
                    result.Add(new ResolvedPackage
                    {
                        Entry = entry,
                        FileName = System.IO.Path.GetFileName(path.Replace('\\', System.IO.Path.DirectorySeparatorChar)),
                        Path = path,
                        Copy = copy,
                        Info = info,
                        InfoError = info == null ? infoError : null
                    });
                }
            }
            return result;
        }
    }
}
