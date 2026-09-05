# Changelog

## 1.7.4-rc1 — Portability and recovery candidate

- Validate the complete payload before removing an existing installation.
  Keep a transaction snapshot and restore previous files after handled failures.
- Use the same native telemetry plugin in both packages. The compatibility
  Bridge remains self-contained C#, but no longer needs REFramework.NET,
  C++/CLI, or a separately installed .NET runtime inside the game.
- Re-enumerate Steam output handles instead of treating a cached handle as a
  permanent connection. Real gameplay-input hotplug acceptance remains pending.
- Reopen failed audio streams, discard stale sample queues, and require four
  audio channels rather than silently accepting stereo downmixing.
- Return explicit errors for invalid configuration and self-tests blocked by
  an existing Bridge. Standalone diagnostics allow a bounded enumeration wait.
- Add native builds and packaged installer/runtime failure tests to Windows CI.
- Preserve the accepted game input/touchpad path, UI assets, event mapping and
  trigger profiles. No virtual controller or resident service was added.


Only public version milestones are listed here. Internal calibration builds,
discarded test packages, and one-off diagnostics are intentionally omitted.

## 1.7.3 — Vibration-path hardening

- Separated DMC5's ordinary rumble from the four-channel advanced-haptics
  stream. Ordinary motor commands now have exactly one output owner: Steam
  Input.
- Added independent 180 ms watchdogs for all four RE Engine motor identifiers,
  preventing traffic for one actuator from extending a stale value on another.
- Added output arbitration: a non-silent advanced-haptics frame temporarily
  takes actuator priority instead of being driven concurrently with traditional
  rumble.
- Replaced hard int16 clipping with a bounded soft limiter that preserves the
  waveform below a 0.90 knee and safely contains overlapping PS5 samples.
- Added matching native and managed regression coverage for motor expiry,
  actuator aliases, output arbitration, audio-bus separation, and peak safety.

## 1.7.2-r1 — Scanner-friendly packaging revision

- Flattened the pinned REFramework and C# API dependency payloads in both
  downloads. The end-user ZIPs no longer contain archives inside archives.
- Updated both installers to consume the flattened dependency directories
  without changing gameplay, controller output, or coexistence behavior.
- Added a release-build guard and clean-install assertion that reject nested
  archives before a package can be published.

## 1.7.2 — Nero remap-aware resistance

- Fixed both runtimes reading Nero's saved controls from a stale derived
  `PadInput` table. They now read the active character-specific
  `SaveDataManager.KeyAssign` links used by DMC5's controls menu.
- Blue Rose resistance now follows `AttackL` when it is reassigned to R2 while
  Exceed remains independently mapped to L2. Face-button assignments still
  leave the corresponding trigger free.
- Made both runtimes fail closed if the authoritative saved binding table
  exists but cannot be read, instead of silently accepting stale defaults.
- Added deterministic coverage for saved Nero R2/L2 assignments and a live-log
  verifier for the in-game binding contract.

## 1.7.1 — Safe REFramework coexistence

- Changed both installers to coexist with an existing recognized REFramework:
  a different `dinput8.dll` is preserved and DMC5DualSense is installed as an
  additional plugin instead of replacing the framework and risking other mods.
- Added a normal interactive `Y/N` choice when an unknown `dinput8.dll` is
  found. The user no longer needs a PowerShell parameter: accepted replacement
  is backed up, recorded in the install manifest, and restored exactly on
  uninstall; declining leaves the existing DLL unchanged.
- Changed installer and uninstaller console messages to clear English text.
- Added isolated coverage for clean framework installation, preservation of a
  different REFramework build, non-interactive rejection of an unknown proxy,
  and reversible prompted replacement.
- Prevented uninstall from overwriting a pre-existing file that another tool
  changed after DMC5DualSense was installed; its original backup is retained for
  manual recovery instead of being discarded.
- Lowered the native plugin's declared REFramework requirement to Plugin API
  1.10 after verifying that it uses only the unchanged ABI prefix. This allows
  the official REFramework v1.5.9.1 DMC5 build to remain installed.

## 1.7.0 — Steam Play autostart and hardware detection

- Removed the Steam Launch Options requirement. REFramework now loads the
  in-game plugin normally, and the plugin starts a hidden Bridge tied directly
  to the current DMC5 process. The bundled Launcher remains only as a backward-
  compatible manual fallback.
