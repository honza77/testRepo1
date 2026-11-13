using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace MsiRemoteUninstall
{
    /// <summary>
    /// Transport 2: WMI over DCOM (System.Management) - no PowerShell or WinRM needed on the server.
    ///  * Inventory: StdRegProv (root\default) reads the registry Uninstall keys.
    ///  * msiexec: Win32_Process.Create runs
    ///      cmd /v:on /c "start /wait msiexec &lt;args&gt; &amp; reg add HKLM\...\&lt;id&gt; /d !errorlevel!"
    ///    because Win32_Process.Create cannot return a child's exit code; the value is read back
    ///    with StdRegProv once the process has ended, then deleted.
    ///  * File upload (install mode): through the admin share \\server\C$ (SMB, port 445);
    ///    explicit credentials are applied with WNetAddConnection2.
    /// </summary>
    public sealed class WmiSession : IRemoteSession
    {
        private const uint Hklm = 0x80000002;
        private const string StatusKey = @"SOFTWARE\MsiRemoteUninstall\ExitCodes";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class NetResource
        {
            public int Scope;
            public int ResourceType;
            public int DisplayType;
            public int Usage;
            public string LocalName;
            public string RemoteName;
            public string Comment;
            public string Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);

        private readonly string _computer;
        private readonly ConnectionSettings _settings;
        private readonly ManagementScope _cimv2;
        private readonly ManagementClass _regProv;
        private readonly ManagementClass _processClass;
        private readonly List<string> _smbConnections = new List<string>();

        public WmiSession(string computer, ConnectionSettings settings)
        {
            _computer = computer;
            _settings = settings;
            var options = new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromMinutes(2)
            };
            // WMI refuses explicit credentials for local connections
            if (settings.Credential != null && !IsLocal(computer))
            {
                options.Username = settings.Credential.UserName;
                options.SecurePassword = settings.Credential.Password;
            }

            _cimv2 = new ManagementScope($@"\\{computer}\root\cimv2", options);
            var defaultNs = new ManagementScope($@"\\{computer}\root\default", options);
            _cimv2.Connect();
            defaultNs.Connect();
            _regProv = new ManagementClass(defaultNs, new ManagementPath("StdRegProv"), null);
            _processClass = new ManagementClass(_cimv2, new ManagementPath("Win32_Process"), null);

            try
            {
                using (var searcher = new ManagementObjectSearcher(_cimv2, new ObjectQuery("SELECT CSName FROM Win32_OperatingSystem")))
                using (var results = searcher.Get())
                    ComputerName = results.Cast<ManagementBaseObject>().Select(o => o["CSName"]?.ToString()).FirstOrDefault() ?? computer;
            }
            catch
            {
                ComputerName = computer;
            }
        }

        public string ComputerName { get; }

        public static bool IsLocal(string computer)
        {
            var c = computer.Trim().ToLowerInvariant();
            return c == "." || c == "localhost" || c == "127.0.0.1" || c == "::1"
                   || string.Equals(c.Split('.')[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>C:\dir\file -> \\computer\C$\dir\file.</summary>
        public static string AdminSharePath(string computer, string remotePath)
        {
            if (remotePath.Length < 3 || remotePath[1] != ':' || remotePath[2] != '\\' || !char.IsLetter(remotePath[0]))
                throw new MruException("MRU406", $"'{remotePath}' is not an absolute local path (like C:\\Folder\\file.msi).");
            return $@"\\{computer}\{char.ToUpperInvariant(remotePath[0])}$\{remotePath.Substring(3)}";
        }

        private static uint U32(object o) => o == null ? uint.MaxValue : Convert.ToUInt32(o);

        private ManagementBaseObject Reg(string method, params (string Name, object Value)[] args)
        {
            using (var inParams = _regProv.GetMethodParameters(method))
            {
                foreach (var (n, v) in args) inParams[n] = v;
                return _regProv.InvokeMethod(method, inParams, null);
            }
        }

        private string[] EnumKeys(string path)
        {
            using (var o = Reg("EnumKey", ("hDefKey", Hklm), ("sSubKeyName", path)))
                return U32(o["ReturnValue"]) == 0 && o["sNames"] is string[] names ? names : new string[0];
        }

        private string GetString(string path, string name)
        {
            using (var o = Reg("GetStringValue", ("hDefKey", Hklm), ("sSubKeyName", path), ("sValueName", name)))
                return U32(o["ReturnValue"]) == 0 && o["sValue"] is string s ? s : "";
        }

        private void DeleteValue(string path, string name)
        {
            using (Reg("DeleteValue", ("hDefKey", Hklm), ("sSubKeyName", path), ("sValueName", name))) { }
        }

        private bool IsProcessRunning(uint pid)
        {
            using (var searcher = new ManagementObjectSearcher(_cimv2, new ObjectQuery($"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {pid}")))
            using (var results = searcher.Get())
                return results.Count > 0;
        }

        public List<InstalledProduct> GetInstalledProducts()
        {
            var list = new List<InstalledProduct>();
            foreach (var (root, arch) in RemoteRegistry.UninstallKeys)
            {
                foreach (var key in EnumKeys(root).Where(ProductCodes.IsProductCode))
                {
                    var path = root + "\\" + key;
                    var name = GetString(path, "DisplayName");
                    if (name.Length == 0) continue;
                    list.Add(new InstalledProduct
                    {
                        ProductCode = key.ToUpperInvariant(),
                        DisplayName = name,
                        Version = GetString(path, "DisplayVersion"),
                        Publisher = GetString(path, "Publisher"),
                        InstallDate = GetString(path, "InstallDate"),
                        Architecture = arch
                    });
                }
            }
            return list.GroupBy(p => p.ProductCode).Select(g => g.First()).ToList();
        }

        public int RunMsiexec(string arguments, string logFolder)
        {
            var valueName = Guid.NewGuid().ToString("N");
            var commandLine =
                "cmd.exe /v:on /c \"" +
                $"(if not exist \"{logFolder}\" mkdir \"{logFolder}\")" +
                $" & start \"\" /wait \"%SystemRoot%\\System32\\msiexec.exe\" {arguments}" +
                $" & reg add \"HKLM\\{StatusKey}\" /v \"{valueName}\" /t REG_SZ /d !errorlevel! /f" +
                "\"";

            uint pid;
            using (var inParams = _processClass.GetMethodParameters("Create"))
            {
                inParams["CommandLine"] = commandLine;
                using (var o = _processClass.InvokeMethod("Create", inParams, null))
                {
                    var rv = U32(o["ReturnValue"]);
                    if (rv != 0)
                    {
                        string reason;
                        switch (rv)
                        {
                            case 2: reason = "access denied"; break;
                            case 3: reason = "insufficient privilege"; break;
                            case 8: reason = "unknown failure"; break;
                            case 9: reason = "path not found"; break;
                            case 21: reason = "invalid parameter"; break;
                            default: reason = "unexpected return value"; break;
                        }
                        throw new MruException("MRU402", $"Win32_Process.Create returned {rv} ({reason}).");
                    }
                    pid = U32(o["ProcessId"]);
                }
            }

            var deadline = DateTime.UtcNow + _settings.Timeout;
            while (IsProcessRunning(pid) && DateTime.UtcNow < deadline)
                Thread.Sleep(TimeSpan.FromSeconds(5));
            if (IsProcessRunning(pid))
                throw new MruException("MRU403",
                    $"msiexec (cmd.exe PID {pid}) still running after {_settings.Timeout.TotalMinutes:0} minute(s); it was left running.");

            var value = GetString(StatusKey, valueName);
            try { DeleteValue(StatusKey, valueName); } catch { /* best effort */ }
            if (int.TryParse(value, out var code)) return code;
            throw new MruException("MRU404", $"No exit code found in HKLM\\{StatusKey}\\{valueName}.");
        }

        private static string PlainText(System.Security.SecureString s)
        {
            var ptr = Marshal.SecureStringToGlobalAllocUnicode(s);
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.ZeroFreeGlobalAllocUnicode(ptr); }
        }

        private void EnsureShare(string uncPath)
        {
            if (_settings.Credential == null || IsLocal(_computer)) return; // current logon is used
            var share = uncPath.Substring(0, uncPath.IndexOf('$') + 1); // \\server\C$
            if (_smbConnections.Contains(share)) return;
            var rc = WNetAddConnection2(new NetResource { ResourceType = 1 /* disk */, RemoteName = share },
                                        PlainText(_settings.Credential.Password), _settings.Credential.UserName, 0);
            if (rc != 0)
                throw new MruException("MRU406", $"Connecting to {share} failed: Windows error {rc} ({new Win32Exception(rc).Message}).");
            _smbConnections.Add(share);
        }

        public void UploadFile(string localPath, string remotePath)
        {
            var target = IsLocal(_computer) ? remotePath : AdminSharePath(_computer, remotePath);
            try
            {
                if (!IsLocal(_computer)) EnsureShare(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(localPath, target, true);
            }
            catch (MruException) { throw; }
            catch (Exception ex)
            {
                throw new MruException("MRU406", $"Copy to {target} failed: {TextUtil.FirstLine(ex.Message)}");
            }
        }

        public void DeleteFile(string remotePath)
        {
            var target = IsLocal(_computer) ? remotePath : AdminSharePath(_computer, remotePath);
            try
            {
                if (File.Exists(target)) File.Delete(target);
            }
            catch (Exception ex)
            {
                throw new MruException("MRU407", $"{target}: {TextUtil.FirstLine(ex.Message)}");
            }
        }

        public void Dispose()
        {
            foreach (var share in _smbConnections) WNetCancelConnection2(share, 0, true);
            _regProv.Dispose();
            _processClass.Dispose();
        }
    }
}
