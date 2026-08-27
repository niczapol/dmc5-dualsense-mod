# DMC5 DualSense

[English](README.md) | [Русский](README_RU.md)

Full DualSense support for the Windows Steam version of Devil May Cry 5:
adaptive triggers, advanced haptics, ordinary combat rumble, character
lightbar colors, PlayStation prompts, and aligned DualSense diagrams in the
controls menu and The Void.

## Which download should I use?

Start with **[Native C++ 1.6.0](https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.6.0/DMC5DualSense-Native-1.6.0-win-x64.zip)**.
It is the recommended, smaller build. If it does not start correctly on your
PC, use the **[Managed C# 1.6.0 fallback](https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.6.0/DMC5DualSense-Managed-1.6.0-win-x64.zip)**.
The fallback implements the same controller behavior but is much larger because
it includes its own .NET runtime; neither build requires additional software.

Do not install both at once. Running either installer over the other performs a
safe replacement while preserving user configuration and logs.

## Features

- adaptive-trigger behavior based on the live DMC5 controller bindings;
- advanced haptic feedback for supported Nero, Dante, and Vergil events;
- DMC5's ordinary combat rumble for every playable character, including V;
- character-specific lightbar colors;
- PlayStation prompts and correctly aligned DualSense diagrams;
- stable touchpad, buttons, sticks, and remapping through normal Steam Input;
- hidden, session-only launcher and bridge that exit with the game;
- reversible installation with backups, hashes, and exact PAK rollback.

## Requirements

- Windows 10 or 11, x64;
- a legitimate Steam copy of Devil May Cry 5;
- a DualSense connected over USB;
- Steam Input enabled or left at the game's default setting.

USB is the only fully supported and physically verified connection. Bluetooth
is experimental: Steam Input should continue to provide buttons, sticks,
touchpad input, and remapping, but this mod's trigger, lightbar, and ordinary
rumble output has not been validated over Bluetooth and is not guaranteed.
Advanced haptics cannot work over the current Bluetooth path because Windows
does not expose the required four-channel DualSense audio endpoint. Sony also
documents PC haptic feedback as requiring USB in its
[DualSense compatibility notes](https://www.playstation.com/en-us/support/hardware/pair-dualsense-controller-bluetooth/).

Close PlayStation Accessories, DS4Windows, DualSenseX, and similar controller
utilities while playing.

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models are supported through Steam's
PS5-controller abstraction, so the mod does not depend on a particular internal
hardware revision or USB product ID. DualSense Edge is recognized by the same
output path and by the audio-endpoint matcher, but it has not yet completed our
physical-controller test matrix. Full feedback support remains USB-only.

## Installation

1. Download the **Native C++** ZIP from the latest GitHub release.
2. Extract the complete ZIP to a normal writable folder.
3. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
4. Leave Steam Input enabled/default for DMC5.
5. Paste the command copied by the installer into
   `Steam → Devil May Cry 5 → Properties → Launch Options`.
6. Connect the controller by USB and start the game normally with Steam's Play
   button.

If the native build does not start correctly, download the **Managed C#
fallback** ZIP and run its installer in the same way. No separate .NET
installation is needed because the fallback archive includes its runtime.

See the detailed [English guide](Native/Package/README_EN.md),
[Russian guide](Native/Package/README_RU.md), and [changelog](CHANGELOG.md).

## Disclaimer

This is an unofficial community mod and is not affiliated with Capcom, Sony,
or Valve. It requires the user's own Steam copy of Devil May Cry 5 and does not
include the game executable, saves, or any DRM bypass.