- Changed advanced-haptics endpoint selection to prefer the Windows USB
  hardware identity and four-channel format. Localized, renamed, and future
  Sony controller endpoints no longer depend on a particular friendly name;
  `AudioDeviceContains` remains an optional fallback.
- Added matching deterministic coverage to both the managed and native
  implementations.
- Corrected the PlayStation branch of the Settings controller diagram so
  Triangle, Square, Cross, and Circle highlights use the physical DualSense
  button centers at every resolution.
- Reduced the four face-button prompt symbols inside binding cards to keep all
  artwork cleanly inside the card frame.

## 1.6.0 — Native C++ primary release

- Reimplemented Launcher, Bridge, and the REFramework gameplay plugin in native
  C++ while preserving the controller behavior accepted in 1.5.5.
- Made the C++ package the recommended download; retained the self-contained C#
  implementation as a compatibility fallback.
- Removed the managed runtime from the primary package, reducing the ZIP from
  roughly 96 MiB to roughly 15 MiB.
- Kept Steam Input as the only gameplay-input owner. The native Bridge performs
  output only: adaptive triggers, LED color, and ordinary rumble through
  `SteamInput006`, plus four-channel WASAPI advanced haptics.
- Kept Launcher and Bridge session-scoped. No service, startup entry, resident
  watcher, direct HID input, virtual gamepad, ViGEm, or separate driver is used.
- Added pinned native dependency preparation, deterministic native builds,
  byte-identical archive checks, import auditing, and clean install/uninstall
  smoke coverage.
- Completed the pause-menu Display Controls diagram for every character and
  both input families while retaining DMC5's dynamic labels and assignments.
- Rebuilt the PlayStation prompt cells without stray edge pixels, refined the
  Settings stick markers, and matched the active Circle marker to the native
  Triangle/Cross/Square artwork.
- Added bilingual primary/fallback installation guidance.

## 1.5.5 — Stable Steam Input architecture

- Changed the runtime from controller-input emulation to an output-only design.
  Buttons, sticks, remapping, and touchpad input now stay entirely on DMC5's
  normal Steam Input path.
- Routed adaptive triggers, lightbar output, and ordinary rumble through Steam's
  DualSense output API and removed the old input interception layer.
- Made trigger placement depend only on the live DMC5 action bindings. Dante
  and Blue Rose effects are applied to L2/R2 only when the corresponding action
  is actually assigned there; face-button assignments leave the triggers free.
- Made DMC5's final motor output authoritative for ordinary combat rumble and
  retained the 180 ms watchdog that prevents vibration tails.
- Kept synthetic hit/damage impulses out of the default `Authentic` profile;
  they remain optional in `Enhanced`.
- Completed controls-menu and The Void alignment for D-pad directions, grouped
  D-pad, L1/L2/R1/R2, L3/R3, face buttons, Circle coloring, Options, and the
  full touchpad marker across resolution changes.
- Disabled heavy calibration/event logging by default and removed runtime log
  spam from normal sessions.

## 1.5.4 — Live binding and interface alignment

- Replaced event-based trigger-side guessing with direct reads of DMC5's active
  `AttackL` and `Special2` assignments.
- Applied remaps immediately without restarting the game.
- Moved controller art and active markers into a shared local coordinate system
  so resolution changes scale them together.
- Corrected compact PlayStation markers and expanded the touchpad marker.

## 1.5.3 — Deterministic gameplay event routing

- Filtered character-specific events to the active manual player, preventing
  AI, doubles, and inactive objects from creating false feedback.
- Distinguished Dante's Ebony and Ivory events from the live weapon selector.
- Replaced failing per-frame member lookups with stable inherited getters.
- Added safe in-place upgrades that preserve configuration and logs.

## 1.5.2 — Steam launch cleanup

- Hid the command window used by Steam to expand the `%command%` wrapper while
  retaining game-session ownership and Bridge cleanup.

## 1.5.1 — V detection and hidden runtime windows

- Corrected V detection in The Void.
- Changed Bridge to a windowless application and started REFramework with its
  panel closed.

## 1.5.0 — First public feature-complete package

- Established the session Launcher/Bridge/plugin structure, reversible
  installer, controller UI, adaptive-trigger profiles, character lightbar
  colors, ordinary rumble, and the supported advanced-haptics event set.
- Later releases replaced the original controller transport and should be used
  instead of this legacy package.
