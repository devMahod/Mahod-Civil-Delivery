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

Pending the final code and reviewed guide. Do not treat this release note as evidence of a completed build or live acceptance. Installer/ZIP hashes and verification results will be recorded after the final build gate.
