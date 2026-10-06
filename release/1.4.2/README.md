# Mahod Civil Delivery 1.4.2

**Status: source ready, release artifacts NOT built.** The guide input is missing (see "Guide"). Nothing here is published.

1.4.2 = the 1.4.0 b41 developer source (`9430d5e`) + usage counts for Mahod Impact (`636e6c7`, `dea0b66` on `feat/impact-usage`).

## Changes from the b41 source

- **Usage counts for Mahod Impact (the only code change).** The tool reports to Mahod AI (`https://dev.mahodeng.co.il/api/v1/usage`) with MahodAI's own recorder, `MahodAI.Civil3D.Plugin/Utilities/MahodUsage.cs`, byte for byte (MahodAI plugin `a259bed`):
  - Actions, timed, `completed` or `failed` (never `cancelled`): `civildelivery_plan`, `_preview`, `_apply`, `_verify` (SectionsWorkflowService), `civildelivery_estimate` (the early estimate's quantity scan), and the typed `MCD_CIVIL_DELIVERY` / `MCD_SETUP` / `MCD_SECTIONS` as `civildelivery_open` / `_setup` / `_sections`.
  - Units: one `section_delivery` per section read back Verified. The key is drawing `FingerprintGuid` + `|section:` + the logical key, hashed on the PC. This is the same scheme MahodAI uses for the Civil Delivery it carries.
  - Doors: `palette`, or `standalone` inside a typed `MCD_*` command. The door is never decided by assembly name.
- **Never in a record:** the drawing, file name, path, layers, quantities, prices, the Windows user or a PC id. The HTTP request authenticates with this PC's MahodAI installation key when there is one, else with the products' key baked in at build time.
- **How it sends:** queue in `%LOCALAPPDATA%\Mahod\cad-usage`; upload on a background task at most every 15 minutes per Civil session. `MAHOD_USAGE_OFF=1` turns it off.
- Version 1.4.2 in `MahodCivilDelivery/VERSION` and `SectionPlanService.ToolVersion`.
  - 1.4.1 is reserved by `round-1.4.1/PLAN_1.4.1_HE.md` for the release that closes the round's five requests. That release must now be numbered 1.4.3 or later, or it would install as a downgrade.
- **Beware: b41 is not the build the portal ships as 1.4.0.** The portal ZIP was uploaded on 4 October; b41 was built on 6 October with the round-1.4.1 work. That work (XREF categories, Hebrew item descriptions, SIMUN mapping, CL picker and more) is not yet live-accepted (`round-1.4.1/STATUS_1.4.1_HE.md`). Shipping 1.4.2 therefore ships that work as well. Publishing is the owner's decision.

## Guide

- `nataly/guide/GUIDE_HE.html` has the new section "מה נשלח ל-Mahod AI" (chapter 7). That template cannot produce the 1.4.x guide:
  - its screenshots (`nataly/guide/img/`) are not in this repository;
  - it still describes the 1.2.x MahodAI-fork install (`MHD_*`, `MahodAI.bundle`);
  - `release/release.json` pins 1.2.91.
- The engineers' 1.4.0 guide (`MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.0.pdf`, SHA-256 `8816E962…F1D4`, README_DEVELOPERS_HE.md) is not in this repository either.
- `build_guide_142.py` makes the 1.4.2 guide from that PDF: the 1.4.0 PDF unchanged, plus one page "תוספת לגרסה 1.4.2". The page is rendered by Edge from the same section and CSS.
  - It was checked on a stand-in PDF: the page renders correctly in Hebrew RTL.
  - Limitation: the cover still says 1.4.0.

## Building (once the 1.4.0 guide PDF is available)

```powershell
# 1. guide (project venv; never bare python)
<venv>\python.exe release\1.4.2\build_guide_142.py --base <MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.0.pdf> --out <dir>
# 2. setup + gates + lifecycle test + portal ZIP, with the key in this process only
powershell -NoProfile -Command "& '<with-usage-key.ps1>' -WorkDir '<repo>' -Script '<repo>\release\1.4.2\build-release-1.4.2.ps1' -Arguments @('<dir>\MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf','<its SHA-256>','<out dir>')"
```

`build-release-1.4.2.ps1` does the following:

1. Runs `MahodCivilDelivery/installer/build-setup.ps1` unchanged. This builds both hosts (Civil 3D 2026 net8 and 2027 net10), checks the payload, writes the MANIFEST, runs the EXE payload self-test and the Defender scan.
2. Checks that each host's DLLs are 1.4.2.0 and carry the endpoint and the key. The key check is a boolean and the key is never printed.
3. Runs `Test-Installer.ps1`.
4. Zips exactly the setup and `Mahod_Civil_Delivery_מדריך_למשתמשת.pdf` (named as `nataly/build-nataly.ps1` names it), runs a Defender scan on the ZIP, and writes the sidecars and `release-report-1.4.2.json`.

Built DLLs, setups and ZIPs carry the key: never commit them (this repository is public).

## Validation done (6 October 2026)

**Repository lanes** (`round-1.4.1/chain_b18.sh` filters, `MAHOD_USAGE_OFF=1`). Results are identical to `9430d5e` on this machine apart from the new tests:

| Lane | 1.4.2 (pass / fail / skip) | 9430d5e (pass / fail / skip) |
|---|---|---|
| Core | 3534 / 42 / 2 | 3534 / 42 / 2 |
| Plugin | 3608 / 12 / 14 | 3598 / 12 / 14 |
| Standalone Core | 3247 / 42 / 2 | 3247 / 42 / 2 |
| Standalone | 2543 / 12 / 14 | 2530 / 12 / 14 |

- The new tests: 10 in Plugin and 13 in Standalone (the same 10 plus 3).
- The failing tests are the same set on both commits. They are fixture SHA-256 mismatches and recorded native runs that are not on this PC; they are not caused by this change.
- The 2026 build (`MahodCivilDeliveryGuideEnabled`) succeeds.

**Pipeline** (through `with-usage-key.ps1`, with a stand-in guide; those artifacts were deleted):

- Both hosts are assembly 1.4.2.0, with the usage endpoint present and the key baked.
- Payload verification, the setup self-test and Defender passed.
- `Test-Installer.ps1`: 20 passed, 0 failed.
- The ZIP held exactly the 2 expected entries.

**Offline end-to-end check** of the built 2027 DLL, run without AutoCAD against a loopback listener:

- Records `civildelivery` actions with real durations, the `palette` and `standalone` doors, and a disposed step sent as `failed`.
- Records 2 `section_delivery` units out of 3 verify records, of which 2 were Verified.
- The envelope holds only `v,sid,events`. No user, PC, drawing or section text was found in the bodies.
- The baked key equals the owner's key file (checked as a boolean).

**Not run:** installing the setup, or any run inside Civil 3D 2026/2027; an upload to the real Mahod AI route from this build.
