using System;
using System.Diagnostics;
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
        /// Wakes the TV and waits for it to actually come up before handing it the desktop.
        /// Firing DisplaySwitch.exe at the same moment as the magic packet used to leave the
        /// switch racing a panel that needs several seconds to negotiate HDMI.
        /// </summary>
        private async Task SwitchToSecondScreenAsync(CancellationTokenSource supersede)
        {
            using var wakeStop = CancellationTokenSource.CreateLinkedTokenSource(supersede.Token);
            Task wake = Task.CompletedTask;

            try
            {
                AppConfig config = AppConfig.Instance;

                // Start knocking before asking whether the TV is up: the probe can take a second
                // to conclude "no", and that second is better spent with packets in flight. The
                // burst always completes, so cancelling this when the TV turns out to be on
                // already leaves exactly the fire-and-forget wake this app started with.
                if (config.EnableTvWake)
                {
                    wake = WakeOnLan.WakeAsync(config.TvMacAddresses, config.TvIp,
                        TimeSpan.FromSeconds(config.WakeTimeoutSeconds), wakeStop.Token);
                }

                (bool present, string detail) = await IsTvOnAsync(config, supersede.Token).ConfigureAwait(false);
                Logger.Log($"Hotkey: second screen only. {detail}. TV on: {present}.");

                // With the wake disabled there is nothing coming, so waiting would only make the
                // hotkey feel broken. Hand the switch straight to Windows as before.
                if (!present && config.EnableTvWake)
                {
                    var stopwatch = Stopwatch.StartNew();
                    present = await WaitForTvAsync(config, supersede.Token).ConfigureAwait(false);

                    if (supersede.IsCancellationRequested)
                    {
                        Logger.Log("Superseded by a newer hotkey press; not switching.");
                        return;
                    }

                    if (present)
                    {
                        wakeStop.Cancel();
                        Logger.Log($"TV came up after {stopwatch.Elapsed.TotalSeconds:0.0}s; " +
                                   $"letting HDMI settle for {config.WakeSettleMs}ms.");
                        await Task.Delay(config.WakeSettleMs, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        // Switch anyway, which is what the app has always done and what Windows
                        // safely tolerates. The log now says which half of this actually failed.
                        Logger.Log($"TV did not come up within {config.WakeTimeoutSeconds}s; switching anyway.");
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

        private static void ApplyDisplayMode(string mode)
        {
            try
            {
                using (Process.Start("DisplaySwitch.exe", mode)) { }
                Logger.Log($"Ran DisplaySwitch.exe {mode}.");
            }
            catch (Exception ex)
            {
                Logger.Log($"DisplaySwitch.exe {mode} failed: {ex.Message}");
                MessageBox.Show($"Error switching screen: {ex.Message}");
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
            base.OnFormClosing(e);
        }
    }
}
