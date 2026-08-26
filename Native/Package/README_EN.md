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

Standard DualSense CFI-ZCT1 and CFI-ZCT2 models use Steam's common PS5-controller
type and need no revision-specific configuration. DualSense Edge is accepted by
the output and audio-endpoint detection paths, but its full hardware matrix is
not yet physically verified. Complete feedback support requires USB.

## Installation

1. Extract the complete archive to a separate folder.
2. Close DMC5 and run `INSTALL-DMC5-DualSense.cmd`.
3. Paste the command shown by the installer into Steam → Devil May Cry 5 →
   Properties → Launch Options.
4. Start the game normally with Steam's Play button.

To remove the mod, run
`Devil May Cry 5\DMC5DualSense\UNINSTALL-DMC5-DualSense.cmd`. The installer
keeps a manifest and backups for reversible removal.

`TEST-DualSense.cmd` runs an optional hardware test for triggers, LED output,
and haptics; it is not required for installation.
