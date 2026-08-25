# DMC5 DualSense

Native DualSense support layer for the Steam version of Devil May Cry 5.

The mod brings the PS5 controller experience to PC, including adaptive
triggers, advanced haptics, character lightbar colors, PlayStation button
prompts, and an aligned DualSense diagram in the controls menu and The Void.
It supports the complete PS5-style controller event set implemented by the mod
for Nero, Dante, V, and Vergil, together with the game's ordinary combat rumble.

Normal gameplay starts directly from Steam after installation. Command files
are used only for installation, removal, and optional diagnostics.

## Features

- adaptive-trigger behavior matching the PS5 version;
- advanced haptic feedback for supported character and weapon events;
- ordinary DMC5 combat rumble through the DualSense actuators;
- character-specific lightbar colors;
- PlayStation prompts and a correctly aligned DualSense controller diagram;
- direct USB controller input with session-only XInput compatibility;
- automatic startup and shutdown with the normal Steam Play button;
- reversible installation with backups and file verification.

## Repository layout

- `Plugin/` — in-game REFramework plugin.
- `Bridge/` — DualSense input, output, and haptics bridge.
- `Launcher/` — hidden Steam session launcher.
- `Package/` — installer, uninstaller, configuration, and user guides.
- `Tests/` — deterministic controller and trigger-mapping tests.
- `Tools/` — reproducible release and UI build tools.

## Install a release

Download the current Windows ZIP from [GitHub Releases](https://github.com/niczapol/dmc5-dualsense-mod/releases),
extract it, and run `INSTALL-DMC5-DualSense.cmd`. The installer locates the
Steam game, offers to install the official signed ViGEmBus driver when needed,
installs the required runtime components, and copies the Steam launch command
to the clipboard.

The complete feature set requires Windows 10/11, a legitimate Steam copy, and
a DualSense connected over USB. Steam Input must be disabled for DMC5. See the
[English guide](Package/README_EN.md) or [Russian guide](Package/README_RU.md).

## Reproducible release build

Runtime code and third-party dependencies build reproducibly from the public
source. The release builder validates all required packaged inputs, runs the
deterministic test suite, publishes self-contained Windows executables, writes
a complete file manifest, and creates a deterministic ZIP with
`CHECKSUMS.txt`.

On Windows with .NET 10 installed:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Build-Release.ps1
```

GitHub Actions independently compiles the public projects and runs the input
and trigger-mapping tests on every push and pull request.

## Disclaimer

This is an unofficial community mod and is not affiliated with Capcom, Sony,
Valve, or Nefarius. It requires the user's own Steam copy of Devil May Cry 5.
The project does not include the game executable, saves, or any DRM bypass.
