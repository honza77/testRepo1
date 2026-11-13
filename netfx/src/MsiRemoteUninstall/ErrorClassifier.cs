using System;
using System.Management;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MsiRemoteUninstall
{
    /// <summary>Maps exceptions from .NET, Windows PowerShell remoting and WMI to catalog codes.</summary>
    public static class ErrorClassifier
    {
        private static readonly Regex CodePrefix = new Regex(@"^(MRU\d{3}):\s*(.*)$", RegexOptions.Singleline);

        private static bool Has(string s, string sub) => s != null && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Returns (code, detail); <paramref name="fallback"/> when nothing more specific is known.</summary>
        public static (string Code, string Detail) Classify(string fallback, Exception ex)
        {
            var detail = TextUtil.FirstLine(ex.Message);
            switch (ex)
            {
                case MruException m:
                    return (m.Code, m.Detail);
                case AggregateException a when a.InnerException != null:
                    return Classify(fallback, a.InnerException);
                case TargetInvocationException t when t.InnerException != null:
                    return Classify(fallback, t.InnerException);
                case PSRemotingTransportException t:
                    return (ErrorRules.ClassifyWinRm(t.ErrorCode, t.Message), detail);
                case PlatformNotSupportedException _:
                    return ("MRU503", detail);
                case UnauthorizedAccessException _:
                    return ("MRU303", detail);
                case ManagementException m:
                    switch (m.ErrorCode)
                    {
                        case ManagementStatus.AccessDenied: return ("MRU303", detail);
                        case ManagementStatus.InvalidNamespace:
                        case ManagementStatus.InvalidClass:
                        case ManagementStatus.ProviderLoadFailure:
                        case ManagementStatus.ProviderFailure:
                        case ManagementStatus.InvalidQuery:
                            return ("MRU309", detail);
                        default: return (fallback, detail);
                    }
                case COMException c:
                    return (ErrorRules.ClassifyHResult(unchecked((uint)c.HResult)) ?? fallback, detail);
                case TimeoutException _:
                    return ("MRU310", detail);
                case System.Net.Sockets.SocketException _:
                    return ("MRU302", detail);
                case RuntimeException r:
                    // remote script errors thrown as "MRU###: detail"
                    var match = CodePrefix.Match(detail);
                    if (match.Success) return (match.Groups[1].Value, match.Groups[2].Value);
                    return (Has(r.Message, "WSMan client library") ? "MRU503" : fallback, detail);
                default:
                    var hr = ErrorRules.ClassifyHResult(unchecked((uint)ex.HResult));
                    return (hr ?? fallback, detail);
            }
        }
    }
}
