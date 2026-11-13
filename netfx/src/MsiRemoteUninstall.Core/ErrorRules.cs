using System;

namespace MsiRemoteUninstall
{
    /// <summary>Code-based classification rules (shared with the F# tool and the PowerShell script).</summary>
    public static class ErrorRules
    {
        private static bool Has(string s, string sub) => s != null && s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>WinRM / PowerShell Remoting errors; code = WinRM / Win32 error code.</summary>
        public static string ClassifyWinRm(int code, string msg)
        {
            if (Has(msg, "WSMan client library")) return "MRU503";
            if (code == 5 || Has(msg, "Access is denied")) return "MRU303";
            if (code == 1326 || code == 1327 || code == 1330 || code == 1331 || code == 1909
                || Has(msg, "user name or password is incorrect") || Has(msg, "logon failure")
                || Has(msg, "account is locked") || Has(msg, "account is disabled") || Has(msg, "password has expired"))
                return "MRU304";
            if (code == 12175 || Has(msg, "certificate")) return "MRU306";
            if (code == -2144108316 /* 0x803380E4 TrustedHosts */ || Has(msg, "TrustedHosts") || Has(msg, "Kerberos")
                || Has(msg, "CredSSP") || Has(msg, "authentication"))
                return "MRU307";
            if (code == -2144108250 /* 0x80338126 */ || Has(msg, "timed out") || Has(msg, "timeout")) return "MRU310";
            if (code == -2144108103 || Has(msg, "cannot be resolved") || Has(msg, "could not be resolved")) return "MRU301";
            if (code == -2144108526 /* 0x80338012 */ || Has(msg, "WinRM") || Has(msg, "WS-Management")) return "MRU305";
            return "MRU399";
        }

        /// <summary>COM / DCOM / WMI HRESULTs; null when not specific.</summary>
        public static string ClassifyHResult(uint hr)
        {
            switch (hr)
            {
                case 0x800706BA: // RPC server unavailable
                case 0x800706BE: // remote procedure call failed
                case 0x800706BF:
                case 0x800706D9: // no more endpoints
                    return "MRU308";
                case 0x8007052E: // logon failure
                case 0x80070532: // password expired
                case 0x80070533: // account disabled
                case 0x80070775: // account locked
                    return "MRU304";
                case 0x80070005:
                case 0x80041003: // WBEM_E_ACCESS_DENIED
                    return "MRU303";
                case 0x80070721: // security package error
                case 0x80090322: // wrong principal
                    return "MRU307";
                case 0x8004100E: // invalid namespace
                case 0x80041010: // invalid class
                case 0x80041013: // provider load failure
                case 0x80041014:
                    return "MRU309";
                default:
                    return null;
            }
        }
    }
}
