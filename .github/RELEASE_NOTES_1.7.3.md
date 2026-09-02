# DMC5 DualSense v1.7.3

Version 1.7.3 hardens controller vibration without changing gameplay input,
touchpad behavior, adaptive-trigger mappings, lightbar profiles, or the
PlayStation UI.

## Changes

- Ordinary DMC5 rumble now has one output owner: Steam Input.
- Advanced haptic events use only the four-channel DualSense audio endpoint.
- Non-silent advanced haptics briefly take actuator priority so two independent
  output paths cannot drive the controller at the same time.
- Each RE Engine motor identifier has an independent 180 ms watchdog, preventing
  traffic on one channel from extending stale vibration on another.
- Overlapping advanced-haptics samples pass through a soft limiter instead of
  hard int16 clipping.
- Native C++ and managed C# implementations share the same behavior and new
  regression coverage.

The release contains two standalone, flat ZIP archives:

- `DMC5DualSense-Native-1.7.3-win-x64.zip` — recommended lightweight version;
- `DMC5DualSense-Managed-1.7.3-win-x64.zip` — compatibility fallback.

No additional runtime, virtual controller, persistent background service, or
Steam Launch Option is required. Full feedback support is verified over USB;
Bluetooth remains experimental and cannot provide four-channel advanced
haptics on the current Windows path.
