using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScreenSwitcher
{
    /// <summary>The embedded monitor icon, at whatever size the caller needs.</summary>
    public static class AppIcon
    {
        /// <summary>
        /// The .ico carries 16 to 256px images, so the exact size comes out crisp instead of
        /// being scaled from the 32px one the exe resource hands back. Falls back to the stock
        /// application icon if the resource is somehow missing.
        /// </summary>
        public static Icon Load(Size size)
        {
            try
            {
                using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("ScreenSwitcher.ico");
                if (stream != null)
                    return new Icon(stream, size);
            }
            catch { }

            return SystemIcons.Application;
        }
    }
}
