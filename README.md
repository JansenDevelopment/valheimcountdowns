# Playtime Timers (local mod)

A lightweight BepInEx plugin that adds in-game countdown timers you can start/pause anytime.

## Features
- Press `F8` to open/close the timer window.
- Create multiple timers with custom name + duration.
- Start, pause, reset, extend (`+1m`), delete.
- Timers use **server/world clock** when available, so they advance even if others stay on the server.
- Sleeping or other time acceleration does not speed up these timers.
- Optional auto-start for a 4-hour **Royal Jelly (Infested Mine)** timer when you leave an Infested Mine.
- Timer state is saved in BepInEx config and restored on next launch.

## Build
From this folder:

```powershell
.\build.ps1 -ValheimDir "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

If your game is installed elsewhere, pass that path to `-ValheimDir`.

## Install
The build script copies `PlaytimeTimers.dll` into this folder:

`BepInEx\plugins\Martijn-PlaytimeTimers\PlaytimeTimers.dll`

Then launch Valheim with your normal BepInEx profile.

## Notes about map/world timestamp
This version stores world-clock anchored timer state when available.
If world clock API is unavailable, timers fall back to local unscaled real-time.
