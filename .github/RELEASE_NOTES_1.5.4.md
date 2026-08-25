## DMC5 DualSense Layer 1.5.4

This maintenance release makes adaptive-trigger routing follow the controller
layout selected inside DMC5.

- Reads `AttackL` and `Special2` directly from the active DMC5 key assignment.
- Enables Dante and Blue Rose weapon resistance only when `AttackL` is assigned
  to L2 or R2.
- Keeps both triggers free when Dante's gun action is assigned to a face button.
- Applies controller-layout changes immediately without restarting the game.
- Removes event-based remap inference, preventing weapon-switch presses from
  being mistaken for gun input.
- Replaces the remaining Xbox active-button markers in the compact controller
  diagram with correctly shaped and aligned PlayStation markers.
- Aligns L1/R1 and all four face-button highlights, and expands the touchpad
  highlight to cover the full touch surface.
- Keeps controller art and highlights in the same local coordinate system so
  they remain aligned when the display resolution changes.
- Adds deterministic regression coverage for face-button layouts, explicit
  L2/R2 remaps, live remapping, and Nero's independent action bindings.
