using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenSwitcher
{
    public class InvisibleForm : Form
    {
        // P/Invoke constants
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_SHIFT = 0x0004;
        private const int WM_HOTKEY = 0x0312;
        private const int VK_1 = 0x31;
        private const int VK_2 = 0x32;

        // Hotkey IDs
        private const int HOTKEY_ID_1 = 1;
        private const int HOTKEY_ID_2 = 2;

        // DisplaySwitch.exe modes
        private const string ModePcOnly = "1";
        private const string ModeSecondScreenOnly = "4";

        private const string StartupValueName = "ScreenSwitcher";
        private const string StartupKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        // How long a network probe waits for the TV to answer, and how long to pause between
        // probes while waiting for it to boot. A TV that is on answers in milliseconds; only a
        // silent one costs the full timeout.
        private const int TvProbeTimeoutMs = 1000;
        private const int TvPollIntervalMs = 250;

        /// <summary>
        /// The switch that is still waiting for the TV, if any. Set from the message loop and
        /// cleared from whichever thread finished the wait, so swap it atomically.
        /// </summary>
        private CancellationTokenSource? _pendingSwitch;

        /// <summary>
        /// Tray icon: the vehicle for "the TV did not come on" notifications (Windows shows a
        /// balloon tip as a native toast), and the only way to quit that is not Task Manager.
        /// </summary>
        private NotifyIcon? _trayIcon;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public InvisibleForm()
        {
            // Configure form to be invisible
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.Load += InvisibleForm_Load;

            // Check and set startup
            SetStartup();
        }

        protected override bool ShowWithoutActivation => true;

        private void InvisibleForm_Load(object? sender, EventArgs e)
        {
            // Hide the form completely
            this.Hide();

            CreateTrayIcon();
            Toast.Register();

            // Read the config now rather than on the first hotkey, so the log records what the
            // app is actually running with at every launch.
            _ = AppConfig.Instance;

            // Register hotkeys
            // Shift + Ctrl + 1
            RegisterHotKey(this.Handle, HOTKEY_ID_1, MOD_CONTROL | MOD_SHIFT, VK_1);

            // Shift + Ctrl + 2
            RegisterHotKey(this.Handle, HOTKEY_ID_2, MOD_CONTROL | MOD_SHIFT, VK_2);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID_1:
                        SwitchScreen(ModePcOnly); // PC Screen Only
                        break;
                    case HOTKEY_ID_2:
                        SwitchScreen(ModeSecondScreenOnly); // Second Screen Only
                        break;
                }
            }
        }

        private void SwitchScreen(string mode)
        {
            // A newer press supersedes whatever the last one was still waiting on, so a pending
            // "switch once the TV wakes" cannot drag the desktop back after you have asked for
            // PC only.
            CancellationTokenSource? previous = Interlocked.Exchange(ref _pendingSwitch, null);
            if (previous != null)
            {
                previous.Cancel();
            }

            if (mode != ModeSecondScreenOnly)
            {
                Logger.Log("Hotkey: PC screen only.");
                ApplyDisplayMode(mode);
                return;
            }

            var supersede = new CancellationTokenSource();
            _pendingSwitch = supersede;
            _ = SwitchToSecondScreenAsync(supersede);
        }

        /// <summary>
        /// Wakes the TV and hands it the desktop only once it has actually come up. If it does
        /// not, the desktop stays where it is and a notification says why: switching to a TV
        /// that is off just blanks the monitor and leaves you guessing.
        /// </summary>
        private async Task SwitchToSecondScreenAsync(CancellationTokenSource supersede)
        {
            using var wakeStop = CancellationTokenSource.CreateLinkedTokenSource(supersede.Token);
            Task wake = Task.CompletedTask;

            try
            {
                AppConfig config = AppConfig.Instance;

                // Defaults because the file was unreadable means the wake is off by accident.
                // That is the one failure that looks exactly like a TV refusing to turn on, so
                // refuse to switch and say so instead.
                if (config.LoadError != null)
                {
                    Logger.Log($"Hotkey: second screen only. Not switching: {config.LoadError}.");
                    Notify("ScreenSwitcher: not switching", $"{config.LoadError}. Fix the file and restart ScreenSwitcher.");
                    return;
                }

                bool canWake = config.EnableTvWake && config.TvMacAddresses.Count > 0;

                // Start knocking before asking whether the TV is up: the probe can take a second
                // to conclude "no", and that second is better spent with packets in flight. The
                // burst always completes, so cancelling this when the TV turns out to be on
                // already leaves exactly the fire-and-forget wake this app started with.
                if (canWake)
                {
                    wake = WakeOnLan.WakeAsync(config.TvMacAddresses, config.TvIp,
                        TimeSpan.FromSeconds(config.WakeTimeoutSeconds), wakeStop.Token);
                }

                (bool present, string detail) = await IsTvOnAsync(config, supersede.Token).ConfigureAwait(false);
                Logger.Log($"Hotkey: second screen only. {detail}. TV on: {present}.");

                if (!present)
                {
                    if (!config.EnableTvWake)
                    {
                        // The user turned the wake off, so they are managing the TV themselves.
                        // Nothing to wait for; hand the switch straight to Windows as before.
                        Logger.Log("TV wake is disabled; switching without waiting.");
                    }
                    else if (!canWake)
                    {
                        Logger.Log("Not switching: EnableTvWake is on but no usable MAC address is configured.");
                        Notify("ScreenSwitcher: not switching",
                            "The TV is off and no MAC address is configured, so it cannot be woken. Check TvMacAddresses in config.json.");
                        return;
                    }
                    else if (config.WakeTimeoutSeconds == 0)
                    {
                        Logger.Log("WakeTimeoutSeconds is 0; switching without waiting for the TV.");
                    }
                    else
                    {
                        var stopwatch = Stopwatch.StartNew();
                        present = await WaitForTvAsync(config, supersede.Token).ConfigureAwait(false);

                        if (supersede.IsCancellationRequested)
                        {
                            Logger.Log("Superseded by a newer hotkey press; not switching.");
                            return;
                        }

                        if (!present)
                        {
                            string target = config.TvIp != null ? $"{config.TvIp}" : "the display";
                            Logger.Log($"Not switching: TV did not come up within {config.WakeTimeoutSeconds}s " +
                                       $"(wake sent to {string.Join(", ", config.TvMacAddresses)}, no answer from {target}).");
                            Notify("ScreenSwitcher: TV did not turn on",
                                $"No answer from {target} within {config.WakeTimeoutSeconds}s after sending the wake packet. " +
                                "Staying on the PC screen. See debug.log.");
                            return;
                        }

                        wakeStop.Cancel();
                        Logger.Log($"TV came up after {stopwatch.Elapsed.TotalSeconds:0.0}s; " +
                                   $"letting HDMI settle for {config.WakeSettleMs}ms.");
                        await Task.Delay(config.WakeSettleMs, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                if (supersede.IsCancellationRequested)
                {
                    Logger.Log("Superseded by a newer hotkey press; not switching.");
                    return;
                }

                ApplyDisplayMode(ModeSecondScreenOnly);
            }
            catch (Exception ex)
            {
                Logger.Log($"Switching to the second screen failed: {ex.Message}");
            }
            finally
            {
                // Stop knocking (the opening burst still completes) and let the wake unwind
                // before its token source goes away.
                wakeStop.Cancel();
                try { await wake.ConfigureAwait(false); } catch { }

                Interlocked.CompareExchange(ref _pendingSwitch, null, supersede);
                supersede.Dispose();
            }
        }

        /// <summary>
        /// Whether the TV is awake, and a note for the log saying how we know. Asks the TV over
        /// the network when its IP is configured, since this TV keeps HDMI hot-plug asserted in
        /// standby and so always looks "connected" to Windows; falls back to the display check
        /// otherwise.
        /// </summary>
        private static async Task<(bool On, string Detail)> IsTvOnAsync(AppConfig config, CancellationToken cancellationToken)
        {
            if (config.TvIp != null)
            {
                string? answeredBy = await TvProbe.ProbeAsync(config.TvIp, TvProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
                return answeredBy != null
                    ? (true, $"TV {config.TvIp} answered on {answeredBy}")
                    : (false, $"TV {config.TvIp} silent for {TvProbeTimeoutMs}ms");
            }

            bool present = DisplayTargets.IsTvPresent(config.TvDisplayName, out string displays);
            return (present, $"Available displays: {displays}");
        }

        private static async Task<bool> WaitForTvAsync(AppConfig config, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WakeTimeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await Task.Delay(TvPollIntervalMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                (bool on, _) = await IsTvOnAsync(config, cancellationToken).ConfigureAwait(false);
                if (on)
                    return true;
            }

            return false;
        }

        private void ApplyDisplayMode(string mode)
        {
            try
            {
                using (Process.Start("DisplaySwitch.exe", mode)) { }
                Logger.Log($"Ran DisplaySwitch.exe {mode}.");
            }
            catch (Exception ex)
            {
                Logger.Log($"DisplaySwitch.exe {mode} failed: {ex.Message}");
                Notify("ScreenSwitcher: could not switch display", ex.Message);
            }
        }

        private void CreateTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open debug.log", null, (_, _) => OpenLog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Close());

            // The .ico carries 16 to 256px images; ask for the tray's own size so Windows does
            // not have to downscale the 32px one. Falls back to the stock app icon if the
            // resource is somehow missing.
            Icon icon;
            try
            {
                using Stream? stream = typeof(InvisibleForm).Assembly.GetManifestResourceStream("ScreenSwitcher.ico");
                icon = stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application;
            }
            catch
            {
                icon = SystemIcons.Application;
            }

            _trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "ScreenSwitcher  (Ctrl+Shift+1: PC, Ctrl+Shift+2: TV)",
                ContextMenuStrip = menu,
                Visible = true
            };
        }

        /// <summary>
        /// Shows a Windows toast that stays in the notification centre until dismissed; falls
        /// back to a tray balloon if the toast API is unavailable. Safe to call from any thread.
        /// </summary>
        private void Notify(string title, string message)
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(() => Notify(title, message));
                return;
            }

            if (!Toast.Show(title, message))
                _trayIcon?.ShowBalloonTip(10000, title, message, ToolTipIcon.Warning);
        }

        private static void OpenLog()
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not open the log: {ex.Message}");
            }
        }

        private void SetStartup()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, true))
                {
                    if (key == null) return;

                    string? existing = key.GetValue(StartupValueName) as string;
                    string executablePath = Application.ExecutablePath;

#if DEBUG
                    // A Debug build lives in bin\Debug inside the source tree. Registering it means
                    // every login starts it from there, where it holds a lock on its own .exe and
                    // breaks the next build. Never register, and clear an entry that points here.
                    if (existing != null &&
                        string.Equals(existing.Trim('"'), executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        key.DeleteValue(StartupValueName, false);
                        Logger.Log($"Removed the startup entry pointing at this Debug build ({executablePath}).");
                    }
                    else
                    {
                        Logger.Log("Debug build: leaving the Windows startup entry alone.");
                    }
#else
                    if (!AppConfig.Instance.RegisterStartupEntry)
                    {
                        if (existing != null)
                        {
                            key.DeleteValue(StartupValueName, false);
                            Logger.Log("RegisterStartupEntry is false; removed the startup entry.");
                        }
                        return;
                    }

                    // Quoted: an unquoted Run value containing a space (C:\Program Files\...) makes
                    // Windows try to launch C:\Program.exe instead.
                    string appPath = "\"" + executablePath + "\"";
                    if (existing != appPath)
                    {
                        key.SetValue(StartupValueName, appPath);
                        Logger.Log($"Startup entry set to {appPath}.");
                    }
#endif
                }
            }
            catch (Exception ex)
            {
                // Ignore errors (e.g. permissions)
                Logger.Log($"Could not update the startup entry: {ex.Message}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            UnregisterHotKey(this.Handle, HOTKEY_ID_1);
            UnregisterHotKey(this.Handle, HOTKEY_ID_2);

            // Take the icon down explicitly; otherwise Explorer keeps a ghost of it in the tray
            // until the mouse passes over it.
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            base.OnFormClosing(e);
        }
    }
}
