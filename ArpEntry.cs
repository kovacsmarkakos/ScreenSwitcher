using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ScreenSwitcher
{
    /// <summary>
    /// What Windows' neighbour (ARP) table holds for the TV's address.
    ///
    /// This decides whether the unicast magic packet leaves the PC at all. Windows can only send
    /// to 192.168.1.x once it knows the MAC behind it; when the TV is in deep standby and no
    /// longer answers ARP, the packet is quietly dropped even though the socket call succeeds.
    /// A Permanent entry (netsh ... store=persistent) removes the need to ask, which is what
    /// makes the unicast wake reliable for this TV.
    /// </summary>
    public readonly struct ArpEntry
    {
        public string State { get; }
        public string? Mac { get; }

        /// <summary>Windows will address a unicast packet to the TV without asking first.</summary>
        public bool CanSendUnicast => State is "Permanent" or "Reachable" or "Stale" or "Delay" or "Probe";

        public bool IsPermanent => State == "Permanent";

        private ArpEntry(string state, string? mac)
        {
            State = state;
            Mac = mac;
        }

        public override string ToString() => Mac == null ? State : $"{State} ({Mac})";

        /// <summary>Exact command that pins the entry; shown in the log when it is missing.</summary>
        public static string PinCommand(IPAddress ip, string mac) =>
            $"netsh interface ipv4 set neighbors interface=\"{AdapterFor(ip) ?? "<adapter>"}\" address={ip} " +
            $"neighbor={mac.Replace(':', '-').ToLowerInvariant()} store=persistent  (run as administrator)";

        /// <summary>Name of the adapter whose subnet contains <paramref name="ip"/>, if any.</summary>
        private static string? AdapterFor(IPAddress ip)
        {
            try
            {
                byte[] target = ip.GetAddressBytes();
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null)
                            continue;

                        byte[] local = unicast.Address.GetAddressBytes();
                        byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                        bool same = true;
                        for (int b = 0; b < 4 && same; b++)
                            same = (local[b] & mask[b]) == (target[b] & mask[b]);
                        if (same)
                            return nic.Name;
                    }
                }
            }
            catch { }
            return null;
        }

        private const ushort AF_INET = 2;

        // MIB_IPNET_ROW2 on x64: SOCKADDR_INET (28) + InterfaceIndex (4) + InterfaceLuid (8)
        // + PhysicalAddress[32] + PhysicalAddressLength (4) + State (4) + Flags (1, padded)
        // + ReachabilityTime (4) = 88 bytes. The table's rows start 8 bytes in.
        private const int RowSize = 88;
        private const int TableHeader = 8;
        private const int OffsetIpv4 = 4;
        private const int OffsetPhysical = 40;
        private const int OffsetPhysicalLength = 72;
        private const int OffsetState = 76;

        private static readonly string[] StateNames =
            { "Unreachable", "Incomplete", "Probe", "Delay", "Stale", "Reachable", "Permanent" };

        [DllImport("iphlpapi.dll")]
        private static extern int GetIpNetTable2(ushort family, out IntPtr table);

        [DllImport("iphlpapi.dll")]
        private static extern void FreeMibTable(IntPtr memory);

        /// <summary>
        /// The best entry Windows has for <paramref name="ip"/> across all adapters, "None" if
        /// there is none, or "Unknown" if the table could not be read.
        /// </summary>
        public static ArpEntry Lookup(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return new ArpEntry("Unknown", null);

            byte[] wanted = ip.GetAddressBytes();
            IntPtr table = IntPtr.Zero;
            try
            {
                if (GetIpNetTable2(AF_INET, out table) != 0 || table == IntPtr.Zero)
                    return new ArpEntry("Unknown", null);

                int count = Marshal.ReadInt32(table);
                ArpEntry best = new ArpEntry("None", null);
                int bestRank = -1;

                for (int i = 0; i < count; i++)
                {
                    IntPtr row = table + TableHeader + i * RowSize;
                    if ((ushort)Marshal.ReadInt16(row) != AF_INET)
                        continue;

                    bool match = true;
                    for (int b = 0; b < 4 && match; b++)
                        match = Marshal.ReadByte(row + OffsetIpv4 + b) == wanted[b];
                    if (!match)
                        continue;

                    int state = Marshal.ReadInt32(row + OffsetState);
                    if (state <= bestRank)
                        continue;

                    int length = Math.Min(Marshal.ReadInt32(row + OffsetPhysicalLength), 32);
                    var mac = new string[length];
                    bool allZero = true;
                    for (int b = 0; b < length; b++)
                    {
                        byte value = Marshal.ReadByte(row + OffsetPhysical + b);
                        allZero &= value == 0;
                        mac[b] = value.ToString("X2");
                    }

                    bestRank = state;
                    best = new ArpEntry(
                        state >= 0 && state < StateNames.Length ? StateNames[state] : $"State{state}",
                        length == 0 || allZero ? null : string.Join(":", mac));
                }
                return best;
            }
            catch
            {
                return new ArpEntry("Unknown", null);
            }
            finally
            {
                if (table != IntPtr.Zero)
                    FreeMibTable(table);
            }
        }
    }
}
