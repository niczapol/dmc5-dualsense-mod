# DMC5 DualSense Layer 1.5.0 — Session Haptics

First reproducible public Windows release of the complete DualSense support
layer for Devil May Cry 5 on Steam.

## Included

- adaptive trigger profiles transferred from the PS5 Nero and Dante branches;
- all 12 identified PS5 advanced-haptics events with their gain, pitch, delay,
  and loop behavior;
- ordinary PC rumble translated to DualSense actuators;
- PS5 character lightbar colors for Nero, Dante, V, and Vergil;
- direct USB DualSense input mirrored to a session-only ViGEm Xbox 360 device;
- PlayStation prompts and an aligned DualSense diagram in controls and The Void;
- normal Steam Play-button launch through a hidden session launcher;
- reversible installation with file hashes, backups, and exact PAK hash restore.

## Installation

1. Download `DMC5DualSense-v1.5.0-win-x64.zip` and `CHECKSUMS.txt`.
2. Verify the ZIP SHA-256 if desired, then extract it.
3. Run `INSTALL-DMC5-DualSense.cmd`.
4. Accept the ViGEmBus UAC prompt only if the driver is not already installed.
5. Disable Steam Input for DMC5 and paste the launch option copied by the
   installer.
6. Connect DualSense by USB and start the game normally from Steam.

The package is self-contained; no separate .NET installation is needed.

## Verified session

The release candidate was exercised with Nero and Dante. Logs confirmed live
character/HP state, adaptive-trigger writes on every output cycle, ordinary
motor output, Blue Rose, EX/MAX-Act, Ebony/Ivory, weapon-hit events, non-zero
four-channel haptics audio, and lossless physical-to-virtual input throughput.
The launcher shut the bridge down cleanly after DMC5 exited with code 0.

## Important notes

- Full advanced haptics require USB; Bluetooth does not expose the necessary
  four-channel audio endpoint.
- ViGEmBus 1.22.0 is the final official signed Nefarius release and the upstream
  project is archived. It is bundled for installation convenience and is not
  removed automatically because other controller tools may use it.
- This is an unofficial community mod. A legitimate Steam copy is required;
  the project is not affiliated with Capcom, Sony, Valve, or Nefarius.

See `README_EN.md`, `README_RU.md`, and `release-manifest.json` inside the ZIP
for complete details and per-file SHA-256 hashes.
