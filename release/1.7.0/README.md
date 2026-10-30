# Mahod Civil Delivery 1.7.0 — release candidate

## Scope

Expansion of the bill-of-quantities workflow across engineering domains, with cross-project learning and validation. The working target includes 50 projects; the final evidence, supported domains and limitations will be recorded after code freeze. Project count is not a guarantee of recognition accuracy or engineering approval. Suggestions require review by the engineer.

## Installation and preservation

The per-user installer targets `%APPDATA%\Autodesk\ApplicationPlugins\Mahod.CivilDelivery.bundle` for Civil 3D 2026 and 2027. It preserves catalogue snapshots, private learning data, projects and saved reviews under LocalAppData. Run normally, without administrator elevation, after saving work and closing Civil 3D. The previous bundle is retained for rollback; installation does not replace project data.

Private KTAVKAM/FData snapshots and project corpora are not part of the public product or Git repository. Local availability depends on files already present on the user's machine. Publisher and edition identities remain separate. Build/research output under `MahodCivilDelivery/out` must not be added to Git or copied into the public package.

## Build gate

No 1.7.0 installer has been built or accepted yet. Wait for an explicit code freeze and the reviewed version-matched PDF before building. This note does not change `MahodCivilDelivery/VERSION`.

The final gate must verify:

- Plugin and Core assembly versions and matching embedded Core identities for Civil 3D 2026/net8 and 2027/net10, plus expected runtime dependencies and assets.
- Actual installer EXE embedded-payload self-test, successful SHA-bound verification receipt and Microsoft Defender result for the same EXE bytes.
- Exact payload manifest allowlist, with no KTAVKAM/FData snapshots, project corpora or research artifacts; no private corpus embedded as an assembly resource.
- ZIP containing exactly the installer and the reviewed Hebrew guide, with CRC and byte equality checks; the shipped guide must match the embedded guide.
- Installation preservation of existing LocalAppData libraries and saved reviews. Live acceptance and installation are performed separately by the owner.

Expected outputs, once built: `MahodCivilDelivery/out/distribution/Mahod_Civil_Delivery_Setup_1.7.0.exe`, `Mahod_Civil_Delivery_1.7.0.zip`, the Hebrew PDF and `SHA256SUMS.txt`. Final source revision, hashes and acceptance results remain pending.

[Hebrew user guide](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.7.0.pdf) — pending reviewed build.
