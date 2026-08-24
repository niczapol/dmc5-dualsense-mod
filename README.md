# DMC5 DualSense

Native DualSense support layer for the Steam version of Devil May Cry 5.

The project combines a REFramework telemetry plugin with a small local bridge
that drives DualSense adaptive triggers, lightbar output and advanced haptics.
After installation, normal gameplay is started directly from Steam; command
files are reserved for installation and diagnostics.

## Repository layout

- `Plugin/` — in-game REFramework plugin.
- `Bridge/` — DualSense HID/audio bridge.
- `Package/` — installer, uninstaller, configuration and documentation.
- `research/` — reproducible notes and reports derived from local analysis.

## Game assets

Original Capcom/Sony UI and haptic media are not committed to this repository.
The build expects those resources to be supplied from a legally obtained local
game copy or an already prepared private package.

See `Package/README_RU.md` for the current Russian installation and usage notes.
