# DMC5 DualSense Layer 1.7.1 — Managed C# fallback

This is the compatibility fallback for PCs where the recommended native C++
build does not start correctly. It implements the same controller behavior but
contains a much larger self-contained managed runtime. Try the Native C++ ZIP
from GitHub Releases first.

The mod adds adaptive triggers, advanced haptics, character lightbar
colors, PlayStation button prompts, and an aligned DualSense controller diagram.

## Features

- adaptive-trigger behavior matching the PS5 version;
- advanced haptics for supported Nero, Dante, and Vergil events;
- ordinary DMC5 combat rumble for every playable character, including V;
- character-specific lightbar colors;
- PlayStation prompts and correctly aligned DualSense controls screens;
- normal Steam Play-button launch with no background console window.

## Requirements

- Windows 10 or 11, x64;
- a legitimate Steam copy of Devil May Cry 5;
- a Sony DualSense connected by USB;
- Steam Input enabled or left at its default setting for Devil May Cry 5;
- PlayStation Accessories, DS4Windows, DualSenseX, and similar controller tools
  closed while playing.

USB is the only fully supported and physically verified connection. Bluetooth
is experimental: normal Steam Input controls should remain available, but
adaptive triggers, lightbar output, and ordinary rumble are not guaranteed.
Advanced haptics cannot work over the current Bluetooth path because Windows
does not expose the required four-channel DualSense audio endpoint.

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models use Steam's common PS5-controller
type and do not require model-specific configuration. DualSense Edge is accepted
by the same output and audio-endpoint detection paths, but its complete hardware
matrix is not yet physically verified. Full feedback support is USB-only.

The haptics endpoint is selected primarily from its Sony USB hardware identity
and four-channel format, not from the visible Windows device name. Localized or
user-renamed speaker endpoints therefore need no manual configuration. The
`AudioDeviceContains` setting remains an optional fallback for unusual drivers.

## Installation

1. Close Devil May Cry 5.
2. Extract the complete release ZIP to a normal writable directory.
3. Run `INSTALL-DMC5-DualSense.cmd`.
4. In Steam, open `Devil May Cry 5 -> Properties -> Controller` and leave Steam
   Input enabled or select `Use default settings`.
5. If an older release left a `DMC5DualSense.Launcher.exe` command in Steam
   Launch Options, remove it once.
6. Connect the controller by USB and use the normal Steam Play button. No launch
   command is required.

Running the installer over an earlier release performs a safe automatic upgrade;
`config.json` and diagnostic logs are preserved.

The package contains an unmodified official REFramework `dinput8.dll`. A
different recognized REFramework build already present in the game directory
is preserved, so its existing plugins remain available. If an unknown
`dinput8.dll` is found, the installer asks whether to replace it. Choose `Y` to
keep an exact backup and continue, or `N` to cancel without changing the
existing DLL. A replaced file is restored exactly on uninstall. The managed fallback requires REFramework
Plugin API 1.15; use replacement mode if an older existing framework rejects
`REFramework.NET.dll`.

REFramework loads the in-game plugin, which starts the hidden Bridge for the
current DMC5 process. The Bridge exits with the game and creates no Windows
startup entry, service, resident controller watcher, or virtual controller. The
bundled Launcher is retained only as a manual compatibility fallback. The
REFramework panel starts hidden and remains available with the `Insert` key for
diagnostics.

## Controller output paths

The session uses two independent output paths:

- Steam Input's DualSense output API for adaptive triggers, lightbar color, and
  ordinary vibration;
- four-channel WASAPI audio for advanced haptics.

Normal buttons, sticks, remapping, and the touchpad remain on DMC5's standard
Steam Input path. The mod neither captures physical input nor creates a virtual
Xbox controller.

Windows often leaves the separate DualSense speaker endpoint at 0% volume. The
bridge remembers that endpoint's volume and mute state, makes it audible only
for the DMC5 session, and restores the original state after the game exits.

## Authentic profile notes

The default `Authentic` profile reproduces the PS5 controller experience without
adding extra synthetic effects. The mod reads the active DMC5 controller layout
directly: Blue Rose and Dante's gun action get adaptive resistance only when
`AttackL` is actually assigned to L2 or R2. Face-button assignments keep both
triggers free, and changing the layout takes effect without restarting the game.

Ordinary sword hits, Blue Rose hold, EX-Act, and MAX-Act use DMC5's standard
combat feedback whenever the game emits it. V uses the purple character light
and ordinary combat rumble; dedicated adaptive-trigger resistance is not part of
his profile.

## Configuration and diagnostics

After installation, settings and logs are located in the game's
`DMC5DualSense` directory. The important files are:

- `config.json` — strengths, feature switches, and audio endpoint settings;
- `launcher.log` — game and bridge startup readiness;
- `bridge.log` — Steam Input output/audio readiness and runtime errors;
- `plugin.log` — RE Engine hooks, live bindings, and runtime errors.

`EnableCalibrationLog` defaults to `false`. Set it to `true` only for a bounded
diagnostic session; it enables five-second counters, per-event traces, reflected
metadata dumps, and the calibration CSV files.

Run `TEST-DualSense.cmd` only when you intentionally want the full hardware
effect test. It is not required for ordinary installation.

## Uninstallation

Run `UNINSTALL-DMC5-DualSense.cmd` from the installed `DMC5DualSense` directory.
The manifest restores pre-existing files and the two original PAK hash pairs.
The game executable, saves, and PAK resource payloads are never replaced.

## Source and release verification

Source code and build tooling are published at
<https://github.com/niczapol/dmc5-dualsense-mod>. Every release contains
`release-manifest.json`; the adjacent `CHECKSUMS.txt` contains the SHA-256 of the
downloadable ZIP. See the repository README for the reproducible build command.
