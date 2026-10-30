# CCD-ParkingMonitor

A tiny Windows tray app that checks whether the **non-V-Cache CCD is parked** while a game is in the foreground on dual-CCD Ryzen X3D CPUs (e.g. 9950X3D / 7950X3D) using the AMD 3D V-Cache Performance Optimizer driver + Windows Game Mode.

## What it shows

- **PASS** (green) – a game has focus and every logical CPU of the non-V-Cache CCD is parked
- **Waiting** (yellow) – game just got focus; the driver gets 10 s to park
- **FAIL** (red) – game has focus but the CCD is still awake after 10 s (one tray notification per session)
- **Idle** (grey) – no game in focus

The window has a per-thread grid (parked `P` or load %), per-CCD load, the current **Windows effective power mode** (the AMD service parks only in `GameMode`), the foreground exe, and session stats. Optional **Stay on top** toggle (remembered in `HKCU\Software\CcdParkMonitor`).

## How it works

- CCDs are found from the L3 cache topology (`GetLogicalProcessorInformationEx`); the CCD with the bigger L3 is the V-Cache die.
- Parking/load come from one PDH query (`\Processor Information(*)\Parking Status` and `% Processor Time`) sampled once per second.
- Games are the exes Windows lists in `HKCU\System\GameConfigStore\Children` (what Game Bar's *Remember this is a game* writes), plus an optional `games.txt` next to the exe (one path or exe name per line).
- Effective power mode comes from `PowerRegisterForEffectivePowerModeNotifications` (event-driven, no polling).

~30–40 MB RAM, near-zero CPU; it stops repainting while minimized to the tray.

## Log

`ccd_park_log.csv` is written next to the exe, one line per state change plus a summary when the game loses focus, so you can check results after a fullscreen session. It is appended to, never overwritten; delete it to start fresh. Untick **Write log** (in the window or the tray menu) to stop logging; the choice is remembered in `HKCU\Software\CcdParkMonitor`.

## Build

No SDK needed – uses the C# compiler that ships with Windows (.NET Framework 4.x):

```
build.cmd
```

Run `CcdParkMonitor.exe` (or `CcdParkMonitor.exe --tray` to start minimized).
