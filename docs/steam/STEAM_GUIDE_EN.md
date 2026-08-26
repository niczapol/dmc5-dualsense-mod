# Steam Community Guide draft — English

Suggested title: **Full DualSense Support on PC — Adaptive Triggers, Haptics and PlayStation UI**

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

GitHub project and releases:

https://github.com/niczapol/dmc5-dualsense-mod

Use **Native C++** first. It is the recommended, compact build and does not
require .NET. The **Managed C# fallback** implements the same behavior and is
provided only for PCs where the native build does not start correctly.

## Requirements

- Windows 10/11 x64;
- a legitimate Steam copy of Devil May Cry 5;
- a DualSense connected over USB;
- Steam Input enabled or set to the game's default.

USB is required for the four-channel audio endpoint used by advanced haptics.
Close DS4Windows, DualSenseX, PlayStation Accessories, and similar controller
tools before starting the game.

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models are revision-independent through
Steam's PS5-controller type. DualSense Edge is recognized by the same output and
audio paths, although its complete hardware matrix is not yet physically tested.

## Installation

1. Download `DMC5DualSense-Native-1.6.0-win-x64.zip` from the latest release.
2. Extract the complete ZIP to a normal writable folder.
3. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
4. In Steam, open `Devil May Cry 5 → Properties → Controller` and leave Steam
   Input enabled/default.
5. Paste the launch command copied by the installer into
   `Properties → General → Launch Options`.
6. Connect DualSense over USB and start DMC5 normally with Steam's Play button.

To switch to the fallback build, run its installer over the current version.
The installer safely replaces the runtime and preserves configuration and logs.

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
- Native build does not start: install the Managed C# fallback from the same
  GitHub release; no separate .NET download is needed.

## Suggested screenshots

1. DualSense diagram in the controls menu with a D-pad or shoulder marker active.
2. Compact DualSense diagram in The Void with several correctly aligned markers.
3. PlayStation face-button prompts during gameplay or training.
4. Optional photo showing the character-specific lightbar color.
5. Steam Launch Options field containing the generated launcher command.

This is an unofficial community mod and is not affiliated with Capcom, Sony,
or Valve. A legitimate Steam copy of Devil May Cry 5 is required.
