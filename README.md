# FocusFlow

A Pomodoro-style focus timer for Windows 11. Sits in the system tray, stays out of the way, and helps you work in structured focus and break cycles.

## Features

- Pomodoro-style focus and break cycles (Focus, Short Break, Long Break)
- Always-on-top mini timer window -- drag it anywhere on screen
- Full-screen per-monitor break overlays with a reflection prompt
- System tray menu with Start, Pause, Resume, Skip, and Settings
- Chime notification and balloon toast at each phase change
- "I'm back at my desk" confirmation gate after a break ends
- Autostart with Windows (optional, off by default)
- Built-in presets: Pomodoro (25/5/15), Deep Work (50/10/30), Ultradian (90/20/20)
- "Work 5 more min" break-postpone option: delays the pending break without skipping it or disrupting the long-break cadence

## Requirements

- Windows 10 or 11 (x64)
- .NET 10 Runtime (or SDK for building from source)

## Build and Run

```
dotnet build -c Release
dotnet run -c Release
```

Or build and launch the executable directly:

```
dotnet build -c Release
bin\Release\net10.0-windows\FocusFlow.exe
```

A self-contained publish (no runtime dependency):

```
dotnet publish -c Release -r win-x64 --self-contained true
```

## Author

Jerome Kneip

## License

FocusFlow is free software licensed under the **GNU General Public License v3.0 or later** (GPL-3.0-or-later).

You may redistribute and modify it under the terms of the GPL as published by the Free Software Foundation -- either version 3, or (at your option) any later version.

See the [LICENSE](LICENSE) file for the full license text, or visit <https://www.gnu.org/licenses/gpl-3.0.html>.
