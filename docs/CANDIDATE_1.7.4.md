# 1.7.4-rc1 acceptance and laptop handoff

This is a test candidate, not a replacement of the public 1.7.3 release yet.
Source branch: `fix/1.7.4-portability`. No Nexus upload is authorized by this
candidate workflow.

## Architecture changes

Both packages now install the exact same native REFramework Plugin API 1.10
telemetry DLL. The lightweight package uses the native Bridge; fallback uses
the self-contained managed Bridge. The fallback no longer packages or loads
REFramework.NET/C++/CLI and does not borrow a system-installed .NET runtime
for game telemetry. The historical `Plugin/DMC5DualSense.cs` is retained as
reference source, not shipped or used by these candidate packages.

Game input and touchpad still belong exclusively to Steam Input. No changes
were made to the accepted trigger profiles, button-remapping source or UI media.
Both Bridges receive the same saved-per-character bindings through the native
plugin, including the configured UDP port.

Steam OUTPUT handles are re-enumerated, audio failures can reopen the endpoint,
and stale sample queues are discarded. These are necessary output recovery
changes, NOT proof that Steam restores gameplay INPUT after unplug/replug.
One USB DualSense is the supported acceptance target; multiple-controller
routing, Bluetooth and Edge remain unverified. Restart the game after USB
reconnection if gameplay input is absent. Do not add an input workaround
unless a separate, reproduced input-layer defect requires it.

## Installer safety boundary

`Install.ps1` calls the transaction wrapper; `Install-Core.ps1` is internal.
Use the public entrypoint, never invoke the core directly. The wrapper validates
sizes/hashes and payload membership before running the old uninstaller.
On a handled failure it restores snapshotted targets and the old affected PAK
TOC rows. Other plugin directories are never recursively cleared. Unknown
loader replacement still requires consent and cannot promise simultaneous
compatibility with mods depending on the replaced loader.

Snapshots retained after a failure are printed in the error output. Successful
transactions remove only their uniquely named temporary snapshot. This is
handled-exception rollback, not a power-loss-safe filesystem transaction.
Close the game and Bridge; do not run two installers or a mod manager
concurrently. Junction/reparse-point targets are rejected rather than risking
files outside the chosen directory.

## Automated acceptance

Run the existing logic suites and `Tools/Test-CleanInstall.ps1` plus all three
`Tests/Portability/*.ps1` audit entrypoints against the new ZIPs. In this
candidate their assertions should be green, not waived. Also test an upgrade
from each 1.7.3 variant and both directions of package switching.

GitHub CI obtains pinned UI/WAV inputs from the public 1.7.3 native ZIP,
validates its checksum and each asset, fetches the pinned LLVM toolchain, builds
both full packages, runs the Windows tests and retains candidate ZIP artifacts.
These Windows runner checks can be repeated while working on a MacBook.
They do not simulate physical adaptive-trigger force or actual Steam gameplay.

### Recorded results (2026-09-05)

- Code and build-input verification commit: `753120d8fa0c324563251643c51506e91f31b1a5`.
- [Windows CI run 33943760124](https://github.com/niczapol/dmc5-dualsense-mod/actions/runs/33943760124):
  both jobs passed, including fresh native/fallback package builds and all
  installer, controller-handle and runtime-isolation regression tests.
- Both final local packages passed clean installation/uninstallation and eight
  adversarial installer cases each. Eight combined runtime-isolation checks
  passed. Local migration fixtures also passed upgrades from each 1.7.3
  variant and switching native/fallback in both directions.
- Installed the native candidate over the developer PC's stable installation:
  all 33 installed manifest files matched; user configuration was preserved
  byte-for-byte. No game launch or tactile test was performed in this pass.
- Local source build was `24e00b8`; the subsequent `753120d` change only fixes
  pinned upstream header verification for LF/CRLF downloads. CI caught this
  fresh-download failure that the existing local compiler/header cache hid.
- Native local ZIP: 16,204,974 bytes; SHA-256
  `B899EE3FBCB9827D3386C88441B7600BEA5903A6303352EE8CBCA3F519B057B7`.
  Fallback local ZIP: 97,984,513 bytes; SHA-256
  `60C318037AAF7ED432BB6EAA528B608795669D185C10A2C47138E0A97345CDA4`.
  These identify local packages, not a claim of byte-identical CI archives.

### Continuing from a laptop

Clone this repository and check out `fix/1.7.4-portability`; read this file and
the audit before changing input/output architecture. The successful run above
has a `windows-tested-candidates` artifact containing both ZIPs and their
checksums (90-day retention, GitHub sign-in required). This is not a Release.
After artifact expiration, rerun CI on this branch: its pinned media and
compiler download do not need the developer PC. Windows jobs run remotely;
macOS alone cannot run the game or validate hardware effects.

The public stable release and Nexus files remain 1.7.3. Do not merge/publish
this candidate as accepted until the hardware checklist below is confirmed.
For local rollback, a pre-update backup is stored on the Windows development
PC under `C:/ds/.work/backup-before-1.7.4-rc1-20260905`. It contains mod state,
logs/configuration and owned loose files, not the multi-gigabyte game PAK.
Use the candidate's uninstaller followed by the stable 1.7.3 installer for
normal rollback; do not blindly overlay old manifests or PAK hash records.

## Human checks before stable publication

1. Cold launch through Steam with one USB DualSense already connected.
2. Nero: default L2 Exceed and shooting explicitly remapped to R2; verify both.
3. Dante: gun fire mapped to trigger; face-button mapping must leave it free.
4. Ordinary rumble (including V), advanced samples, lightbar and touchpad;
   switch mouse/keyboard and gamepad without introducing input hooks.
5. UI in settings, pause legend and The Void remains the accepted layout.
6. Disconnect/reconnect once in the menu and once during gameplay: record
   separately gameplay input, trigger/light output and audio haptics.
7. Exit: Bridge ends; triggers release. Reopen once for a clean session.

Try the fallback separately if time permits. A no-developer-tools Windows PC
or VM remains useful for a final independent prerequisite check. Do not
publish the candidate as hardware-verified until the user confirms these tests.
