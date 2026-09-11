# ScreenSwitcher

**ScreenSwitcher** is a lightweight background utility for Windows 11 designed for users with multi-monitor setups, specifically those using a secondary Smart TV (like an LG OLED). It removes the friction of manual display management and TV power controls by combining them into instant, global keyboard shortcuts.

---

## 🚀 Key Features

- **Global Hotkeys**: Switch display modes instantly from anywhere in Windows without opening menus.
- **Smart TV Integration**: Sends a "Magic Packet" (Wake-on-LAN) to power on your TV when switching to the secondary screen.
- **Verified Switching**: Rather than firing the display switch at the same moment as the wake packet, it asks the TV whether it is actually up — over the network — and only then switches. If the TV never answers, **it stays on the PC screen and tells you why** with a Windows notification, instead of blanking your monitor for a TV that is off.
- **Sustained Wake**: Opens with a tight burst and then keeps re-sending for the whole wake window, because a TV in standby runs its network interface at very low power and drops packets.
- **Wi‑Fi-aware Delivery**: Sends the magic packet as a unicast to the TV's own address as well as by broadcast. An access point holds unicast frames for a dozing Wi‑Fi client and flags them in the beacon, whereas broadcasts are only flushed at DTIM and are routinely dropped.
- **Silent Background Operation**: Runs hidden, with a single tray icon for opening the log and exiting. Problems are reported as Windows notifications that stay in the notification centre until you dismiss them.
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
  "TvIpAddress": "",
  "TvDisplayName": "",
  "WakeTimeoutSeconds": 20,
  "WakeSettleMs": 1500,
  "RegisterStartupEntry": true
}
```

| Key | Meaning |
| :--- | :--- |
| `EnableTvWake` | Toggle the Wake-on-LAN feature. |
| `TvMacAddresses` | Every MAC your TV might answer on. Accepts `00:00:00:00:00:00`, `00-00-00...` or `000000...`. LG sets separate wired and Wi‑Fi MACs and only wakes on the one it last used, so list whichever apply. |
| `TvIpAddress` | The TV's IP address. **Recommended.** Give it a DHCP reservation on your router so it stays put. With this set, "is the TV on" is asked of the TV itself (webOS control port 3000/3001, or ping), and a unicast magic packet is sent here alongside the broadcasts. |
| `TvDisplayName` | Fallback used only when no `TvIpAddress` is set: the TV's name as Windows reports it, e.g. `LG TV SSCR2` (a substring is enough). The switch then waits for this display to appear. Left empty as well, it waits for any display beyond the one already in use. **Some TVs keep the HDMI link asserted in standby, which makes this signal useless for them** — that is what `TvIpAddress` is for. |
| `WakeTimeoutSeconds` | How long to keep waking and waiting for the TV. If it has not answered by then, the switch is **not** made and a notification explains why. `0` disables the wait and switches immediately. Clamped to 0–120. |
| `WakeSettleMs` | Grace period after the TV answers, so HDMI can finish negotiating. Clamped to 0–30000. |
| `RegisterStartupEntry` | Whether to add the app to the per-user startup list. Ignored by Debug builds, which never register themselves. |

The older single-MAC form (`"TvMacAddress": "..."`) is still read and merged in, so existing config files keep working.

Every load is written to `debug.log` next to the executable, along with the outcome of each switch — whether the TV answered and on what, how long it took to come up, and whether it came up at all. Right-click the tray icon to open it.

### When it refuses to switch

`Shift + Ctrl + 2` will leave you on the PC screen and show a notification saying why in three situations, each also written to the log:

- **The TV did not answer** within `WakeTimeoutSeconds` after the wake packets went out. The most common cause is the TV having dropped off the network in standby — see *Quick Start+* below.
- **`config.json` could not be read.** A typo in the file used to silently turn the wake off, which looks exactly like a TV refusing to turn on. Now it says so. Fix the file and restart.
- **Wake is enabled but no MAC address is configured.**

If you have deliberately set `EnableTvWake` to `false`, none of this applies: the switch is made immediately and the TV is your business.

---

## 🛠️ Setup & Requirements

### 1. Prerequisites
- **.NET 10.0 Runtime** (Windows 10 1809 or later; notifications use the Windows toast API)
- An **LG OLED** or any Smart TV that supports Wake-on-LAN.

### 2. TV Preparation
- Ensure your TV is connected to the same local network as your PC.
- Enable "Wake-on-LAN" or "Mobile/Network Power On" in your TV settings.
  - *LG TVs*: `Settings > General > Devices > External Devices > TV On With Mobile` -> **ON**.
  - Also enable **Quick Start+**. Without it, some models drop their standby network interface after a while and become unreachable until you turn the TV on by hand.
- Note the MAC address of the interface the TV actually uses (wired or Wi‑Fi) from `Settings > Support > TV Information`, and reserve its IP on your router.

### 3. Deployment
1. Build the project using `dotnet build -c Release`.
2. Copy `config.json.template` to `config.json` in the build output folder.
3. Edit `config.json` with your TV's MAC address and IP.
4. Launch `ScreenSwitcher.exe`. It will add itself to your Windows Startup unless `RegisterStartupEntry` is `false`.

> Debug builds deliberately never register themselves for startup, and will remove a startup entry that points at the Debug executable. Otherwise every login starts the build output, which then holds a lock on its own `.exe` and breaks the next build.

---

## 🔍 Technical Implementation

| Component | Detail |
| :--- | :--- |
| **Language** | C# |
| **Framework** | .NET 10 (WinForms for Message Loop) |
| **Display Engine** | Native Windows `DisplaySwitch.exe` |
| **TV Presence** | TCP connect to webOS SSAP (3000/3001) or ICMP ping; `QueryDisplayConfig` target availability as the fallback when no IP is configured |
| **WoL Logic** | UDP magic packet on ports 7 and 9 — unicast to the TV, limited broadcast, and a per-interface directed broadcast sent from each interface's own address |
| **Configuration** | `System.Text.Json`, cached after first read |
| **Persistence** | Windows Registry (HKCU Run Key) |

---

## 📜 License

This project is open-source and available under the MIT License.
