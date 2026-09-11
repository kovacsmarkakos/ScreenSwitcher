using System;
using System.Globalization;
using System.IO;

namespace ScreenSwitcher
{
    /// <summary>
    /// Append-only log shared by every component, written next to the executable.
    /// </summary>
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        private const long MaxLogSize = 256 * 1024; // 256KB
        private static readonly object Gate = new object();

        public static void Log(string message)
        {
            // Wake packets are sent from a background thread while the message loop logs too,
            // so serialise the writes rather than losing lines to a file-in-use exception.
            lock (Gate)
            {
                try
                {
                    FileInfo info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxLogSize)
                    {
                        File.Delete(LogPath);
                    }

                    File.AppendAllText(LogPath, string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm:ss}: {1}{2}",
                        DateTime.Now, message, Environment.NewLine));
                }
                catch { }
            }
        }
    }
}
