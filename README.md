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

## Install a release

Download the current Windows ZIP from GitHub Releases, extract it, and run
`INSTALL-DMC5-DualSense.cmd`. The package locates the Steam game, offers to
install the official signed ViGEmBus driver when required, installs
REFramework, and copies the Steam launch command to the clipboard.

The complete feature set requires Windows 10/11, a legitimate Steam copy, and
a DualSense connected over USB. See [the English guide](Package/README_EN.md) or
[the Russian guide](Package/README_RU.md).

## Reproducible release build

Runtime code and third-party dependencies are reproducible from public source.
The original PS5 haptic media and modified game UI payloads are deliberately not
stored in Git history. Maintainers supply those local inputs; the builder checks
every byte against `release-assets.json` before producing a package.

On Windows with .NET 10 installed:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Build-Release.ps1
```

The command downloads only the three pinned public dependencies, publishes the
bridge and launcher as self-contained win-x64 executables, assembles the
installer, writes a complete file manifest, and creates a deterministic ZIP and
`CHECKSUMS.txt` under `artifacts/`.

GitHub Actions independently compiles the public projects and runs the
deterministic input/trigger tests on every push and pull request.

## Game assets

Original Capcom/Sony UI and haptic media are not committed to this repository.
The build expects those resources to be supplied from a legally obtained local
game copy or an already prepared private package.

Release users never need the PS5 image or extraction workspace. They install the
prepared release archive into their own legitimate Steam copy. Contributors
building a full authentic package must provide the locally obtained inputs
listed in `release-assets.json`.
