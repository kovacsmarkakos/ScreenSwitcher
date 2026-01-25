# ScreenSwitcher

**ScreenSwitcher** is a high-performance, lightweight background utility for Windows 11 designed for users with multi-monitor setups, specifically those using a secondary Smart TV (like an LG OLED). It removes the friction of manual display management and TV power controls by combining them into instant, global keyboard shortcuts.

---

## 🚀 Key Features

- **Global Hotkeys**: Switch display modes instantly from anywhere in Windows without opening menus.
- **Smart TV Integration**: Automatically sends a "Magic Packet" (Wake-on-LAN) to power on your TV when switching to the secondary screen.
- **Asynchronous Execution**: Display switching is triggered immediately; network signals for the TV are handled in the background to ensure zero lag in hotkey responsiveness.
- **Robust Networking**: Uses a burst-packet strategy (sending 5 packets across both ports 7 and 9) across all available network interfaces to ensure your TV wakes up reliably every time.
- **Silent Background Operation**: Runs as a hidden process with no taskbar clutter.
- **Auto-Startup**: Automatically registers with Windows to start when you log in.
- **Self-Maintaining Logs**: Built-in `debug.log` with a strict 256KB rotation cap to keep your filesystem clean.

---

## ⌨️ Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **`Shift + Ctrl + 1`** | **PC Screen Only**: Instantly enables your primary monitor. |
| **`Shift + Ctrl + 2`** | **Second Screen Only**: Powers on your Smart TV (WoL) and switches display to it. |

---

## ⚙️ Configuration

The program is customized via a `config.json` file located in the application directory.

```json
{
  "EnableTvWake": true,
  "TvMacAddress": "3C:F0:83:88:D2:50"
}
```

- `EnableTvWake`: Toggle the Wake-on-LAN feature `true` or `false`.
- `TvMacAddress`: The physical MAC address of your TV. Supports formats like `00:00:00:00:00:00`, `00-00-00...`, or `000000...`.

---

## 🛠️ Setup & Requirements

### 1. Prerequisites
- **.NET 10.0 Runtime** (Windows)
- An **LG OLED** or any Smart TV that supports Wake-on-LAN.

### 2. TV Preparation
- Ensure your TV is connected to the same local network as your PC (Ethernet is recommended for highest reliability).
- Enable "Wake-on-LAN" or "Mobile/Network Power On" in your TV settings.
  - *LG TVs*: `Settings > General > Devices > External Devices > TV On With Mobile` -> **ON**.

### 3. Deployment
1. Build the project using `dotnet build -c Release`.
2. Edit the `config.json` in the build folder with your TV's MAC address.
3. Launch `ScreenSwitcher.exe`. It will automatically add itself to your Windows Startup.

---

## 🔍 Technical Implementation

| Component | Detail |
| :--- | :--- |
| **Language** | C# 13 |
| **Framework** | .NET 10 (WinForms for Message Loop) |
| **Display Engine** | Native Windows `DisplaySwitch.exe` integration |
| **WoL Logic** | Multicast UDP (Interface-aware directed broadcast) |
| **Configuration** | Singleton Caching for zero-latency shortcut processing |
| **Persistence** | Windows Registry (HKCU Run Key) |

---

## 📜 License

This project is open-source and available under the MIT License.