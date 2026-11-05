# Mahod Civil Delivery 1.8.1 — local test package

This updates the standalone Delivery 1.8.0 package previously prepared for the owner, for local testing of the merged `dev-itay` interface on `DEV-HAIM`. It is not a MahodAI/Mahod Tools feed release. The integrated office release workflow remains documented in the repository's `AGENTS.md`; no main branch, production feed or portal is published by this package build.

The guide now describes the light interface, the Project landing tab and general BOQ launcher, the recommended-item badge, one-click automatic assignment, undo and quantity-unit behaviour. The product retains the merged design and BOQ implementation. Packaging carries the four exact Rubik font weights and their OFL licence beside each host DLL, and verifies the newly embedded green Mahod mark. Previous local installers remain available separately.

## Verification

Built from frozen source `88ac7491338a290b0672bbfc557980df38ab76d5`. The reviewed guide identifies `1.8.1+88ac7491338a` (15 pages, 400,008 bytes); its source was unchanged after the freeze. Civil 3D 2026/net8 and 2027/net10 built sequentially. All four Plugin/Core assemblies report `1.8.1.0`, and embedded Core pairing, five asset pins and runtime checks passed.

Eight font-staging tests passed, including missing or altered files, unexpected content and rejection before copying invalid input. They also passed in a fresh checkout created with `core.autocrlf=false`: the explicit license EOL attribute retained the pinned bytes. The actual payload contains exactly 25 manifest entries: six runtime files and five pinned font/licence files per host, plus guide, uninstaller and package declaration. All ten font/licence manifest hashes were verified against the fixed pins. The real EXE self-test checked every embedded payload entry and returned 0; Microsoft Defender reported CLEAN for unchanged EXE bytes. ZIP verification checked exactly EXE plus PDF, CRCs and byte equality with the sources; embedded and separately shipped PDF hashes match.

Twenty installer lifecycle tests passed against a temporary ApplicationPlugins folder and dedicated temporary HKCU test key. Fresh install, reinstall, refusal paths and injected rollback passed; recursive byte snapshots include Fonts. No installation was performed against the live bundle, and no LocalAppData catalogue/review files were modified by packaging. Prior versioned distribution artifacts remain present. Native Civil acceptance of this package remains separate.

Artifacts: `MahodCivilDelivery/out/distribution`; payload: `MahodCivilDelivery/out/stage/payload`. Local receipts: `SETUP_PAYLOAD_VERIFICATION_1.8.1.json`, `DEFENDER_SCAN_1.8.1.json`, `installer-1.8.1-build.log` and `installer-1.8.1-lifecycle.log` under `MahodCivilDelivery/out`.

| Artifact | SHA-256 |
| --- | --- |
| `Mahod_Civil_Delivery_Setup_1.8.1.exe` | `8EF38EBC622298EF8AE7F8AE26F426BAC5565F691EAFBC9303315C349EA4F9AB` |
| `Mahod_Civil_Delivery_1.8.1.zip` | `A1F806EB991DB5EA2874726FB29857B797B8B81667A08ADFD6B7513F08BB3FB3` |
| Hebrew guide PDF | `17B2A3D196F9F1290C7C3C2F966EE629B1BFEA1368AB55156A106803D06BC21E` |
| Payload manifest | `6F8A8AB83BDC58B3CE0954946E3C59F5BFA71882F0AD02E328B355AAB4A01B49` |

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.8.1.pdf)

The preceding design merge passed 162 checks; all 11 UI failures also occurred on one of its parents. Those results are recorded locally in `MahodCivilDelivery/out/dev-itay-merge-validation/validation.json` and are not a fully green product release gate or native acceptance of this package. Private catalogues, project archives and research output remain outside the package.
