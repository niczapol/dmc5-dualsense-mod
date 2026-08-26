# Build tools

The tools in this directory produce deterministic release and UI outputs for
the DMC5 DualSense mod.

`Build-Release.ps1` validates required inputs, runs the test suite, publishes
self-contained Windows executables, writes a per-file manifest, and creates a
deterministic release ZIP with `CHECKSUMS.txt`.

The finished archive includes the pinned REFramework and C# API packages and
the .NET runtime, so end users install no separate runtime or controller driver.
UI and haptic media are supplied to the builder separately and are accepted
only when their sizes and SHA-256 hashes match `release-assets.json`. Use
`-UiDirectory`, `-HapticsDirectory`, and `-DependencyCache` to make every build
input explicit on a clean workstation.

After building, validate the actual ZIP against a generated clean mock Steam
library. The smoke test installs every packaged file, verifies the manifest and
PAK changes, uninstalls, and verifies the exact rollback without launching the
game:

```powershell
.\Tools\Test-CleanInstall.ps1 `
  -PackageZip '.\artifacts\v1.5.5\DMC5DualSense-v1.5.5-win-x64.zip'
```

`UiAssetBuilder` prepares the DualSense controller atlases and PlayStation
system-button prompts while preserving unaffected UI regions.

`GuiLayoutTool` aligns the interactive button positions used by the controls
menu and The Void with the visible DualSense diagram. It also assigns the
PlayStation-specific active marker shapes and sizes, including the full
touchpad overlay. Its `inspect` command prints the effective local layout and
attribute overrides for verification.

`GuiLayoutTool` uses REE-Lib. Its default project reference expects
REE-Content-Editor in the documented sibling checkout; pass
`-p:ReeLibProject=...` to MSBuild when using another location.
