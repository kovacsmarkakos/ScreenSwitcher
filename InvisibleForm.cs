using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

        private void InvisibleForm_Load(object sender, EventArgs e)
        {
            // Hide the form completely
            this.Hide();
            
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
                        SwitchScreen("1"); // PC Screen Only
                        break;
                    case HOTKEY_ID_2:
                        SwitchScreen("4"); // Second Screen Only
                        break;
                }
            }
        }

        private void SwitchScreen(string mode)
        {
            try
            {
                Process.Start("DisplaySwitch.exe", mode);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error switching screen: {ex.Message}");
            }
        }

        private void SetStartup()
        {
            try
            {
                string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(runKey, true))
                {
                    if (key == null) return;
                    
                    string appName = "ScreenSwitcher";
                    string appPath = Application.ExecutablePath;
                    
                    if (key.GetValue(appName) as string != appPath)
                    {
                        key.SetValue(appName, appPath);
                    }
                }
            }
            catch
            {
                // Ignore errors (e.g. permissions)
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
