# Mahod Civil Delivery 1.4.2

Built on 7 October 2026 from source `b3995512094ccd913ffbdb3b156eeea23595fb51` (`feat/impact-usage`) by `build-release-1.4.2.ps1`, run through the owner's `with-usage-key.ps1`. **Not published**: putting it on the portal is the owner's decision.

## Changes from the b41 source

**Usage counts for Mahod Impact (the only code change).** The tool reports to Mahod AI (`https://dev.mahodeng.co.il/api/v1/usage`) with MahodAI's own recorder, `Utilities/MahodUsage.cs`. It is a byte-for-byte copy of the MahodAI plugin's recorder at `a259bed`.

- **Actions:** each is timed and reported as `completed` or `failed` (never `cancelled`).
  - `civildelivery_plan`, `_preview`, `_apply` and `_verify` (SectionsWorkflowService).
  - `civildelivery_estimate`, the early estimate's quantity scan.
  - `civildelivery_open`, `_setup` and `_sections`, for the typed `MCD_CIVIL_DELIVERY`, `MCD_SETUP` and `MCD_SECTIONS`.
- **Units:** one `section_delivery` per section read back as Verified. Its key is the drawing `FingerprintGuid` + `|section:` + the section's logical key, hashed on the PC.
- **Doors:** `palette`, or `standalone` inside a typed `MCD_*` command. The door is never decided by assembly name.
- **Never sent:** the drawing, file name, path, layers, quantities, prices, the Windows user or the PC.
  - The request authenticates with this PC's MahodAI installation key when there is one. Otherwise it uses the products' key, baked in at build time.
  - Records wait in `%LOCALAPPDATA%\Mahod\cad-usage` and upload at most every 15 minutes per Civil session.
  - `MAHOD_USAGE_OFF=1` turns reporting off.

**Version and guide**

- The version is 1.4.2, in `MahodCivilDelivery/VERSION` and `SectionPlanService.ToolVersion`.
  - 1.4.1 is reserved by `round-1.4.1/PLAN_1.4.1_HE.md` for the release that closes the round's five requests.
  - That release must therefore be numbered 1.4.3 or later.
- The guide is a new Hebrew user guide written for 1.4.2 (`guide-source/`), by the owner's decision of 7 October 2026.
  - The 1.4.0 guide's source and screenshots are not in this repository.
  - `nataly/guide/GUIDE_HE.html` is the 1.2.x guide.

**Beware: b41 is newer than the build the portal ships as 1.4.0.**

- 1.4.2 therefore also carries the round-1.4.1 work: XREF categories, Hebrew item descriptions, SIMUN mapping, the CL picker, known price books and the work-domain choice.
- That work is not yet fully live-accepted (`round-1.4.1/STATUS_1.4.1_HE.md`). The guide says so in chapter 8.

## Artifacts

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `Mahod_Civil_Delivery_1.4.2.zip` (portal package) | 71673279 | `A132C6E0665710FCB014F7401DF6CBF59C64F7E018656C1FC134DBE8E9C1EA56` |
| `Mahod_Civil_Delivery_Setup_1.4.2.exe` | 77129739 | `AD1D1055FE14D4084E224E5B29960BEF1959407317B250FDCDEC6039DBC5E97B` |
| [`MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf`](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf) (9 pages) | 296658 | `5A14A476C92E4188CC51470CE24CACB1107A29F7A23AC2AE83D0114CB14E2156` |

- **The ZIP** holds exactly the setup and `Mahod_Civil_Delivery_מדריך_למשתמשת.pdf`. Each is byte-identical to the rows above. This is the two-file rule of `nataly/build-nataly.ps1`.
  - The portal's description of 1.4.0 says its ZIP also carried a folder `מחירון` with `nti-2026.xlsx`. That file is not in this repository, so it is not in this package; the guide tells engineers to get the price book from Arthur.
- **The setup and ZIP are not in git:** they carry the baked key, and this repository is public.
- **[release-report-1.4.2.json](release-report-1.4.2.json)** gives the plugin and Core SHA-256 for each host, the assembly versions, and the endpoint and key checks (booleans only). It also records the installer lifecycle result.

## Hosts and installation

- **Hosts:** Civil 3D 2026 (.NET 8) and 2027 (.NET 10), the same hosts as 1.4.0.
- **Install:** per user, no administrator rights, into `%APPDATA%\Autodesk\ApplicationPlugins\Mahod.CivilDelivery.bundle`.
- **Upgrade:** setup upgrades 1.4.0 in place. The previous version is kept as `Mahod.CivilDelivery.previous`, and project data is untouched.
- **Refusals:** setup refuses while Civil 3D or AutoCAD runs, when run elevated, and when neither Civil 3D 2026 nor 2027 is present.
- **Beside MahodAI:** the tool is a separate assembly (`Mahod.CivilDelivery`) with `MCD_*` commands, so it loads beside MahodAI. It hides MahodAI's old Civil Delivery button.

## Validation (6–7 October 2026)

**Test lanes** (filters of `round-1.4.1/chain_b18.sh`, run with `MAHOD_USAGE_OFF=1`):

| Lane | 1.4.2 (pass / fail / skip) | 9430d5e (pass / fail / skip) |
|---|---|---|
| Core | 3534 / 42 / 2 | 3534 / 42 / 2 |
| Plugin | 3608 / 12 / 14 | 3598 / 12 / 14 |
| Standalone Core | 3247 / 42 / 2 | 3247 / 42 / 2 |
| Standalone | 2543 / 12 / 14 | 2530 / 12 / 14 |

- The failing set is identical on both commits: fixture SHA-256 mismatches and native runs that are not on this PC.
- The new usage tests pass: +10 in Plugin, +13 in Standalone.

**Release build:**

- `build-setup.ps1` checks passed: payload verification, the setup EXE payload self-test, and Defender (clean).
- Both hosts are assembly 1.4.2.0, with the usage endpoint present and the key baked in.
- `Test-Installer.ps1`: 20 passed, 0 failed.
- The ZIP held exactly 2 entries, and Defender found it clean.

**Offline end-to-end check** of this release's 2027 DLL, run without AutoCAD against a loopback listener:

- Reported `civildelivery` actions with real durations, in the `palette` and `standalone` doors, including a `failed` step.
- Reported 2 `section_delivery` units out of 3 verify records, of which 2 were Verified.
- The body held only `v,sid,events`, and no user, PC, drawing or section text.
- The baked key equals the key file (checked as a boolean).

**Guide:**

- Rendered with Edge headless.
- All 9 pages were rendered to PNG and inspected: Hebrew right-to-left, commands and paths not reversed, nothing cut off.
- Mixed-direction lines were also checked by extracting their visual character order.
- Every command, button label and path in the guide was checked against this source.

**Not run:** installing the setup; any run inside Civil 3D 2026/2027; an upload to the real Mahod AI route from this build.

## Rebuild

```powershell
# guide (project venv; never bare python) - pass the commit the installer is built from
<venv>\python.exe release\1.4.2\guide-source\build_guide.py <commit> --out <dir>
# setup + gates + lifecycle test + portal ZIP, with the key in that child process only
powershell -NoProfile -Command "& '<with-usage-key.ps1>' -WorkDir '<repo>' -Script '<repo>\release\1.4.2\build-release-1.4.2.ps1' -Arguments @('<dir>\MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf','<its SHA-256>','<out dir>')"
```
