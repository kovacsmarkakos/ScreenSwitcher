using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenSwitcher
{
    public class AppConfig
    {
        /// <summary>Send a Wake-on-LAN packet before switching to the second screen.</summary>
        public bool EnableTvWake { get; set; } = false;

        /// <summary>
        /// Every MAC the TV might answer on. LG sets a separate MAC for the wired and the Wi-Fi
        /// interface and only wakes on the one it last connected with, so listing both is what
        /// keeps the wake working after the TV changes network.
        /// </summary>
        public List<string> TvMacAddresses { get; set; } = new List<string>();

        /// <summary>Older single-MAC form; still honoured and merged into TvMacAddresses.</summary>
        public string? TvMacAddress { get; set; }

        /// <summary>
        /// The TV's name as Windows reports it, e.g. "LG TV SSCR2" (a substring is enough).
        /// When set, the switch waits for exactly this display. Left empty, it waits for any
        /// display beyond the one already in use.
        /// </summary>
        public string TvDisplayName { get; set; } = "";

        /// <summary>How long to keep waking and waiting for the TV before switching regardless.</summary>
        public int WakeTimeoutSeconds { get; set; } = 20;

        /// <summary>Grace period after the TV appears, so HDMI can finish negotiating.</summary>
        public int WakeSettleMs { get; set; } = 1500;

        /// <summary>Register the app in the per-user startup list. Ignored by Debug builds.</summary>
        public bool RegisterStartupEntry { get; set; } = true;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static AppConfig? _instance;

        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Load();
                return _instance;
            }
        }

        public static void Reload() => _instance = Load();

        private static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Logger.Log($"No config.json beside the executable ({ConfigPath}); using defaults, TV wake is off.");
                    return new AppConfig();
                }

                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), SerializerOptions);
                if (config == null)
                {
                    Logger.Log("config.json is empty; using defaults, TV wake is off.");
                    return new AppConfig();
                }

                config.Normalize();
                Logger.Log($"Loaded config.json: EnableTvWake={config.EnableTvWake}, " +
                           $"MACs=[{string.Join(", ", config.TvMacAddresses)}], " +
                           $"TvDisplayName='{config.TvDisplayName}', " +
                           $"WakeTimeoutSeconds={config.WakeTimeoutSeconds}, WakeSettleMs={config.WakeSettleMs}.");
                return config;
            }
            catch (Exception ex)
            {
                // This used to fall back to defaults in silence, which looks exactly like the TV
                // refusing to wake. Say which one it actually was.
                Logger.Log($"Could not read config.json ({ex.Message}); using defaults, TV wake is off.");
                return new AppConfig();
            }
        }

        private void Normalize()
        {
            var merged = new List<string>();
            AddMac(merged, TvMacAddress);
            if (TvMacAddresses != null)
            {
                foreach (string mac in TvMacAddresses)
                    AddMac(merged, mac);
            }
            TvMacAddresses = merged;

            if (EnableTvWake && merged.Count == 0)
                Logger.Log("EnableTvWake is on but no usable MAC address is configured.");

            TvDisplayName = (TvDisplayName ?? "").Trim();
            WakeTimeoutSeconds = Math.Clamp(WakeTimeoutSeconds, 0, 120);
            WakeSettleMs = Math.Clamp(WakeSettleMs, 0, 30000);
        }

        private static void AddMac(List<string> target, string? mac)
        {
            if (string.IsNullOrWhiteSpace(mac))
                return;

            if (!WakeOnLan.TryNormalizeMac(mac, out string normalized))
            {
                Logger.Log($"Ignoring malformed MAC address '{mac}'.");
                return;
            }

            // The template ships this placeholder; treat it as "not configured" rather than
            // spending the whole wake window on an address that cannot answer.
            if (normalized == "00:00:00:00:00:00")
                return;

            if (!target.Contains(normalized))
                target.Add(normalized);
        }
    }
}
