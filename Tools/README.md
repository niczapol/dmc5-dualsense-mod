# UI build tools

The repository does not contain extracted DMC5 textures, GUI files, or the
third-party controller artwork. The two tools in this directory make the local
UI build reproducible without committing copyrighted game payloads.

`UiAssetBuilder` performs three deterministic operations:

1. preserves the aspect ratio of the 1467x816 DualSense source image and places
   it into the 1024x1024 and 512x512 controller atlases;
2. draws the DualSense Create/Options system-button prompts;
3. copies only selected 4x4 BC7 blocks into an original RE Engine TEX file.

The last step leaves every prompt and highlight block outside the explicitly
listed controller/system-button rectangles byte-identical to the original
PlayStation prompt mod. This prevents a second lossy BC7 pass over the ordinary
button icons.

`GuiLayoutTool` writes the matching physical button centers into the PC `c_XB1`
nodes that DMC5 uses with Steam Input. Large controller clips use one coordinate
for every active layer, while the Void diagram uses calibrated static panel
positions. The default project reference expects REE-Lib in the sibling
`work/vendor/REE-Content-Editor` checkout; pass `-p:ReeLibProject=...` to MSBuild
to use another location.

The local build uses the MIT-licensed Gamepad Asset Pack DualSense source and
Microsoft DirectXTex `texconv` for `BC7_UNORM_SRGB`, one mip level. These external
inputs and their licenses remain outside this repository.
