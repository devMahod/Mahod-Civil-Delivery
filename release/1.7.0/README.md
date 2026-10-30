# Mahod Civil Delivery 1.7.0 — release candidate

## Scope

Delivery opens on the project tab. Selecting the dark palette's bill-of-quantities tab opens the simple white workspace for all domains. A focus dropdown offers demolitions, paving, earthworks, concrete, landscape and infrastructure, followed by the selected publisher's chapter/subchapter folders. Changing focus filters catalogue items without dropping drawing rows. Existing infrastructure command names remain compatible.

The workspace supports native curve lengths, valid closed-curve and Hatch areas, Solid3d volumes and block counts grouped by effective block name and layer. Item suggestions must match the selected measurement unit. Units without a native quantity require a positive quantity entered by the engineer; that origin is recorded in the export. Partial exports retain unmapped rows without inventing prices. No approver-name form is required.

Local archive context was expanded from 20 to 50 distinct projects: the additional 30 contributed 6,102 BOQ rows and 86 readable DWGs. Across both samples, 146 DWGs were read without changing their source bytes. The runtime retrieval prior contains 4,923 valid positive-quantity description/unit examples from 50 projects. It provides a bounded ranking hint, not approved layer-to-item training pairs. Project count is not a guarantee of recognition accuracy or engineering approval. Suggestions require review by the engineer.

Seventeen locally available catalogue editions remain separated by publisher and edition. Their prices were not modified by the enrichment. Only the existing public NTI catalogue is bundled for distribution; private catalogue snapshots and archive context remain on the authorized machine.

## Functional validation and limits

- Final focused suites: 140 passed for 2026/net8 and 140 passed for 2027/net10.
- Portable Estimate suite: 1,979 passed, two skipped. Four other historical native-record tests depend on unavailable local preflight artifacts and were excluded from this portable run. The final added Hatch recommendation regression is covered by both focused runs.
- Native AutoCAD 2027 read/measure/export/reopen checks passed for a 100 m2 closed paving outline, a 96 m2 Hatch with a hole, two identical blocks, a 24 m3 solid, invalid self-crossing area rejection, and a rotated/scaled XREF Hatch. Metre and millimetre inputs preserved length/area/volume/count scaling in output DWGs.
- The archive evaluator covered 6,477 eligible layer/source groups. Name-based domain coverage rose from 609 to 1,065 groups after adding general domains and observed aliases. This is coverage, not a measured accuracy score; exact approved layer/item ground truth is unavailable. The 1,005 sampled suggestions retained compatible units.
- This workspace scans the full model and loaded sources; it does not apply polygon clipping. It does not derive mass, excavation depth or volume from a two-dimensional line. Unloaded sources, unsupported geometry and block arrays are reported rather than approximated. Counted symbols are exported as location markers, with their original block definitions retained in the source DWG.

## Installation and preservation

The per-user installer targets `%APPDATA%\Autodesk\ApplicationPlugins\Mahod.CivilDelivery.bundle` for Civil 3D 2026 and 2027. It preserves catalogue snapshots, private learning data, projects and saved reviews under LocalAppData. Run normally, without administrator elevation, after saving work and closing Civil 3D. The previous bundle is retained for rollback; installation does not replace project data.

Private KTAVKAM/FData snapshots and project corpora are not part of the public product or Git repository. Local availability depends on files already present on the user's machine. Publisher and edition identities remain separate. Build/research output under `MahodCivilDelivery/out` must not be added to Git or copied into the public package.

## Build verification

Built from frozen source `6530a277c89ac3f27fc053d5c7430d9238f322ef` with the reviewed 14-page Hebrew guide (351,628 bytes). Civil 3D 2026/net8 and 2027/net10 were built sequentially. All four Plugin/Core assemblies report `1.7.0.0`; embedded Core pairing, runtime dependencies, assets and host component declarations passed the standard build gate.

Completed artifact checks:

- Plugin and Core assembly versions and matching embedded Core identities for Civil 3D 2026/net8 and 2027/net10, plus expected runtime dependencies and assets.
- Actual installer EXE embedded-payload self-test, successful SHA-bound verification receipt and Microsoft Defender result for the same EXE bytes.
- Exact payload manifest allowlist, with no KTAVKAM/FData snapshots, project corpora or research artifacts; no private corpus embedded as an assembly resource.
- ZIP containing exactly the installer and the reviewed Hebrew guide, with CRC and byte equality checks; the shipped guide must match the embedded guide.
- The installer continues to target only the application bundle; LocalAppData libraries and reviews are outside its write targets. No installation was run during packaging. Installation preservation and live acceptance are performed separately by the owner.

The actual EXE self-test returned 0, Defender reported CLEAN for unchanged setup bytes, and ZIP CRC/byte comparison passed. The manifest contains exactly 15 expected bundle files; assembly resource declarations include only the existing public price book/rules/assets and no private KTAVKAM/FData/project corpus.

Artifacts are in `MahodCivilDelivery/out/distribution`; installable payload is in `MahodCivilDelivery/out/stage/payload`.

| Artifact | SHA-256 |
| --- | --- |
| `Mahod_Civil_Delivery_Setup_1.7.0.exe` | `15AC00A787633F9E15B3356A7C394970C25CAC8310E95AD09F2009095D96EF95` |
| `Mahod_Civil_Delivery_1.7.0.zip` | `DDE0E30FDFD549E4747179E841A6AB99F2F8AD3B36BE21EE5816AF1C4E288A9D` |
| Hebrew guide PDF | `58C01A15F65AEF5DA63B6B807A41BE110683FB55CC0F2D64428125ABA78C1B96` |
| Payload manifest | `BD40C1E40395803AA06109C040C51E7EA9015E6E5C81C75E47D027FD19FFEB81` |

Receipts remain local under `MahodCivilDelivery/out`: `SETUP_PAYLOAD_VERIFICATION_1.7.0.json`, `DEFENDER_SCAN_1.7.0.json` and `installer-1.7.0-build.log`. Native UI acceptance is pending separately.

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.7.0.pdf)
