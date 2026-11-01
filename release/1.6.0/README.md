# Mahod Civil Delivery 1.6.0

## Changes

- Offline loading of private local KTAVKAM catalogue snapshots from `%LOCALAPPDATA%\MahodCivilDelivery\price-books\ktavkam`. Each publisher and edition remains a separate price book identity.
- Expanded multilingual interpretation of infrastructure layer descriptions and aliases, safer handling of auxiliary layers, and BOQ usage context hints. Recommendations require the engineer's review and approval.
- The private catalogue corpus and project research data are not distributed with the product or committed to the public repository. Local availability depends on the user's own snapshot files; catalogue group/item counts are not a product guarantee.

## Installation and data

The installer targets the current user's `%APPDATA%\Autodesk\ApplicationPlugins\Mahod.CivilDelivery.bundle` for Civil 3D 2026 and 2027. Existing catalogue snapshots under LocalAppData, project data and saved review decisions are preserved. Close Civil 3D before installing and run the installer normally, without administrator elevation.

The distribution ZIP contains exactly the installer and the version-matched Hebrew guide. Build output stays under `MahodCivilDelivery/out`; private project research under that directory must not be added to the package or Git.

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.6.0.pdf)

## Build verification

Built from source revision `c485875`. Both Civil 3D 2026 and 2027 builds passed the payload identity, dependency, asset and version checks. The real setup EXE's embedded-payload self-test returned 0; Microsoft Defender reported CLEAN for the unchanged setup bytes. Packaging verified exactly two ZIP entries, their CRCs and byte identity with the installer and reviewed guide.

The payload manifest contains 15 expected bundle files only: six runtime/dependency files per host, the guide, uninstaller and PackageContents.xml. No private KTAVKAM snapshots or project research files are included. This verification does not replace live Civil 3D acceptance.

| Artifact | SHA-256 |
| --- | --- |
| `Mahod_Civil_Delivery_Setup_1.6.0.exe` | `722EF8EA60838F1A0FD3C4D4D8CC1B789E1E80E12C5BE771B73EA91DA070C149` |
| `Mahod_Civil_Delivery_1.6.0.zip` | `A08E06B7D99F213A839779B113F631D345EFBE8B1A85F3FCC65743CCF0DDFED5` |
| Hebrew guide PDF | `1BB473D83709267D56308508291BC26003FA146EA6BF404E8DFCC9FD2278FCA8` |
| Payload manifest | `B95333F4AF2D8865372424BB1D577E26A08398508CD8B54D0F6A0C53D9E8BAEC` |

Local receipts: `MahodCivilDelivery/out/SETUP_PAYLOAD_VERIFICATION_1.6.0.json` and `MahodCivilDelivery/out/DEFENDER_SCAN_1.6.0.json`. Distribution artifacts and receipts remain outside Git.

The focused infrastructure/catalogue/review regression suite passed 111 tests on each target (2026/net8 and 2027/net10). Live acceptance in Civil 3D 2027 verified the installed Plugin/Core pair, grouped local catalogues, restoration of existing choices and Hebrew search intent overriding an inferred layer topic. The original drawing and saved choices were unchanged. No new engineering item assignments were made during acceptance.
