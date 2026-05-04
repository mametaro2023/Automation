# Automation Tool

Lightweight Windows macro recorder/player built with C# WinForms.

## Features

- Records keyboard and button/wheel input with low-level Windows hooks.
- Records mouse movement with configurable polling, defaulting to 200Hz:
  - Light
  - Standard
  - High
- Replays input with `SendInput`.
- Builds a playback timeline before replay and sends movement frames from a high-precision loop.
- Supports playback speed changes from slow motion to fast replay.
- Adds small configurable replay jitter:
  - Click coordinate jitter
  - Timing jitter
  - Acceleration jitter on interpolated mouse movement
  - Trajectory jitter that bends movement paths while preserving click/key anchors
- Saves and loads macros as JSON.
- Automatically saves macros under the user's application data folder and loads them on startup.
- Registers global macro shortcuts.
- Emergency stop hotkey: `Ctrl+Alt+Pause`.
- Optional countdown before recording starts.
- Preview overlay that draws recorded movement without sending input:
  - Red: exact recorded path
  - Yellow: one or more noise-adjusted path candidates
  - Thin lines show the full path, and the bold line animates the current preview playback position

## Notes

- This tool is intended for local desktop automation.
- Administrative windows may require running this tool as administrator.
- UAC secure desktop cannot be automated by normal desktop apps.
- Coordinate jitter is applied to click/button targets, not every mouse move.
- Mouse movement is interpolated during replay to avoid jumpy point-to-point playback.
- Click and key event positions are snapped close to their recorded coordinates.
- Movement noise is applied per movement segment, not per frame, so the pointer does not shake.
- Trajectory jitter is distance-aware: longer non-click movement gets more visible path variation, while short and drag movements stay restrained.
- Preview can draw multiple noisy trajectory candidates at once.
- Preview event markers are drawn as cross marks, with one readable label per event.
- Basic macro editing supports trimming the beginning and end with timeline sliders and a path preview.
- Deleting a macro requires confirmation.
- Click anchors are capped to small jitter, and key anchors are not coordinate-jittered.
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

## Release Build

```powershell
.\scripts\publish.ps1 -Version 0.1.0
```

See [DISTRIBUTING.md](DISTRIBUTING.md) for GitHub Release steps.
