using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace MsiRemoteUninstall
{
    /// <summary>
    /// DNS resolution + TCP connect to the management port before connecting, so vague remoting
    /// errors become precise ones (MRU301 name not resolved, MRU302 unreachable / port closed).
    /// </summary>
    public static class Preflight
    {
        public static int ManagementPort(Transport transport, bool useSsl, int? port) =>
            transport == Transport.Wmi ? 135 : port ?? (useSsl ? 5986 : 5985);

        private static Exception Inner(Exception ex) =>
            ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;

        public static void Check(string computer, int port, TimeSpan timeout)
        {
            IPAddress[] addresses;
            try
            {
                addresses = Dns.GetHostAddresses(computer);
            }
            catch (Exception ex)
            {
                throw new MruException("MRU301", $"'{computer}': {TextUtil.FirstLine(Inner(ex).Message)}");
            }
            if (addresses.Length == 0) throw new MruException("MRU301", $"'{computer}' resolved to no IP address.");

            var family = addresses[0].AddressFamily;
            var sameFamily = addresses.Where(a => a.AddressFamily == family).ToArray();
            using (var client = new TcpClient(family))
            {
                bool connected;
                try
                {
                    connected = client.ConnectAsync(sameFamily, port).Wait(timeout);
                }
                catch (Exception ex)
                {
                    var e = Inner(ex);
                    string reason;
                    if (e is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                        reason = "connection refused (nothing listening, or blocked by a firewall)";
                    else if (e is SocketException se2 && (se2.SocketErrorCode == SocketError.HostUnreachable || se2.SocketErrorCode == SocketError.NetworkUnreachable))
                        reason = "host / network unreachable";
                    else
                        reason = TextUtil.FirstLine(e.Message);
                    throw new MruException("MRU302", $"TCP port {port} on {computer}: {reason}.");
                }
                if (!connected)
                    throw new MruException("MRU302",
                        $"TCP port {port} on {computer} did not answer within {timeout.TotalSeconds:0} s (server offline or port filtered by a firewall).");
            }
        }
    }
}
