# DMC5 DualSense

[English](README.md) | [Русский](README_RU.md)

Full DualSense support for the Windows Steam version of Devil May Cry 5:
adaptive triggers, advanced haptics, ordinary combat rumble, character
lightbar colors, PlayStation prompts, and aligned DualSense diagrams in the
controls menu and The Void.

## Which download should I use?

| Build | Recommendation | Extra installation |
| --- | --- | --- |
| **Native C++** | **Use this first.** Small, fast, and the primary supported build. | None |
| Managed C# fallback | Try this only if the native build does not start correctly on your PC. It implements the same controller behavior but has a much larger self-contained runtime. | None |

Download both builds from [GitHub Releases](https://github.com/niczapol/dmc5-dualsense-mod/releases).
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

USB is required for the four-channel audio endpoint used by advanced haptics.
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

## Runtime design

Steam Input is the only owner of gameplay input: buttons, sticks, remapping,
and the physical touchpad. The mod does not capture controller input or create a
virtual gamepad. Its session bridge sends adaptive-trigger effects, LED color,
and ordinary rumble through Steam's DualSense output API, while four-channel
WASAPI carries advanced haptics. There is no service, startup entry, resident
watcher, or background process after DMC5 exits.

## Reproducible builds

The primary C++ build uses pinned LLVM-MinGW and REFramework headers:

```powershell
powershell -ExecutionPolicy Bypass -File .\Native\Prepare-Dependencies.ps1
powershell -ExecutionPolicy Bypass -File .\Native\build-package.ps1 -Version 1.6.0
```

The C# fallback remains buildable with the .NET 10 SDK:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Build-Release.ps1 `
  -Version 1.6.0
```

Final packaging needs the separately supplied UI and haptic media listed in
`release-assets.json`; every input is verified by exact size and SHA-256. The
finished player archives are self-contained and require no .NET installation,
ViGEm, virtual-controller driver, or build tools.

## Community guides

Drafts for the planned Steam Community guides are kept in
[English](docs/steam/STEAM_GUIDE_EN.md) and
[Russian](docs/steam/STEAM_GUIDE_RU.md). Screenshots will be added from the
final native release in-game.

## Disclaimer

This is an unofficial community mod and is not affiliated with Capcom, Sony,
or Valve. It requires the user's own Steam copy of Devil May Cry 5 and does not
include the game executable, saves, or any DRM bypass.
