using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenSwitcher
{
    /// <summary>
    /// Asks the TV itself whether it is on, over the network.
    ///
    /// Windows cannot tell: this TV keeps its HDMI hot-plug line asserted in standby, so the
    /// display always looks "connected". The TV's network stack is more honest. In standby the
    /// Wi-Fi chip only answers ARP so it can hear a magic packet; once webOS is up it answers
    /// ping and opens its control port. Either of those is proof the panel is awake.
    /// </summary>
    public static class TvProbe
    {
        // webOS SSAP: 3000 is plain websocket, 3001 is TLS. Older firmware opens both, newer
        // firmware only 3001, so try each.
        private static readonly int[] ControlPorts = { 3000, 3001 };

        /// <summary>
        /// Returns the name of the first signal that answered ("port 3001", "ping"), or null if
        /// the TV stayed silent for <paramref name="timeoutMs"/>.
        /// </summary>
        public static async Task<string?> ProbeAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken)
        {
            var attempts = new List<Task<string?>>();
            foreach (int port in ControlPorts)
                attempts.Add(TryConnectAsync(address, port, timeoutMs, cancellationToken));
            attempts.Add(TryPingAsync(address, timeoutMs));

            // Report the first positive answer; only give up once every probe has come back empty.
            while (attempts.Count > 0)
            {
                Task<string?> finished = await Task.WhenAny(attempts).ConfigureAwait(false);
                attempts.Remove(finished);

                if (finished.Status == TaskStatus.RanToCompletion && finished.Result != null)
                    return finished.Result;
            }
            return null;
        }

        private static async Task<string?> TryConnectAsync(IPAddress address, int port, int timeoutMs, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMs);

                await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return $"port {port}";
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> TryPingAsync(IPAddress address, int timeoutMs)
        {
            try
            {
                using var ping = new Ping();
                PingReply reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
                return reply.Status == IPStatus.Success ? "ping" : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
