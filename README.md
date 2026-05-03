# Automation Tool

Lightweight Windows macro recorder/player built with C# WinForms.

## Features

- Records keyboard and button/wheel input with low-level Windows hooks.
- Records mouse movement with configurable polling, defaulting to 200Hz:
  - Light
  - Standard
  - High
- Replays input with `SendInput`.
- Adds small configurable replay jitter:
  - Click coordinate jitter
  - Timing jitter
  - Acceleration jitter on interpolated mouse movement
- Saves and loads macros as JSON.
- Registers global macro shortcuts.
- Emergency stop hotkey: `Ctrl+Alt+Pause`.
- Optional countdown before recording starts.
- Preview overlay that draws recorded movement without sending input:
  - Red: exact recorded path
  - Yellow: noise-adjusted path

## Notes

- This tool is intended for local desktop automation.
- Administrative windows may require running this tool as administrator.
- UAC secure desktop cannot be automated by normal desktop apps.
- Coordinate jitter is applied to click/button targets, not every mouse move.
- Mouse movement is interpolated during replay to avoid jumpy point-to-point playback.
- Click and key event positions are snapped close to their recorded coordinates.
- Coordinate jitter should still be kept low for small buttons and precise UI targets.
- Detailed event rows are not rendered in the UI while recording, to avoid slowing down pointer rendering.

## Build

```powershell
dotnet build
```

## Run

```powershell
dotnet run
```
