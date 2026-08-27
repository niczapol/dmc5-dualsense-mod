## DMC5 DualSense Layer 1.6.0

Version 1.6.0 makes the accepted native C++ implementation the primary release
and keeps the self-contained C# implementation as a compatibility fallback.

### Downloads

- **`DMC5DualSense-Native-1.6.0-win-x64.zip` — recommended.** Small native C++
  package with no .NET runtime requirement.
- **`DMC5DualSense-Managed-1.6.0-win-x64.zip` — fallback.** Use only if the
  native build does not start correctly on a particular PC. It implements the
  same controller behavior and includes its own .NET runtime.

Neither archive requires a separate .NET installation, ViGEm, a virtual
controller, or an additional driver.

### Connection support

USB is the supported and physically verified connection for the complete
feature set. Bluetooth is experimental: normal Steam Input controls should
remain available, but trigger, lightbar, and ordinary-rumble output is not
guaranteed. Advanced haptics cannot work over the current Bluetooth path because
Windows does not expose the required four-channel DualSense audio endpoint.

### Native runtime

- Ports Launcher, Bridge, and the REFramework plugin to native C++.
- Keeps Steam Input as the only owner of buttons, sticks, remapping, and the
  physical touchpad.
- Sends adaptive triggers, LED colors, and ordinary rumble through
  `SteamInput006` and advanced haptics through four-channel WASAPI.
- Runs only for the current DMC5 session and exits with the game.
- Reduces the primary download from roughly 96 MiB to roughly 15 MiB.

### Reproducibility and installation

- Uses pinned LLVM-MinGW and REFramework inputs.
- Produces byte-identical native binaries and ZIP archives across independent
  builds.
- Includes all end-user runtime files in the ZIP.
- Passes clean install, exact installed-hash verification, reversible GUI PAK
  invalidation, and clean uninstall without launching the game.
- Safely upgrades either previous implementation while preserving user
  configuration and logs.

### Interface polish

- Adds the DualSense diagram to Pause → Display Controls without freezing
  character-specific labels, custom bindings, or keyboard/controller switching.
- Separates the touchpad/Provoke and Options/Pause indicators.
- Cleans the PlayStation prompt atlas and aligns Settings, The Void, stick,
  D-pad, shoulder, touchpad, and active Circle markers with the controller art.

Leave Steam Input enabled/default for DMC5, connect DualSense over USB, run the
installer, paste its Steam launch option, and start the game normally.
