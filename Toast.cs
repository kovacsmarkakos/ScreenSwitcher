using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ScreenSwitcher
{
    /// <summary>
    /// Windows toast notifications that stay in the notification centre until dismissed.
    ///
    /// A NotifyIcon balloon looks the same on screen but Windows treats it as legacy and
    /// drops it the moment it times out, so "the TV did not turn on" would vanish before you
    /// got back to the desk. The real toast API keeps it. For an app that is not packaged
    /// that requires an AppUserModelID registered under HKCU, which is also what gives the
    /// notification its name and icon.
    /// </summary>
    public static class Toast
    {
        private const string Aumid = "ScreenSwitcher";
        private const string DisplayName = "ScreenSwitcher";

        private static readonly string IconPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSwitcher", "ScreenSwitcher.png");

        private static bool _registered;

        /// <summary>Registers the app identity. Cheap and idempotent; called before the first toast.</summary>
        public static void Register()
        {
            if (_registered) return;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
                {
                    key.SetValue("DisplayName", DisplayName);
                    string? icon = EnsureIconFile();
                    if (icon != null)
                        key.SetValue("IconUri", icon);
                }
                _registered = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not register for toast notifications: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows a toast. Returns false if the toast API refused, so the caller can fall back to
        /// a balloon rather than lose the message.
        /// </summary>
        public static bool Show(string title, string message)
        {
            Register();
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(
                    "<toast duration=\"long\">" +
                      "<visual><binding template=\"ToastGeneric\">" +
                        $"<text>{SecurityElement.Escape(title)}</text>" +
                        $"<text>{SecurityElement.Escape(message)}</text>" +
                      "</binding></visual>" +
                    "</toast>");

                var toast = new ToastNotification(xml)
                {
                    // Stays in the notification centre for a day, or until dismissed.
                    ExpirationTime = DateTimeOffset.Now.AddDays(1)
                };
                ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Toast failed ({ex.Message}); falling back to a balloon.");
                return false;
            }
        }

        /// <summary>
        /// The notification centre wants an image file on disk for the icon. Write the embedded
        /// one out once, to a per-user folder that stays writable wherever the exe lives.
        /// </summary>
        private static string? EnsureIconFile()
        {
            try
            {
                if (File.Exists(IconPath))
                    return IconPath;

                using Stream? stream = typeof(Toast).Assembly.GetManifestResourceStream("ScreenSwitcher.ico");
                if (stream == null)
                    return null;

                Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
                using var icon = new Icon(stream, 64, 64);
                using Bitmap bitmap = icon.ToBitmap();
                bitmap.Save(IconPath, ImageFormat.Png);
                return IconPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not write the notification icon: {ex.Message}");
                return null;
            }
        }
    }
}
