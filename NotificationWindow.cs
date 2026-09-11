using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenSwitcher
{
    /// <summary>
    /// A small notice in the corner of the screen, shown when a switch is refused.
    ///
    /// Replaces the tray balloon, whose on-screen time Windows fixes at a system-wide five
    /// seconds whatever the app asks for. This one stays for as long as the config says, or
    /// until clicked, and never takes focus away from whatever you were doing.
    /// </summary>
    public sealed class NotificationWindow : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // keep out of Alt+Tab
        private const int WS_EX_NOACTIVATE = 0x08000000;   // never take focus

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Design-time sizes at 96 DPI; scaled by DeviceDpi at runtime.
        private const int BaseWidth = 380;
        private const int Padding_ = 14;
        private const int IconSize = 32;
        private const int AccentWidth = 4;
        private const int ScreenMargin = 16;

        private static NotificationWindow? _current;

        private readonly Color _border;
        private readonly Color _accent = Color.FromArgb(0xF7, 0xB5, 0x00);
        private readonly System.Windows.Forms.Timer _lifetime = new System.Windows.Forms.Timer();

        /// <summary>Shows the notice, replacing any that is still up. Must be called on the UI thread.</summary>
        public static void Show(string title, string message, int seconds)
        {
            _current?.Close();
            _current = new NotificationWindow(title, message, seconds);
            _current.Show();
        }

        private NotificationWindow(string title, string message, int seconds)
        {
            bool dark = IsSystemDark();
            Color back    = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xFF, 0xFF, 0xFF);
            Color fore    = dark ? Color.FromArgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x1A, 0x1A);
            Color muted   = dark ? Color.FromArgb(0xD0, 0xD0, 0xD0) : Color.FromArgb(0x40, 0x40, 0x40);
            Color link    = dark ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0x00, 0x67, 0xC0);
            _border       = dark ? Color.FromArgb(0x4A, 0x4A, 0x4A) : Color.FromArgb(0xC8, 0xC8, 0xC8);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = back;
            ForeColor = fore;
            AutoScaleMode = AutoScaleMode.None;
            Text = title;

            float s = DeviceDpi / 96f;
            int pad = (int)(Padding_ * s);
            int icon = (int)(IconSize * s);
            int accent = (int)(AccentWidth * s);
            int width = (int)(BaseWidth * s);
            int textLeft = accent + pad + icon + (int)(12 * s);
            int closeSize = (int)(20 * s);
            int textWidth = width - textLeft - pad - closeSize;

            var titleFont = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold);
            var bodyFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f);

            var iconBox = new PictureBox
            {
                Image = AppIcon.Load(new Size(icon, icon)).ToBitmap(),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Location = new Point(accent + pad, pad),
                Size = new Size(icon, icon)
            };

            var titleLabel = new Label
            {
                Text = title,
                Font = titleFont,
                ForeColor = fore,
                AutoSize = false,
                Location = new Point(textLeft, pad),
                Size = new Size(textWidth, TextRenderer.MeasureText(title, titleFont, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height)
            };

            int bodyTop = titleLabel.Bottom + (int)(4 * s);
            var bodyLabel = new Label
            {
                Text = message,
                Font = bodyFont,
                ForeColor = muted,
                AutoSize = false,
                Location = new Point(textLeft, bodyTop),
                Size = new Size(textWidth, TextRenderer.MeasureText(message, bodyFont, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height)
            };

            var logLink = new LinkLabel
            {
                Text = "Open debug.log",
                Font = bodyFont,
                LinkColor = link,
                ActiveLinkColor = link,
                VisitedLinkColor = link,
                LinkBehavior = LinkBehavior.HoverUnderline,
                AutoSize = true,
                Location = new Point(textLeft, bodyLabel.Bottom + (int)(8 * s))
            };
            logLink.LinkClicked += (_, _) => { Logger.Open(); Close(); };

            var closeLabel = new Label
            {
                Text = "✕",
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9f),
                ForeColor = muted,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(width - pad - closeSize, pad - (int)(4 * s)),
                Size = new Size(closeSize, closeSize),
                Cursor = Cursors.Hand
            };

            Controls.AddRange(new Control[] { iconBox, titleLabel, bodyLabel, logLink, closeLabel });
            Size = new Size(width, Math.Max(iconBox.Bottom, logLink.Bottom) + pad);

            // Anywhere that is not the link dismisses it.
            foreach (Control c in Controls)
                if (c != logLink) c.Click += (_, _) => Close();
            Click += (_, _) => Close();

            _lifetime.Interval = Math.Max(1, seconds) * 1000;
            _lifetime.Tick += (_, _) => Close();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Rounded corners on Windows 11; silently a no-op on older builds.
            int pref = DWMWCP_ROUND;
            try { DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Bottom-right of the screen the desktop is on, clear of the taskbar.
            Rectangle area = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            int margin = (int)(ScreenMargin * DeviceDpi / 96f);
            Location = new Point(area.Right - Width - margin, area.Bottom - Height - margin);

            _lifetime.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.None;

            int accent = (int)(AccentWidth * DeviceDpi / 96f);
            using (var accentBrush = new SolidBrush(_accent))
                e.Graphics.FillRectangle(accentBrush, 0, 0, accent, Height);

            using (var pen = new Pen(_border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _lifetime.Stop();
            _lifetime.Dispose();
            if (ReferenceEquals(_current, this))
                _current = null;
            base.OnFormClosed(e);
        }

        private static bool IsSystemDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
