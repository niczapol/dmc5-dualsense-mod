# 1.7.4 acceptance and laptop handoff (engineering notes)

Current GitHub revision: 1.7.4-rc2. The user confirmed normal USB gameplay on
rc1, with USB reconnect still failing. This is a working USB-session build,
not a claim of complete hardware coverage or a Nexus replacement.
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

Steam output handle caching and the audio stream lifecycle are restored to the
1.7.3 baseline in rc2. The strict four-channel endpoint check is retained.
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

Run the existing logic suites and `Tools/Test-CleanInstall.ps1` plus both
`Tests/Portability/*.ps1` audit entrypoints against the new ZIPs. In this
candidate their assertions should be green, not waived. Also test an upgrade
from each 1.7.3 variant and both directions of package switching.

GitHub CI obtains pinned UI/WAV inputs from the public 1.7.3 native ZIP,
validates its checksum and each asset, fetches the pinned LLVM toolchain, builds
both full packages, runs the Windows tests and retains candidate ZIP artifacts.
These Windows runner checks can be repeated while working on a MacBook.
They do not simulate physical adaptive-trigger force or actual Steam gameplay.

### Historical rc1 results (2026-09-05)

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

The public stable release and Nexus files remain 1.7.3. Keep this working
revision on GitHub for later distribution; do not update Nexus in this task.
For local rollback, a pre-update backup is stored on the Windows development
PC under `C:/ds/.work/backup-before-1.7.4-rc1-20260905`. It contains mod state,
logs/configuration and owned loose files, not the multi-gigabyte game PAK.
Use the candidate's uninstaller followed by the stable 1.7.3 installer for
normal rollback; do not blindly overlay old manifests or PAK hash records.

## Hardware acceptance record and future checks

The user reports that everything in the tested rc1 session works except USB
reconnection. Do not infer that a second physical controller, Bluetooth,
fallback gameplay or a clean second PC was tested. rc2 removes the experiment;
its rebuilt packages need fresh automated checks, not relabelled rc1 results.

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

## Rejected reconnect experiment — internal history only

### rc2 verification

Runtime/package source commit: `962693bf2d8db6f90fe94a2a74dfc6bd0376d47f`.
Both local ZIPs passed clean installation/uninstallation, eight adversarial
installer cases each and all eight combined runtime checks. Existing logic
tests passed. The first rc2 CI run (33944937635) also reported every runtime
assertion as passing, but its caller treated an unset `$LASTEXITCODE` as failure.
The audit scripts now explicitly return zero on success instead of depending
on a preceding compiler invocation to initialize the caller's exit-code state.
This is a test-harness correction, not a change to runtime behavior or assertions.

Local native ZIP: 16,204,969 bytes, SHA-256
`A8DD3D9B853BD7423D8EEF0E204B0BA358A14598286B045ACA357BD865FA443A`.
Local fallback ZIP: 97,984,089 bytes, SHA-256
`936203D96FBDEAC1F5EA34089AD992B7B99B8119F00851985315AF33405FC0E4`.
The user's real installation is still the tested rc1; rc2 was built/tested in
isolated folders, not installed over their working game during this cleanup.

### Experiment outcome

Implementation at `24e00b8` / `753120d`: enumerate Steam OUTPUT handles on each
write, retain a still-present handle or select a replacement, reopen a failed
WASAPI stream and discard queued voices. A fake Steam API proved replacement
of handle 101 by 202, but did not exercise Steam's actual gameplay INPUT path.
The user's USB disconnect/reconnect test still lost control in the game.
Therefore the mock passing was not end-to-end hotplug acceptance and must not
be used to justify this approach again without reproducing the input failure.

At the user's request, rc2 removes those changes from both Bridges and removes
the experiment-only fake API and lifecycle test from active CI. Historical
source/tests remain recoverable from the commits above; do not rewrite shared
Git history or erase diagnostic evidence. Public changelog describes retained
fixes only. No new per-frame reconnect logging or input workaround is added.
Further work, if authorized, must distinguish physical input, Steam/game input,
Steam output and WASAPI audio instead of treating them as one connection.
