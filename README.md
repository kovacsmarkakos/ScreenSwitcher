# ScreenSwitcher

**ScreenSwitcher** is a lightweight background utility for Windows 11 designed for users with multi-monitor setups, specifically those using a secondary Smart TV (like an LG OLED). It removes the friction of manual display management and TV power controls by combining them into instant, global keyboard shortcuts.

---

## 🚀 Key Features

- **Global Hotkeys**: Switch display modes instantly from anywhere in Windows without opening menus.
- **Smart TV Integration**: Sends a "Magic Packet" (Wake-on-LAN) to power on your TV when switching to the secondary screen.
- **Verified Switching**: Rather than firing the display switch at the same moment as the wake packet, it waits until Windows reports the TV as actually connected, then switches. A TV needs several seconds to power on and negotiate HDMI; switching into that gap is what makes a switch silently do nothing.
- **Sustained Wake**: Opens with a tight burst and then keeps re-sending for the whole wake window, because a TV in standby runs its network interface at very low power and drops packets.
- **Dual MAC Support**: LG TVs use a different MAC for wired and Wi-Fi and only wake on the one they last connected with. List both and the wake keeps working after the TV changes network.
- **Silent Background Operation**: Runs as a hidden process with no taskbar clutter.
- **Auto-Startup**: Registers with Windows to start when you log in (Release builds only, and only when enabled in config).

---

## ⌨️ Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **`Shift + Ctrl + 1`** | **PC Screen Only**: Instantly enables your primary monitor. |
| **`Shift + Ctrl + 2`** | **Second Screen Only**: Powers on your Smart TV (WoL), waits for it, then switches display to it. |

Pressing either hotkey cancels a switch that is still waiting on the TV, so `Shift + Ctrl + 1` always gets you back to your monitor immediately.

---

## ⚙️ Configuration

The program reads `config.json` **from the folder containing `ScreenSwitcher.exe`**. To get started:

1. Copy `config.json.template` to `config.json` in the build output folder.
2. Edit it with your TV's details.

```json
{
  "EnableTvWake": true,
  "TvMacAddresses": [ "00:00:00:00:00:00" ],
  "TvDisplayName": "",
  "WakeTimeoutSeconds": 20,
  "WakeSettleMs": 1500,
  "RegisterStartupEntry": true
}
```

| Key | Meaning |
| :--- | :--- |
| `EnableTvWake` | Toggle the Wake-on-LAN feature. |
| `TvMacAddresses` | Every MAC your TV might answer on. Accepts `00:00:00:00:00:00`, `00-00-00...` or `000000...`. **List both the wired and the Wi-Fi MAC** — LG only wakes on the interface it last used. |
| `TvDisplayName` | The TV's name as Windows reports it, e.g. `LG TV SSCR2`; a substring is enough. When set, the switch waits for exactly this display. Left empty, it waits for any display beyond the one already in use. |
| `WakeTimeoutSeconds` | How long to keep waking and waiting before switching regardless. Clamped to 0–120. |
| `WakeSettleMs` | Grace period after the TV appears, so HDMI can finish negotiating. Clamped to 0–30000. |
| `RegisterStartupEntry` | Whether to add the app to the per-user startup list. Ignored by Debug builds, which never register themselves. |

The older single-MAC form (`"TvMacAddress": "..."`) is still read and merged in, so existing config files keep working.

Every load is written to `debug.log` next to the executable, along with the outcome of each switch — which display Windows could see, how long the TV took to appear, and whether it appeared at all. If the TV does not wake, that log tells you whether the packets went out and whether the TV ever came back.

---

## 🛠️ Setup & Requirements

### 1. Prerequisites
- **.NET 10.0 Runtime** (Windows)
- An **LG OLED** or any Smart TV that supports Wake-on-LAN.

### 2. TV Preparation
- Ensure your TV is connected to the same local network as your PC.
- Enable "Wake-on-LAN" or "Mobile/Network Power On" in your TV settings.
  - *LG TVs*: `Settings > General > Devices > External Devices > TV On With Mobile` -> **ON**.
  - Also enable **Quick Start+**. Without it, some models drop their standby network interface after a while and become unreachable until you turn the TV on by hand.
- Note down **both** MAC addresses from `Settings > Support > TV Information` (wired and Wi-Fi) and put them both in `TvMacAddresses`.

### 3. Deployment
1. Build the project using `dotnet build -c Release`.
2. Copy `config.json.template` to `config.json` in the build output folder.
3. Edit `config.json` with your TV's MAC addresses.
4. Launch `ScreenSwitcher.exe`. It will add itself to your Windows Startup unless `RegisterStartupEntry` is `false`.

> Debug builds deliberately never register themselves for startup, and will remove a startup entry that points at the Debug executable. Otherwise every login starts the build output, which then holds a lock on its own `.exe` and breaks the next build.

---

## 🔍 Technical Implementation

| Component | Detail |
| :--- | :--- |
| **Language** | C# |
| **Framework** | .NET 10 (WinForms for Message Loop) |
| **Display Engine** | Native Windows `DisplaySwitch.exe`, gated on `QueryDisplayConfig` target availability |
| **WoL Logic** | Broadcast UDP on ports 7 and 9 — limited broadcast plus a per-interface directed broadcast, sent from each interface's own address |
| **Configuration** | `System.Text.Json`, cached after first read |
| **Persistence** | Windows Registry (HKCU Run Key) |

---

## 📜 License

This project is open-source and available under the MIT License.
