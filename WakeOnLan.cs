using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenSwitcher
{
    public static class WakeOnLan
    {
        private static readonly int[] Ports = { 7, 9 };

        // A TV that is already awake answers the very first packet, so open with the same tight
        // burst this has always sent...
        private const int InitialBurstRounds = 5;
        private const int InitialBurstIntervalMs = 50;

        // ...then keep knocking for the rest of the wake window. A TV in standby runs its NIC at
        // very low power and will drop packets; a quarter of a second of coverage is not enough
        // for it to reliably hear us.
        private const int RetryIntervalMs = 500;

        public static Task WakeAsync(IReadOnlyList<string> macAddresses, IPAddress? unicastTarget, TimeSpan duration, CancellationToken cancellationToken)
        {
            return Task.Run(() => Wake(macAddresses, unicastTarget, duration, cancellationToken));
        }

        /// <summary>
        /// Sends magic packets to every configured MAC until <paramref name="duration"/> elapses
        /// or the caller cancels (normally because the TV has come up). The opening burst is
        /// always sent in full whatever happens, so a zero duration or an immediate cancel still
        /// behaves like the plain fire-and-forget wake this started as.
        /// </summary>
        public static void Wake(IReadOnlyList<string> macAddresses, IPAddress? unicastTarget, TimeSpan duration, CancellationToken cancellationToken)
        {
            var packets = new List<KeyValuePair<string, byte[]>>();
            foreach (string mac in macAddresses)
            {
                byte[]? packet = CreateMagicPacket(mac);
                if (packet == null)
                {
                    Logger.Log($"Skipping malformed MAC '{mac}'.");
                    continue;
                }
                packets.Add(new KeyValuePair<string, byte[]>(mac, packet));
            }

            if (packets.Count == 0)
            {
                Logger.Log("No usable MAC address configured; no wake packet sent.");
                return;
            }

            var macList = new List<string>();
            foreach (var entry in packets)
                macList.Add(entry.Key);

            string via = unicastTarget == null ? "broadcast" : $"broadcast + unicast to {unicastTarget}";
            Logger.Log($"Waking {string.Join(", ", macList)} via {via} for up to {duration.TotalSeconds:0.#}s.");

            DateTime deadline = DateTime.UtcNow + duration;
            int round = 0;

            while (true)
            {
                foreach (var entry in packets)
                {
                    foreach (int port in Ports)
                    {
                        // Only the first round is logged; the rest are identical by construction.
                        SendMagicPacket(entry.Value, port, unicastTarget, round == 0);
                    }
                }
                round++;

                // Cancellation and the deadline only count once the opening burst is out.
                bool burstDone = round >= InitialBurstRounds;
                if (burstDone && (cancellationToken.IsCancellationRequested || DateTime.UtcNow >= deadline))
                    break;

                if (!burstDone)
                    Thread.Sleep(InitialBurstIntervalMs);
                else if (!Sleep(RetryIntervalMs, cancellationToken))
                    break;
            }

            string reason = cancellationToken.IsCancellationRequested ? "stopped early" : "window elapsed";
            Logger.Log($"Wake finished after {round} round(s) ({reason}).");
        }

        /// <summary>Interruptible sleep. False once cancellation has been requested.</summary>
        private static bool Sleep(int milliseconds, CancellationToken cancellationToken)
        {
            const int Slice = 50;
            for (int elapsed = 0; elapsed < milliseconds; elapsed += Slice)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;
                Thread.Sleep(Math.Min(Slice, milliseconds - elapsed));
            }
            return !cancellationToken.IsCancellationRequested;
        }

        /// <summary>Rewrites any accepted MAC spelling as upper-case colon-separated form.</summary>
        public static bool TryNormalizeMac(string? macAddress, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(macAddress))
                return false;

            string hex = Regex.Replace(macAddress, "[^0-9A-Fa-f]", "");
            if (hex.Length != 12)
                return false;

            var parts = new string[6];
            for (int i = 0; i < 6; i++)
                parts[i] = hex.Substring(i * 2, 2).ToUpperInvariant();

            normalized = string.Join(":", parts);
            return true;
        }

        private static void SendMagicPacket(byte[] magicPacket, int port, IPAddress? unicastTarget, bool shouldLog)
        {
            // Straight to the TV's own address. On Wi-Fi this is the delivery that actually gets
            // through: the access point holds unicast frames for a dozing client and flags them in
            // the beacon, whereas broadcasts are only flushed at DTIM and are routinely dropped.
            // Needs the TV in the ARP cache; in standby the Wi-Fi chip still answers ARP, so it is.
            if (unicastTarget != null)
            {
                try
                {
                    using (UdpClient client = new UdpClient(unicastTarget.AddressFamily))
                    {
                        client.Connect(unicastTarget, port);
                        client.Send(magicPacket, magicPacket.Length);
                        if (shouldLog) Logger.Log($"Sent packet to {unicastTarget} on port {port}");
                    }
                }
                catch (Exception ex)
                {
                    if (shouldLog) Logger.Log($"Error sending to {unicastTarget} port {port}: {ex.Message}");
                }
            }

            // Send to 255.255.255.255 (Limited Broadcast)
            try
            {
                using (UdpClient client = new UdpClient())
                {
                    client.EnableBroadcast = true;
                    client.Connect(IPAddress.Broadcast, port);
                    client.Send(magicPacket, magicPacket.Length);
                    if (shouldLog) Logger.Log($"Sent packet to 255.255.255.255 on port {port}");
                }
            }
            catch (Exception ex)
            {
                if (shouldLog) Logger.Log($"Error sending to generic broadcast port {port}: {ex.Message}");
            }

            // Send to all network interfaces
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        networkInterface.OperationalStatus != OperationalStatus.Up)
                        continue;

                    var ipProperties = networkInterface.GetIPProperties();
                    foreach (var unicastAddress in ipProperties.UnicastAddresses)
                    {
                        if (unicastAddress.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        var broadcastAddress = GetBroadcastAddress(unicastAddress.Address, unicastAddress.IPv4Mask);
                        if (broadcastAddress == null)
                            continue;

                        // Bind to the address we resolved the broadcast from, so the packet leaves
                        // that adapter rather than wherever the routing table happens to point once
                        // a VPN or virtual switch is up.
                        using (UdpClient client = new UdpClient(new IPEndPoint(unicastAddress.Address, 0)))
                        {
                            client.EnableBroadcast = true;
                            client.Connect(broadcastAddress, port);
                            client.Send(magicPacket, magicPacket.Length);
                            if (shouldLog) Logger.Log($"Sent packet via interface {networkInterface.Name} to {broadcastAddress} port {port}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (shouldLog) Logger.Log($"Error sending on interface {networkInterface.Name} port {port}: {ex.Message}");
                }
            }
        }

        private static IPAddress? GetBroadcastAddress(IPAddress address, IPAddress? mask)
        {
            if (mask == null) return null;

            byte[] ipAdressBytes = address.GetAddressBytes();
            byte[] subnetMaskBytes = mask.GetAddressBytes();

            if (ipAdressBytes.Length != subnetMaskBytes.Length) return null;

            byte[] broadcastAddressBytes = new byte[ipAdressBytes.Length];
            for (int i = 0; i < broadcastAddressBytes.Length; i++)
            {
                broadcastAddressBytes[i] = (byte)(ipAdressBytes[i] | (subnetMaskBytes[i] ^ 255));
            }
            return new IPAddress(broadcastAddressBytes);
        }

        private static byte[]? CreateMagicPacket(string macAddress)
        {
            try
            {
                macAddress = Regex.Replace(macAddress, "[: -]", "");
                if (macAddress.Length != 12) return null;

                byte[] macBytes = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    macBytes[i] = byte.Parse(macAddress.Substring(i * 2, 2), NumberStyles.HexNumber);
                }

                byte[] packet = new byte[102];
                // First 6 bytes are 0xFF
                Array.Fill(packet, (byte)0xFF, 0, 6);

                // Then 16 copies of the MAC address
                for (int i = 1; i <= 16; i++)
                {
                    Buffer.BlockCopy(macBytes, 0, packet, i * 6, 6);
                }
                return packet;
            }
            catch
            {
                return null;
            }
        }
    }
}
