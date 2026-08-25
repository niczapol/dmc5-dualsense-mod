# Build tools

The tools in this directory produce deterministic release and UI outputs for
the DMC5 DualSense mod.

`Build-Release.ps1` validates required inputs, runs the test suite, publishes
self-contained Windows executables, writes a per-file manifest, and creates a
deterministic release ZIP with `CHECKSUMS.txt`.

`UiAssetBuilder` prepares the DualSense controller atlases and PlayStation
system-button prompts while preserving unaffected UI regions.

`GuiLayoutTool` aligns the interactive button positions used by the controls
menu and The Void with the visible DualSense diagram.

`GuiLayoutTool` uses REE-Lib. Its default project reference expects
REE-Content-Editor in the documented sibling checkout; pass
`-p:ReeLibProject=...` to MSBuild when using another location.
