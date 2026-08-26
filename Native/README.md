# Native C++ runtime

This directory contains the primary native C++ runtime. It preserves the
behavior accepted in the managed implementation while removing the managed
runtime requirement from the player package.

Architecture:

- Steam Input remains the only gameplay-input and touchpad owner.
- The native Bridge sends output only through SteamInput006: adaptive triggers,
  LED color, and ordinary rumble.
- Four-channel WASAPI carries the bundled advanced-haptics samples.
- The REFramework plugin publishes game state and events over localhost UDP.
- Launcher and Bridge are session-scoped; background/resident mode is rejected.
- No .NET runtime, direct HID input, ViGEm, virtual controller, or external
  driver is used.

Run `Prepare-Dependencies.ps1` once, build with `build-native.ps1`, and create a
deterministic end-user archive with `build-package.ps1`. UI and haptic media are
external release inputs validated through the root `release-assets.json`.
