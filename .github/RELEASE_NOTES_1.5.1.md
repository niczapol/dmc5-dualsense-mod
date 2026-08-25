# DMC5 DualSense Layer 1.5.1

This maintenance release improves V support and removes visible background mod
windows during normal gameplay.

- Fixes V detection in The Void so `app.PlayerV` is no longer reported as an
  unknown character.
- Builds the Bridge as a Windows GUI application, preventing a background
  command window from appearing.
- Starts REFramework with its panel closed while keeping `Insert` available for
  diagnostics.
- Updates `TEST-DualSense.cmd` so it waits correctly for the hidden Bridge.

For upgrades from 1.5.0, uninstall the previous release before installing this
archive.

ZIP SHA-256:
`ECCD3DFB765CABE968D0C1F35531DFA9C6E9CFC4811A69FB48AB7DA81E0FA426`
