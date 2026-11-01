# Mahod Civil Delivery 1.8.2 — local test package

This is a bugfix to the standalone test package previously supplied to the owner on `DEV-HAIM`. It does not publish main, the MahodAI production feed or the portal. The integrated office release procedure remains documented in the repository's `AGENTS.md`.

The BOQ launcher previously queued a CAD command from a tab selection event. Loading or restoring the palette's selection/focus could therefore open the BOQ window without an explicit action. The launcher now opens only on a left click on its header, or Enter/Space on the header. Selection changes, nested selectors, startup and hiding/re-showing the palette do not open it. The Project landing tab and the merged design remain in place. The guide documents the corrected activation behaviour.

## Verification

Real WPF regression tests exercise the production launcher with the production tab theme and a stub only at the CAD command boundary. They cover startup, restored selection/focus, repeated hide/show, direct header and header-child clicks, Enter/Space, ordinary navigation, nested selection/content and inactive launchers. A markup check verifies the actual palette's named tabs and absence of a selection-based launch handler. Native Civil 3D acceptance remains pending; these tests do not claim to reproduce or validate the native host lifecycle.

The focused suite passed 17/17 checks on Civil 3D 2026/net8 and 17/17 on 2027/net10. This includes 11 launcher cases, two recommendation display cases and four font/theme checks. Previous versioned local packages remain available separately.

## Packaging verification

Built from frozen source `a63a318a2d35f3a6e8bb3a9228d0ceab2803932e`. The reviewed guide identifies `1.8.2+a63a318a2d35` (15 pages, 401,971 bytes), with no guide-source edits after freeze. Both host builds completed sequentially; all four Plugin/Core assemblies report `1.8.2.0`. Embedded Core pairing, five asset pins and five font/licence pins per host passed.

The payload has exactly 25 expected manifest entries. The actual EXE embedded-payload self-test returned 0; Defender reported CLEAN for unchanged setup bytes. ZIP verification passed CRCs, exactly two entries and byte equality with the EXE and reviewed PDF; embedded and shipped guide hashes match. Private catalogues and project research are excluded.

Twenty installer lifecycle checks passed in a temporary ApplicationPlugins folder with a dedicated HKCU test key, including reinstall, refusal paths and byte-exact rollback with font files included. No live installation or Civil shutdown was performed. Native acceptance remains pending. Existing 1.8.1 EXE and ZIP remain available.

Artifacts: `MahodCivilDelivery/out/distribution`; payload: `MahodCivilDelivery/out/stage/payload`. Local evidence under `MahodCivilDelivery/out`: `SETUP_PAYLOAD_VERIFICATION_1.8.2.json`, `DEFENDER_SCAN_1.8.2.json`, `installer-1.8.2-build.log`, `installer-1.8.2-lifecycle.log`.

| Artifact | SHA-256 |
| --- | --- |
| `Mahod_Civil_Delivery_Setup_1.8.2.exe` | `80E230E31D012BA3CC482B4015580370745B1D147C1E2A7DD321873AE3A61BBE` |
| `Mahod_Civil_Delivery_1.8.2.zip` | `070FE5397A5CD3DF5122DE5836283721A4C765DEBB1559C46A1969B9D2308934` |
| Hebrew guide PDF | `A987C8B9D3165A21F9D03E10D0B3CE694D654301A98EE4657842D6396730042A` |
| Payload manifest | `C518B193BDA2D9D79DDC0C988ECA6D4FF0C53A327E772ECD0E2E65A1957C686F` |

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.8.2.pdf)
