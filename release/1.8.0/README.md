# Mahod Civil Delivery 1.8.0

The simple bill-of-quantities workspace now offers **שייך אוטומטית**. One click assigns each eligible, visible, unmapped layer to its recommended item in the selected catalogue edition and category. The layer-name filter controls the batch scope. The item-search query belongs to the selected layer and is never applied to other layers. Existing assignments and excluded rows are preserved. Rows without a layer-topic recommendation, compatible quantity unit or positive quantity remain unmapped.

Manual selection uses the same ranked recommendation: one matching item receives a green background, bold text and a **מומלץ** badge beside its code. The badge remains readable when selection moves. Displaying or selecting a recommendation does not assign it; the manual or automatic assignment action does. A category or exact-code search alone cannot manufacture a layer recommendation for an unknown or unrelated layer. Auxiliary length layers are not automatically assigned through a category filter.

**בטל שיוך אוטומטי אחרון** reverses the last batch without removing subsequent manual decisions. Automatic origin survives saved-review restore and appears in exported Excel row status. Existing review files remain compatible. Catalogue prices and measurement rules are unchanged; no missing engineering specification or quantity is invented. No approver-name or confirmation form is added.

## Validation

Frozen product source: `2adc7e8c9934406ef43f375d6fd12a94cbe787d7`. The guide source was unchanged after that freeze; its PDF identifies `1.8.0+2adc7e8c9934` (14 pages, 358,161 bytes).

The owner reports 109 relevant Core tests and two WPF render checks passing on each host target (111 per host). Host-free tests cover recommendation/assignment consistency, chosen catalogue/category, scope, unit compatibility, preserving existing editions, planning without mutation, undo, old/new saved reviews and exported prices. WPF rendering checks cover the badge and readable green emphasis with and without selection.

A local saved-scan check, performed in memory only, planned 139 assignments out of 176 groups, leaving 37 unmatched in approximately 0.823 seconds. All planned units were compatible and prices came literally from the selected edition; the source review was unchanged. Receipt: `MahodCivilDelivery/out/boq-auto-assignment-20261101/saved-scan-batch-validation.json`. This is neither a recognition-accuracy result nor native Civil 3D acceptance.

## Installer artifacts

Civil 3D 2026/net8 and 2027/net10 were built sequentially. All four Plugin/Core assemblies report `1.8.0.0`; the standard build verified embedded Core pairing, runtime dependencies, pinned assets and host declarations. The actual EXE payload self-test returned 0, Microsoft Defender reported CLEAN for unchanged EXE bytes, and packaging verified ZIP CRCs and exact byte equality with the EXE and reviewed PDF. The shipped and embedded guide hashes match.

The payload manifest contains 15 expected files only: six DLL/dependency files per host, guide, uninstaller and PackageContents.xml. Resource declarations contain the public NTI book/rules/assets only; private KTAVKAM/FData snapshots and project research are excluded. No installation or Civil shutdown was performed during packaging; native acceptance remains separate.

Artifacts: `MahodCivilDelivery/out/distribution`. Installable payload: `MahodCivilDelivery/out/stage/payload`. Local verification receipts: `SETUP_PAYLOAD_VERIFICATION_1.8.0.json` and `DEFENDER_SCAN_1.8.0.json` under `MahodCivilDelivery/out`.

| Artifact | SHA-256 |
| --- | --- |
| `Mahod_Civil_Delivery_Setup_1.8.0.exe` | `765EECC08CCB9460D0E5037A62E763EC5A74BBDB71C99ECC73FA15089F01D9E6` |
| `Mahod_Civil_Delivery_1.8.0.zip` | `E5062F19FF3475DFCC93FB1955DCCF266AAFFAC8BE5182E7C3562B84E3287CF0` |
| Hebrew guide PDF | `1412827DEC915337BF603C2918B3F5CEAA2225174D2E4FFE10B13BEB3829D9C1` |
| Payload manifest | `6A3AF253F0C2A93599EB6BD707F64A48B3A4D7EBFC02BB566CF4CAB103AD3D52` |

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.8.0.pdf)

Private catalogue snapshots, project archives and generated research output remain local and are not distributed or added to Git. The installer targets only the per-user application bundle and requires Civil 3D to be closed.
