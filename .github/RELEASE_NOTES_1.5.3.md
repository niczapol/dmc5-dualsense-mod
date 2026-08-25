## DMC5 DualSense Layer 1.5.3

This maintenance release makes gameplay event routing deterministic and removes
REFramework log spam observed during long sessions.

- Accepts Nero, Dante, and Vergil gameplay events only from the active manual player.
- Requires three consistent AttackL observations before choosing L2 or R2, then locks
  the mapping for the session so weapon-switch presses cannot move the resistance.
- Distinguishes Ebony from Ivory using the live selector update around `createShell`.
- Reads inherited player state through RE Engine getter methods, eliminating the
  per-frame `Member not found` messages for `manualPlayer`, `hp`, and `maxHp`.
- Logs adaptive mappings only when a mapping actually changes.
- Adds regression coverage for incidental, alternating, isolated, and locked mappings.
- Upgrades an existing installation automatically while preserving configuration and
  diagnostic logs; unexpected unowned files still stop the installer safely.

Install over an existing release with `Install-DMC5DualSense.cmd`; personal settings
and backups are preserved.
