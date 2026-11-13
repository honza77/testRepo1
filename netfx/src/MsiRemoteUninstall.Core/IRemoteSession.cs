using System;
using System.Collections.Generic;

namespace MsiRemoteUninstall
{
    public sealed class ConnectionSettings
    {
        public Credential Credential { get; set; }
        public bool UseSsl { get; set; }
        public bool SkipCertCheck { get; set; }
        public int? Port { get; set; }
        public AuthMode Auth { get; set; }
        public TimeSpan Timeout { get; set; }
    }

    /// <summary>Session to one server; implemented per remoting technology.</summary>
    public interface IRemoteSession : IDisposable
    {
        /// <summary>Name reported by the server (falls back to the requested host name).</summary>
        string ComputerName { get; }

        /// <summary>MSI products from the 64-bit and 32-bit registry Uninstall keys.</summary>
        List<InstalledProduct> GetInstalledProducts();

        /// <summary>Runs msiexec.exe with the argument string (creating logFolder first); returns its exit code.</summary>
        int RunMsiexec(string arguments, string logFolder);

        /// <summary>Copies a local file to an absolute local path on the server.</summary>
        void UploadFile(string localPath, string remotePath);

        /// <summary>Deletes a file on the server (no error if missing).</summary>
        void DeleteFile(string remotePath);
    }

    public static class RemoteRegistry
    {
        public static readonly (string Path, string Arch)[] UninstallKeys =
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "64-bit"),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "32-bit")
        };
    }
}
