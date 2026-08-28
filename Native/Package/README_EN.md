# DMC5 DualSense Layer — Native C++

This is the recommended build of the Devil May Cry 5 Steam mod. It provides
the complete feature set: binding-aware adaptive triggers, advanced haptics,
character LED colors, and the DualSense interface artwork.

## Requirements

- Windows x64 and a legitimate Steam copy of Devil May Cry 5;
- a DualSense connected over USB;
- Steam Input enabled or left at the game's default controller setting.

The archive is self-contained. It does not require .NET, ViGEm, a virtual
controller, or a separate driver. The Bridge exists only for the current game
session and exits with DMC5.

USB is the only fully supported and physically verified connection. Bluetooth
is experimental: normal Steam Input controls should remain available, but
adaptive triggers, lightbar output, and ordinary rumble are not guaranteed.
Advanced haptics cannot work over the current Bluetooth path because it has no
four-channel DualSense audio endpoint.

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models use Steam's common PS5-controller
type and need no revision-specific configuration. DualSense Edge is accepted by
the output and audio-endpoint detection paths, but its full hardware matrix is
not yet physically verified. Complete feedback support requires USB.

The haptics endpoint is selected primarily from its Sony USB hardware identity
and four-channel format, not from the visible Windows device name. Localized or
user-renamed speaker endpoints therefore need no manual configuration. The
`AudioDeviceContains` setting remains an optional fallback for unusual drivers.

## Installation

1. Extract the complete archive to a separate folder.
2. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
3. If an older version left a `DMC5DualSense.Launcher.exe` command in Steam
   Launch Options, remove it once.
4. Start the game normally with Steam's Play button. No launch command is
   required.

REFramework loads the in-game plugin, which starts the hidden Bridge for the
current DMC5 process. The Bridge exits with the game and creates no resident
watcher or service. The bundled Launcher is retained only as a manual
compatibility fallback.

The package contains an unmodified official REFramework `dinput8.dll`. If a
different recognized REFramework build is already installed, it is preserved
and this installer adds the DMC5DualSense plugin alongside the user's existing
plugins. If an unknown `dinput8.dll` is found, the installer asks whether to
replace it. Choose `Y` to keep an exact backup and continue, or `N` to cancel
without changing the existing DLL. The uninstaller restores a replaced file
exactly. The native plugin deliberately targets the
unchanged ABI prefix available in REFramework Plugin API 1.10 and newer.

To remove the mod, run
`Devil May Cry 5\DMC5DualSense\UNINSTALL-DMC5-DualSense.cmd`. The installer
keeps a manifest and backups for reversible removal.

`TEST-DualSense.cmd` runs an optional hardware test for triggers, LED output,
and haptics; it is not required for installation.
