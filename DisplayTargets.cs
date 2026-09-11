using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScreenSwitcher
{
    /// <summary>
    /// Asks Windows which display targets are physically present right now.
    ///
    /// A TV in standby drops its HDMI hot-plug signal, so Windows reports its target as
    /// unavailable until the panel has finished powering on. That is the signal the switch
    /// needs: sending the desktop to a display that has not come up yet is what makes
    /// DisplaySwitch.exe silently do nothing.
    /// </summary>
    public static class DisplayTargets
    {
        private const uint QDC_ALL_PATHS = 0x00000001;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const int ERROR_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public uint targetAvailable; // Win32 BOOL
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // Windows needs a correctly sized mode array even though we never read it. The trailing
        // 48 bytes are a union whose contents are irrelevant here, so Size pads them out.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(
            uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

        /// <summary>
        /// Decides whether the TV looks ready to receive the desktop. With a configured name we
        /// look for that display; without one we settle for "something beyond the single panel
        /// already in use". Returns true when the query itself fails, so a problem with our own
        /// diagnostics never blocks a switch the user asked for.
        /// </summary>
        public static bool IsTvPresent(string tvDisplayName, out string detail)
        {
            List<string>? names = TryGetAvailableTargetNames();
            if (names == null)
            {
                detail = "display query unavailable";
                return true;
            }

            detail = names.Count == 0 ? "none" : string.Join(", ", names);

            if (!string.IsNullOrEmpty(tvDisplayName))
            {
                foreach (string name in names)
                {
                    if (name.IndexOf(tvDisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }

            return names.Count >= 2;
        }

        /// <summary>
        /// Friendly names of every display target that is currently connected and powered.
        /// Null if Windows would not answer.
        /// </summary>
        private static List<string>? TryGetAvailableTargetNames()
        {
            try
            {
                uint pathCount, modeCount;
                if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out pathCount, out modeCount) != ERROR_SUCCESS)
                    return null;

                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                if (QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
                    return null;

                var names = new List<string>();
                for (int i = 0; i < pathCount; i++)
                {
                    DISPLAYCONFIG_PATH_TARGET_INFO target = paths[i].targetInfo;
                    if (target.targetAvailable == 0)
                        continue;

                    string name = GetTargetName(target.adapterId, target.id);
                    if (!names.Contains(name))
                        names.Add(name);
                }
                return names;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read the display configuration: {ex.Message}");
                return null;
            }
        }

        private static string GetTargetName(LUID adapterId, uint targetId)
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = adapterId,
                    id = targetId
                }
            };

            if (DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS &&
                !string.IsNullOrWhiteSpace(request.monitorFriendlyDeviceName))
            {
                return request.monitorFriendlyDeviceName.Trim();
            }

            return $"target {targetId}";
        }
    }
}
