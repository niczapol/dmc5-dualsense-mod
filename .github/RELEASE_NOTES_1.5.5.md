## DMC5 DualSense Layer 1.5.5

This release replaces the mod's controller-input emulation with a stable,
session-only output architecture and completes the DualSense interface
calibration.

### Controller runtime

- Keeps buttons, sticks, remapping, and touchpad input on DMC5's normal Steam
  Input path.
- Sends adaptive-trigger effects, character lightbar colors, and ordinary
  rumble through Steam Input's DualSense output API.
- Keeps four-channel WASAPI advanced haptics for the supported Nero, Dante, and
  Vergil events.
- Removes direct HID input, the virtual Xbox controller, ViGEmBus, HidSharp,
  resident controller watchers, and Windows autostart behavior.
- Starts the bridge only for the current DMC5 session and reliably shuts it down
  when the game exits.
- Keeps the physical touchpad stable across keyboard/mouse and controller source
  switching because the mod no longer intercepts gameplay input.

### Gameplay behavior

- Routes Nero's Exceed and Blue Rose effects from the live in-game bindings.
- Routes Dante's firearm resistance only when `AttackL` is actually assigned to
  L2 or R2; face-button assignments leave both triggers free.
- Applies controller-layout changes without restarting the game.
- Uses DMC5's final motor output for ordinary combat rumble, with the existing
  watchdog preventing vibration tails.
- Keeps detailed calibration counters, event traces, metadata dumps, and CSV
  logs disabled by default.

### Interface

- Aligns the complete DualSense D-pad in Nero's controls and each individual
  direction for Dante, V, and Vergil.
- Aligns L1/L2/R1/R2, L3/R3, face buttons, Options, and the full touchpad
  highlight in both the controls menu and The Void.
- Applies the compact controller calibration to both runtime UI branches and
  keeps markers in the controller artwork's local coordinate system across
  resolution changes.
- Corrects the Settings Circle marker size and PlayStation color treatment.

### Installation

- Leaves Steam Input enabled or at its default setting for DMC5.
- Requires no virtual-controller driver and no separately installed .NET
  runtime when using the complete release ZIP.
- Preserves reversible installation, exact file manifests, backups, and safe
  upgrade behavior.
