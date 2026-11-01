# Mahod Civil Delivery 1.8.2 — local test package

This is a bugfix to the standalone test package previously supplied to the owner on `DEV-HAIM`. It does not publish main, the MahodAI production feed or the portal. The integrated office release procedure remains documented in the repository's `AGENTS.md`.

The BOQ launcher previously queued a CAD command from a tab selection event. Loading or restoring the palette's selection/focus could therefore open the BOQ window without an explicit action. The launcher now opens only on a left click on its header, or Enter/Space on the header. Selection changes, nested selectors, startup and hiding/re-showing the palette do not open it. The Project landing tab and the merged design remain in place. The guide documents the corrected activation behaviour.

## Verification

Real WPF regression tests exercise the production launcher with the production tab theme and a stub only at the CAD command boundary. They cover startup, restored selection/focus, repeated hide/show, direct header and header-child clicks, Enter/Space, ordinary navigation, nested selection/content and inactive launchers. A markup check verifies the actual palette's named tabs and absence of a selection-based launch handler. Native Civil 3D acceptance remains pending; these tests do not claim to reproduce or validate the native host lifecycle.

The focused suite passed 17/17 checks on Civil 3D 2026/net8 and 17/17 on 2027/net10. This includes 11 launcher cases, two recommendation display cases and four font/theme checks. Build and packaging receipts will be recorded here after the source freeze and guide review. Previous versioned local packages remain available separately.
