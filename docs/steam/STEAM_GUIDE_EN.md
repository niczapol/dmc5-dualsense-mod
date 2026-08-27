# Steam Community Guide — English

Suggested title: **DMC5 DualSense Support — Adaptive Triggers, Haptics & PlayStation UI**

Suggested category: `Modding or Configuration`

---

This mod adds full DualSense support to the Steam version of Devil May Cry 5:

- adaptive triggers for the supported Nero and Dante actions;
- advanced haptics for supported weapon and character events;
- normal DMC5 combat rumble for Nero, Dante, V, and Vergil;
- character-specific lightbar colors;
- PlayStation button prompts;
- a properly aligned DualSense diagram in the controls menu and The Void;
- stable touchpad input through Steam Input.

The mod is session-only. It does not install a controller driver, create a
virtual gamepad, add a Windows startup entry, or leave a process running after
DMC5 exits.

## Download

Project page, downloads, source code, and updates:

https://github.com/niczapol/dmc5-dualsense-mod

For most users: **Recommended version — lightweight** (C++ build):

https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.6.0/DMC5DualSense-Native-1.6.0-win-x64.zip

If it does not start on your PC: **Compatibility version — fallback** (C# build):

https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.6.0/DMC5DualSense-Managed-1.6.0-win-x64.zip

Both versions provide the same controller features and use the same installation
steps. The compatibility version is larger because it includes its own runtime;
you do not need to install .NET separately.

## Requirements

- Windows 10/11 x64;
- a legitimate Steam copy of Devil May Cry 5;
- a DualSense connected over USB;
- Steam Input enabled or set to the game's default.

USB is the only fully tested connection for the complete feature set. Bluetooth
is experimental: normal Steam Input controls should remain available, but
trigger, lightbar, and ordinary-rumble output is not guaranteed. Advanced
haptics cannot work through the current Bluetooth path because Windows does not
expose the required four-channel DualSense audio endpoint.

Close DS4Windows, DualSenseX, PlayStation Accessories, and similar controller
tools before starting the game.

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models are revision-independent through
Steam's PS5-controller type. DualSense Edge is recognized by the same output and
audio paths, although its complete hardware matrix is not yet physically tested.

## Installation

Installation is identical for both versions:

1. Download the Recommended version, or use the Compatibility version if the
   Recommended version does not start on your PC.
2. Extract the complete ZIP to a normal writable folder.
3. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
4. In Steam, open `Devil May Cry 5 → Properties → Controller` and leave Steam
   Input enabled/default.
5. Paste the launch command copied by the installer into
   `Properties → General → Launch Options`.
6. Connect DualSense over USB and start DMC5 normally with Steam's Play button.

To switch between versions, run the other installer over the current one. The
installer safely replaces the runtime and preserves configuration and logs.

## What to expect

Nero's Exceed and Blue Rose effects follow the actions currently assigned in
DMC5. Dante's firearm resistance appears on L2 or R2 only when the gun action is
actually assigned there; face-button assignments leave both triggers free.
Controller remaps apply without restarting the game.

V uses his purple lightbar color and DMC5's ordinary combat rumble. His profile
does not add an artificial adaptive-trigger effect.

The physical touchpad remains on Steam Input and continues to work after
switching between mouse/keyboard and controller input.

## Uninstallation

Close DMC5 and run
`Devil May Cry 5\DMC5DualSense\UNINSTALL-DMC5-DualSense.cmd`.
The installer manifest restores backed-up files and the original GUI PAK table
entries. Saves and the game executable are not modified.

## Troubleshooting

- No effects: confirm the Steam launch option still points to
  `DMC5DualSense.Launcher.exe`.
- No advanced haptics: reconnect the controller by USB and check that Windows
  exposes `Speakers (DualSense Wireless Controller)`.
- Duplicate or missing input: close other controller utilities and leave Steam
  Input enabled/default.
- Recommended version does not start: install the Compatibility version from
  the same GitHub release; no separate .NET download is needed.

## Feedback and bug reports

If something does not work, leave a comment under this guide, send me a Steam
message, or open an issue on GitHub:

https://github.com/niczapol/dmc5-dualsense-mod/issues

Please include the mod version, your controller model, USB/Bluetooth connection,
the affected character or action, and the files from
`Devil May Cry 5\DMC5DualSense\Logs`. These details make problems much easier to
reproduce and fix.

This is an unofficial community mod and is not affiliated with Capcom, Sony,
or Valve. A legitimate Steam copy of Devil May Cry 5 is required.
