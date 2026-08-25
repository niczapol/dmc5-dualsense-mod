# DMC5 DualSense Layer 1.5.3

Unofficial native DualSense support for the Windows Steam version of Devil May
Cry 5. The mod adds adaptive triggers, advanced haptics, character lightbar
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
- Steam Input disabled specifically for Devil May Cry 5;
- PlayStation Accessories, DS4Windows, DualSenseX, and similar controller tools
  closed while playing.

Bluetooth cannot carry the four-channel audio stream used for advanced haptics,
so the complete feature set requires USB.

## Installation

1. Close Devil May Cry 5.
2. Extract the complete release ZIP to a normal writable directory.
3. Run `INSTALL-DMC5-DualSense.cmd`.
4. If ViGEmBus is missing, accept the standard Windows UAC prompt. The bundled
   installer is the official, signed Nefarius 1.22.0 release.
5. In Steam, open `Devil May Cry 5 -> Properties -> Controller` and select
   `Disable Steam Input`.
6. Paste the launch command copied by the installer into
   `Properties -> General -> Launch Options`.
7. Connect the controller by USB and use the normal Steam Play button.

Running the installer over an earlier release performs a safe automatic upgrade;
`config.json` and diagnostic logs are preserved.

The bridge starts before DMC5 and exits with it. It creates no Windows startup
entry, opens no console window, and leaves no virtual Xbox controller running
outside the game. The REFramework panel starts hidden and remains available with
the `Insert` key for diagnostics.
The launcher also hides the parent `cmd.exe` window Steam uses to expand the
`%command%` wrapper while retaining session cleanup after DMC5 exits.

## What the launcher verifies

Before DMC5 starts, all four paths must be ready:

- writable DualSense USB output for triggers and the lightbar;
- four-channel WASAPI advanced haptics;
- direct physical DualSense input;
- the ViGEm virtual Xbox 360 input seen by this older PC game.

Windows often leaves the separate DualSense speaker endpoint at 0% volume. The
bridge remembers that endpoint's volume and mute state, makes it audible only
for the DMC5 session, and restores the original state after the game exits.

## Authentic profile notes

The default `Authentic` profile reproduces the PS5 controller experience without
adding extra synthetic effects. A face button cannot physically gain trigger
resistance, so Blue Rose gets adaptive resistance only when its action is mapped
to L2 or R2. Remap detection requires three consecutive gameplay events on the
same physical trigger and then locks that side for the game session, preventing
an incidental trigger press from moving the effect.

Ordinary sword hits, Blue Rose hold, EX-Act, and MAX-Act use DMC5's standard
combat feedback whenever the game emits it. V uses the purple character light
and ordinary combat rumble; dedicated adaptive-trigger resistance is not part of
his profile.

## Configuration and diagnostics

After installation, settings and logs are located in the game's
`DMC5DualSense` directory. The important files are:

- `config.json` — strengths, feature switches, and audio endpoint settings;
- `launcher.log` — game and bridge startup readiness;
- `bridge.log` — HID, trigger, rumble, audio, and input counters;
- `plugin.log` — RE Engine hooks and gameplay events.

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
