using System;
using System.IO;
using System.Text.Json;

namespace ScreenSwitcher
{
    public class AppConfig
    {
        public bool EnableTvWake { get; set; } = false;
        public string TvMacAddress { get; set; } = "00:00:00:00:00:00";

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
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
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch
            {
                // Fallback to default if loading fails
            }
            return new AppConfig();
        }
    }
}
