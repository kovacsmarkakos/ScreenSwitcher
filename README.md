# ScreenSwitcher

A lightweight, background utility for Windows 11 that allows you to switch between display modes using global keyboard shortcuts. It's designed to be faster and more direct than the default Windows display switcher.

## Features

- **Global Hotkeys**: Switch displays instantly from anywhere.
- **Background Operation**: Runs silently in the background without any visible windows.
- **Low Footprint**: Extremely lightweight C# .NET application.
- **Auto-Startup**: Automatically registers to start with Windows.

## Hotkeys

| Hotkey | Action |
| :--- | :--- |
| `Shift + Ctrl + 1` | **PC Screen Only**: Enable primary display only. |
| `Shift + Ctrl + 2` | **Second Screen Only**: Enable secondary display only. |

## Installation & Setup

1. **Prerequisites**: Ensure you have [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later installed.
2. **Clone the Repo**:
   ```bash
   git clone https://github.com/yourusername/ScreenSwitcher.git
   cd ScreenSwitcher
   ```
3. **Build the Project**:
   ```bash
   dotnet build -c Release
   ```
4. **Run the Application**:
   - You can run it directly: `dotnet run`
   - Or launch the compiled executable from `bin/Release/net10.0-windows/ScreenSwitcher.exe`.

Once launched, the app will automatically add itself to the Windows Registry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) so it starts every time you log in.

## How It Works

The program uses the Windows API (`User32.dll`) to register global hotkeys. When a hotkey is detected, it invokes the built-in Windows utility `DisplaySwitch.exe` with the corresponding mode argument:
- `1`: PC Screen Only
- `4`: Second Screen Only

## Technical Details

- **Language**: C#
- **Framework**: .NET (Windows Forms - used for the message loop)
- **Deployment**: Single executable background process.