# Managed fallback build tools

The tools in this directory produce the deterministic self-contained C#
fallback release and shared UI outputs. The recommended native release uses
`Native/Prepare-Dependencies.ps1` and `Native/build-package.ps1`.

`Build-Release.ps1` validates required inputs, runs the test suite, publishes
self-contained Windows executables, writes a per-file manifest, and creates a
deterministic release ZIP with `CHECKSUMS.txt`.

The finished archive includes the pinned REFramework and C# API contents in
flattened directories, plus the .NET runtime, so end users install no separate
runtime or controller driver. The release builder rejects nested archives so
the resulting ZIP can be inspected directly by distribution-site scanners.
UI and haptic media are supplied to the builder separately and are accepted
only when their sizes and SHA-256 hashes match `release-assets.json`. Use
`-UiDirectory`, `-HapticsDirectory`, `-DependencyCache`, and optionally
`-DotnetPath` to make every build input explicit on a clean workstation.

After building, validate the actual ZIP against a generated clean mock Steam
library. The smoke test installs every packaged file, verifies the manifest and
PAK changes, uninstalls, and verifies the exact rollback without launching the
game. It also covers REFramework coexistence: preservation of a different
recognized build, non-interactive rejection of an unknown proxy DLL, prompted replacement, and
exact restoration, including protection when another tool changes the loader
after installation:

```powershell
.\Tools\Test-CleanInstall.ps1 `
  -PackageZip '.\artifacts\v1.6.0-managed\DMC5DualSense-Managed-1.6.0-win-x64.zip'
```

`UiAssetBuilder` prepares the DualSense controller atlases and rebuilds the
80x80 PlayStation controller-prompt cells at 4x resolution. It zeroes the
near-transparent RGB fringe before BC7 encoding while preserving keyboard-only
and unrelated UI regions.

`GuiLayoutTool` aligns the interactive button positions used by the controls
menu and The Void with the visible DualSense diagram. It also assigns the
PlayStation-specific active marker shapes and sizes, including the full
touchpad overlay. Its `inspect` command prints the effective local layout and
attribute overrides for verification.

`GuiLayoutTool` uses REE-Lib. Its default project reference expects
REE-Content-Editor in the documented sibling checkout; pass
`-p:ReeLibProject=...` to MSBuild when using another location.
