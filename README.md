# DMC5 DualSense

[English](README.md) | [Русский](README_RU.md)

Full DualSense support for the Windows Steam version of Devil May Cry 5:
adaptive triggers, advanced haptics, ordinary combat rumble, character
lightbar colors, PlayStation prompts, and aligned DualSense diagrams in the
controls menu and The Void.

## Which download should I use?

Start with the **[Recommended version — lightweight](https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.7.1/DMC5DualSense-Native-1.7.1-win-x64.zip)**
(C++ build). If it does not start correctly on your PC, use the
**[Compatibility version — fallback](https://github.com/niczapol/dmc5-dualsense-mod/releases/download/v1.7.1/DMC5DualSense-Managed-1.7.1-win-x64.zip)**
(C# build). Both provide the same controller features. The compatibility
version is much larger because it includes its own .NET runtime; neither one
requires additional software.

Do not install both at once. Running either installer over the other performs a
safe replacement while preserving user configuration and logs.

## Features

- adaptive-trigger behavior based on the live DMC5 controller bindings;
- advanced haptic feedback for supported Nero, Dante, and Vergil events;
- DMC5's ordinary combat rumble for every playable character, including V;
- character-specific lightbar colors;
- PlayStation prompts and correctly aligned DualSense diagrams;
- automatic hidden Bridge startup from the in-game plugin, with no Steam launch
  command or resident background service;
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

Installation is identical for both versions:

1. Download either the **Recommended version — lightweight** or, if it does not
   work on your PC, the **Compatibility version — fallback** using the links
   above.
2. Extract the complete ZIP to a normal writable folder.
3. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
4. Leave Steam Input enabled/default for DMC5.
5. If upgrading from an older release, remove its old
   `DMC5DualSense.Launcher.exe` line from Steam Launch Options.
6. Connect the controller by USB and start the game normally with Steam's Play
   button.

No Steam Launch Options are required. REFramework loads the in-game plugin,
which starts the hidden Bridge for that DMC5 process and closes it with the
game. The bundled Launcher remains only as a compatibility fallback.

The bundled `dinput8.dll` is an unmodified, pinned official REFramework build;
it contains no DMC5DualSense-specific code. If the game already has a different
recognized REFramework build, the installer preserves it and installs only this
mod's plugin and assets. If an unknown `dinput8.dll` is found, the normal
installer asks whether to replace it. Choosing `Y` keeps an exact backup before
replacement; choosing `N` cancels without changing the existing DLL. The
uninstaller restores a replaced DLL exactly. The recommended native plugin uses the stable
REFramework Plugin API 1.10 prefix; the managed fallback requires Plugin API
1.15 and may need replacement mode with an older framework.

See the detailed [English guide](Native/Package/README_EN.md),
[Russian guide](Native/Package/README_RU.md), and [changelog](CHANGELOG.md).

## Steam Community guides

- [English - DMC5 DualSense Support: Adaptive Triggers, Haptics and PlayStation UI](https://steamcommunity.com/sharedfiles/filedetails/?id=3790893015)
- [Russian - DMC5 DualSense Support: адаптивные курки, хаптика и интерфейс PlayStation](https://steamcommunity.com/sharedfiles/filedetails/?id=3790889124)

The Steam guides provide a concise public installation and compatibility
reference. Keep them synchronized with changes to releases, installation,
hardware support, known limitations, and the bug-report procedure.

## Disclaimer

This is an unofficial community mod and is not affiliated with Capcom, Sony,
or Valve. It requires the user's own Steam copy of Devil May Cry 5 and does not
include the game executable, saves, or any DRM bypass.
