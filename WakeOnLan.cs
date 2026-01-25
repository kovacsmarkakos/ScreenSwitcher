using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ScreenSwitcher
{
    public static class WakeOnLan
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        private const long MaxLogSize = 256 * 1024; // 256KB

        private static void Log(string message)
        {
            try
            {
                // Ensure log doesn't grow too large
                FileInfo info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogSize)
                {
                    File.Delete(LogPath);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {message}{Environment.NewLine}");
            }
            catch { }
        }

        public static Task WakeAsync(string macAddress)
        {
            return Task.Run(() => Wake(macAddress));
        }

        public static void Wake(string macAddress)
        {
            Log($"Attempting to wake MAC: {macAddress}");

            if (string.IsNullOrWhiteSpace(macAddress) || macAddress == "00:00:00:00:00:00")
            {
                Log("Invalid MAC address.");
                return;
            }

            byte[]? magicPacket = CreateMagicPacket(macAddress);
            if (magicPacket == null)
            {
                Log("Failed to create magic packet.");
                return;
            }

            // Ports to send to
            int[] ports = { 7, 9 };

            // Send burst of packets
            for (int i = 0; i < 5; i++)
            {
                foreach (int port in ports)
                {
                    SendMagicPacket(magicPacket, port, i == 0);
                }
                // Small delay between burst packets
                System.Threading.Thread.Sleep(50);
            }
        }

        private static void SendMagicPacket(byte[] magicPacket, int port, bool shouldLog)
        {
            // Send to 255.255.255.255 (Limited Broadcast)
            try
            {
                using (UdpClient client = new UdpClient())
                {
                    client.EnableBroadcast = true;
                    client.Connect(IPAddress.Broadcast, port);
                    client.Send(magicPacket, magicPacket.Length);
                    if (shouldLog) Log($"Sent packet to 255.255.255.255 on port {port}");
                }
            }
            catch (Exception ex)
            {
                if (shouldLog) Log($"Error sending to generic broadcast port {port}: {ex.Message}");
            }

            // Send to all network interfaces
            foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (networkInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;

                    var ipProperties = networkInterface.GetIPProperties();
                    foreach (var unicastAddress in ipProperties.UnicastAddresses)
                    {
                        if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var broadcastAddress = GetBroadcastAddress(unicastAddress.Address, unicastAddress.IPv4Mask);
                            if (broadcastAddress != null)
                            {
                                using (UdpClient client = new UdpClient())
                                {
                                    client.EnableBroadcast = true;
                                    client.Connect(broadcastAddress, port);
                                    client.Send(magicPacket, magicPacket.Length);
                                    if (shouldLog) Log($"Sent packet via interface {networkInterface.Name} to {broadcastAddress} port {port}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (shouldLog) Log($"Error sending on interface {networkInterface.Name} port {port}: {ex.Message}");
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
