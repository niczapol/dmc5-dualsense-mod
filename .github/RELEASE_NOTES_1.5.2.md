# DMC5 DualSense Layer 1.5.2

This maintenance release removes the remaining command window when DMC5 is
started through Steam.

- Detects and hides the parent `cmd.exe` window Steam uses to expand the
  `%command%` launch wrapper.
- Keeps the launcher attached to the DMC5 session so the Bridge still shuts down
  cleanly after the game exits.
- Includes the V detection, hidden REFramework panel, and windowless Bridge
  improvements from v1.5.1.

ZIP SHA-256:
`01062B29B4DE9D025A058CA48D1C2E9EBD9C80D5CFE997933B4C40AD9ADBB92A`
