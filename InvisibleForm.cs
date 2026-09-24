using System;
using System.Diagnostics;
using System.Drawing;
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
        private const int MOD_NOREPEAT = 0x4000;
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

        // A TV that is already on answers in a few milliseconds (2-13ms measured), so the first
        // "is it on?" check only needs a short window; anything slower is treated as off and the
        // wait below takes over. While waiting, a new probe starts every TvPollIntervalMs and
        // each one gives up after TvProbeTimeoutMs; they overlap.
        private const int TvQuickCheckMs = 300;
        private const int TvProbeTimeoutMs = 1000;
        private const int TvPollIntervalMs = 250;

        /// <summary>
        /// The switch that is still waiting for the TV, if any. Set from the message loop and
        /// cleared from whichever thread finished the wait, so swap it atomically.
        /// </summary>
        private CancellationTokenSource? _pendingSwitch;

        /// <summary>
        /// Tray icon: a way to open the log and to quit that is not Task Manager.
        /// </summary>
        private NotifyIcon? _trayIcon;

        [DllImport("user32.dll", SetLastError = true)]
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

            // Read the config now rather than on the first hotkey, so the log records what the
            // app is actually running with at every launch.
            AppConfig config = AppConfig.Instance;
            LogArpPin(config);

            // Register hotkeys. MOD_NOREPEAT stops a held-down key from firing again and again.
            // If another app already owns a combination, registration fails and that hotkey is dead
            // for as long as we run, so say so rather than leaving it to look broken.
            var failed = new List<string>();
            if (!RegisterHotKey(this.Handle, HOTKEY_ID_1, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_1))
                failed.Add("Ctrl+Shift+1");
            if (!RegisterHotKey(this.Handle, HOTKEY_ID_2, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_2))
                failed.Add("Ctrl+Shift+2");

            if (failed.Count > 0)
            {
                string keys = string.Join(" and ", failed);
                Logger.Log($"Could not register {keys} (error {Marshal.GetLastWin32Error()}); another app probably owns it.");
                Notify("ScreenSwitcher: hotkey unavailable",
                    $"{keys} is already taken by another app, so it will do nothing until that app releases it and ScreenSwitcher restarts.");
            }
        }

        /// <summary>
        /// Records at launch whether the TV's ARP entry is pinned. Losing the pin (a network reset,
        /// a new adapter) silently brings back the deep-standby failures, so say so up front.
        /// </summary>
        private static void LogArpPin(AppConfig config)
        {
            if (!config.EnableTvWake || config.TvIp == null || config.TvMacAddresses.Count == 0)
                return;

            ArpEntry arp = ArpEntry.Lookup(config.TvIp);
            string mac = config.TvMacAddresses[0];

            if (!arp.IsPermanent)
            {
                Logger.Log($"TV ARP entry for {config.TvIp} is {arp}, not pinned. Wakes from deep standby will be unreliable. " +
                           $"Fix: {ArpEntry.PinCommand(config.TvIp, mac)}");
            }
            else if (arp.Mac != null && !string.Equals(arp.Mac, mac, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"TV ARP entry for {config.TvIp} is pinned to {arp.Mac}, but the configured MAC is {mac}. " +
                           $"Unicast wakes are going to the wrong device. Fix: {ArpEntry.PinCommand(config.TvIp, mac)}");
            }
            else
            {
                Logger.Log($"TV ARP entry for {config.TvIp} is pinned to {arp.Mac}.");
            }
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

                // One clock for the whole attempt: the wake and the wait both run against it, so
                // packets keep going out for as long as we are still listening for an answer.
                var sincePress = Stopwatch.StartNew();
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(config.WakeTimeoutSeconds);

                // Start knocking before asking whether the TV is up, so the quick check below is
                // spent with packets in flight. The wake is sized to outlast the final probe and is
                // cancelled the moment the wait concludes either way; its opening burst always
                // completes, so a TV that turns out to be on already gets exactly the
                // fire-and-forget wake this app started with.
                if (canWake)
                {
                    wake = WakeOnLan.WakeAsync(config.TvMacAddresses, config.TvIp,
                        TimeSpan.FromSeconds(config.WakeTimeoutSeconds) + TimeSpan.FromMilliseconds(TvProbeTimeoutMs),
                        wakeStop.Token);
                }

                (bool present, string detail) = await IsTvOnAsync(config, TvQuickCheckMs, supersede.Token).ConfigureAwait(false);
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
                        string? answeredBy = await WaitForTvAsync(config, deadline, supersede.Token).ConfigureAwait(false);

                        if (supersede.IsCancellationRequested)
                        {
                            Logger.Log("Superseded by a newer hotkey press; not switching.");
                            return;
                        }

                        if (answeredBy == null)
                        {
                            string target = config.TvIp != null ? $"{config.TvIp}" : "the display";
                            Logger.Log($"Not switching: TV did not come up within {config.WakeTimeoutSeconds}s " +
                                       $"(wake sent to {string.Join(", ", config.TvMacAddresses)}, no answer from {target}).");

                            // The one cause the app can see for itself: the unicast wake could not be
                            // addressed. Worth pointing at, since it is the fix that worked.
                            string hint = config.TvIp != null && !ArpEntry.Lookup(config.TvIp).IsPermanent
                                ? " The TV's ARP entry is not pinned, which makes this much more likely; the fix is in debug.log."
                                : " See debug.log.";
                            Notify("ScreenSwitcher: TV did not turn on",
                                $"No answer from {target} within {config.WakeTimeoutSeconds}s after sending the wake packet. " +
                                "Staying on the PC screen." + hint);
                            return;
                        }

                        wakeStop.Cancel();
                        Logger.Log($"TV answered on {answeredBy} {sincePress.Elapsed.TotalSeconds:0.0}s after the first wake packet; " +
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
        private static async Task<(bool On, string Detail)> IsTvOnAsync(AppConfig config, int timeoutMs, CancellationToken cancellationToken)
        {
            if (config.TvIp != null)
            {
                string? answeredBy = await TvProbe.ProbeAsync(config.TvIp, timeoutMs, cancellationToken).ConfigureAwait(false);
                return answeredBy != null
                    ? (true, $"TV {config.TvIp} answered on {answeredBy}")
                    : (false, $"TV {config.TvIp} silent for {timeoutMs}ms");
            }

            bool present = DisplayTargets.IsTvPresent(config.TvDisplayName, out string displays);
            return (present, $"Available displays: {displays}");
        }

        /// <summary>
        /// Waits for the TV to answer, up to <paramref name="deadline"/>. Returns what it answered
        /// on, or null if it never did (or a newer press cancelled the wait).
        ///
        /// A fresh probe starts every <see cref="TvPollIntervalMs"/> without waiting for the last
        /// one to give up. Each probe can hang for up to a second on a silent TV, so running them
        /// back to back meant a TV that woke mid-probe went unnoticed until the next one began;
        /// overlapping them catches it within a quarter of a second.
        /// </summary>
        private static async Task<string?> WaitForTvAsync(AppConfig config, DateTime deadline, CancellationToken cancellationToken)
        {
            if (config.TvIp == null)
            {
                // Display fallback: a cheap synchronous check, so a plain poll is enough.
                while (DateTime.UtcNow < deadline)
                {
                    try { await Task.Delay(TvPollIntervalMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }

                    if (DisplayTargets.IsTvPresent(config.TvDisplayName, out _))
                        return "display";
                }
                return null;
            }

            // Cancelled once we have an answer, so the probes still in flight stop straight away
            // rather than holding up the switch. Deliberately not disposed here: those probes may
            // still be unwinding against its token when this method returns.
            var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var probes = new List<Task<string?>>();
            DateTime nextProbe = DateTime.UtcNow;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now < deadline && now >= nextProbe)
                    {
                        probes.Add(TvProbe.ProbeAsync(config.TvIp, TvProbeTimeoutMs, done.Token));
                        nextProbe = now.AddMilliseconds(TvPollIntervalMs);
                    }

                    for (int i = probes.Count - 1; i >= 0; i--)
                    {
                        if (!probes[i].IsCompleted)
                            continue;
                        string? answer = probes[i].Status == TaskStatus.RanToCompletion ? probes[i].Result : null;
                        if (answer != null)
                            return answer;
                        probes.RemoveAt(i);
                    }

                    // Past the deadline no new probes start; the ones already out still get to
                    // finish, since the TV may be answering one of them right now.
                    if (now >= deadline && probes.Count == 0)
                        return null;

                    var waitOn = new List<Task>(probes);
                    if (now < deadline)
                    {
                        TimeSpan untilNext = (nextProbe < deadline ? nextProbe : deadline) - now;
                        waitOn.Add(Task.Delay(untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero, cancellationToken));
                    }
                    await Task.WhenAny(waitOn).ConfigureAwait(false);
                }
                return null;
            }
            finally
            {
                done.Cancel();
            }
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
            menu.Items.Add("Open debug.log", null, (_, _) => Logger.Open());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Close());

            _trayIcon = new NotifyIcon
            {
                Icon = AppIcon.Load(SystemInformation.SmallIconSize),
                Text = "ScreenSwitcher  (Ctrl+Shift+1: PC, Ctrl+Shift+2: TV)",
                ContextMenuStrip = menu,
                Visible = true
            };
        }

        /// <summary>
        /// Shows the corner notice for as long as the config says. Safe to call from any thread.
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

            NotificationWindow.Show(title, message, AppConfig.Instance.NotificationSeconds);
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
