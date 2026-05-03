# Automation Tool

Lightweight Windows macro recorder/player built with C# WinForms.

## Features

- Records keyboard and mouse input with low-level Windows hooks.
- Uses event-driven recording with configurable mouse move thinning:
  - Light
  - Standard
  - High
- Replays input with `SendInput`.
- Adds small configurable replay jitter:
  - Mouse coordinate jitter
  - Timing jitter
- Saves and loads macros as JSON.
- Registers global macro shortcuts.
- Emergency stop hotkey: `Ctrl+Alt+Pause`.

## Notes

- This tool is intended for local desktop automation.
- Administrative windows may require running this tool as administrator.
- UAC secure desktop cannot be automated by normal desktop apps.
- Coordinate jitter should be kept low for small buttons and precise UI targets.

## Build

```powershell
dotnet build
```

## Run

```powershell
dotnet run
```
