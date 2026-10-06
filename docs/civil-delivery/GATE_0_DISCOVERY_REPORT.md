# GATE 0 — DISCOVERY REPORT

**Version:** v1 (partial — file-level + estimate complete; Civil object-level pending Civil 3D session)
**Date:** 2026-08-18
**Branch:** `feature/civil-delivery` @ `7df13b589764a2e5ae8bfc495e0df90a3b63d390`
**Input Package v1 location:** `C:\Users\arthurf\Downloads\Cutz\Materials` (immutable source copies)
**Author:** Claude Code (Gate 0, read-only)

---

## 1. Repository baseline verification (plan §1.1)

| Check | Result |
|---|---|
| Plan-expected main SHA | `7df13b589764a2e5ae8bfc495e0df90a3b63d390` |
| `origin/main` after fetch | `7df13b5...` — **EXACT MATCH** (2026-08-16 "Merge test into main — stale agent-URL override no longer survives install") |
| Local checkout state before work | `main` @ `5ac09f0` (2026-04-20), clean, 162 commits behind, 0 ahead |
| Action taken | `git merge --ff-only origin/main` (safe fast-forward), then created + checked out `feature/civil-delivery` @ `7df13b5` |
| Commits/pushes made | **NONE** — working-tree files only, per Arthur's safety gates |

### §1.2 component re-verification at 7df13b5 (all present)

```
CreateSampleLinesTool      MahodAI.Civil3D.Plugin/Tools/Creation/CreateSampleLinesTool.cs
CreateSectionViewsTool     MahodAI.Civil3D.Plugin/Tools/Creation/CreateSectionViewsTool.cs
SampleCrossSectionsTool    MahodAI.Civil3D.Plugin/Tools/Corridor/SampleCrossSectionsTool.cs
CreateSampleLineGroupTool  MahodAI.Civil3D.Plugin/Tools/Creation/CreateSampleLineGroupTool.cs
AuditCrossSectionsTool     MahodAI.Civil3D.Plugin/Tools/CrossSection/AuditCrossSectionsTool.cs
DrawingSummaryExtractor    MahodAI.Civil3D.Plugin/DrawingSummaryExtractor.cs
SerialToolQueue            MahodAI.Civil3D.Plugin/Tools/SerialToolQueue.cs
ToolRegistry               MahodAI.Civil3D.Plugin/Tools/ToolRegistry.cs
Test projects              MahodAI.Core.Tests, MahodAI.Civil3D.Plugin.Tests
```

### Baseline test suite — GREEN (resolved 2026-08-18, Arthur-authorized SDK install)

.NET SDK 10.0.400 installed via winget per Arthur's EXECUTE_TO_PRODUCT_NOW directive. Baseline executed
locally on `feature/civil-delivery` @ `7df13b5`, Civil 2027 / net10.0 target:

```
Signal lane (--filter "Category!=RequiresCivil3D") — repo's own 100%-green criterion:
  MahodAI.Core.Tests:            369 passed / 0 failed / 0 skipped
  MahodAI.Civil3D.Plugin.Tests: 1045 passed / 0 failed / 0 skipped

Full unfiltered run (documented as red-outside-Civil by design in Tests/README.md):
  Plugin: 1045 passed / 32 env-dependent failures (Acdbmgd assembly load outside Civil) / 18 skipped
```

Matches plan §1.1 historical baseline (369 + 1045) exactly. Environment: AutoCAD 2026 + AutoCAD/Civil 3D 2027
installed; `Directory.Build.props` auto-detected 2027 (net10.0-windows).

---

## 2. Input Package v1 inventory (file level)

Source folder treated as immutable. SHA-256 recorded in `artifacts/discovery/discovery_manifest.json`.

| File | Size | DWG fmt | Role (candidate) |
|---|---|---|---|
| `CL.dwg` | 1,033,236 | AC1032 (2018+) | **Production CL input** (section locations) |
| `CL-CHECK.dwg` | 29,445,235 | AC1032 | CL verification/overlay drawing |
| `HW-CIVIL-CL.dwg` | 9,991,077 | AC1032 | Civil drawing with CL context |
| `6422-HW-CS.dwg` | 16,330,333 | AC1027 (2013) | **Current-project section evidence** (plan §4.2 priority) |
| `6422-HW-PR.dwg` | 1,440,978 | AC1032 | Profiles |
| `6422-HW-SM-MODEL-NATAZ-KETA1-MARAVI.dwg` | 1,668,058 | AC1027 | Surface model, segment 1 (western) |
| `6422-HW-SM-MODEL-NATAZ-KETA2-MIZRAHI.dwg` | 1,519,045 | AC1027 | Surface model, segment 2 (eastern) |
| `UT-3D.dwg` | 1,015,595 | AC1032 | Utilities 3D |
| `UT-MAARVI.dwg` | 9,429,562 | AC1027 | Utilities, western segment |
| `ACAD-HW-CL-M34.dwg` | **0 bytes** | — | **CORRUPT/EMPTY — see F-001** |
| `VSC-UT-ALL-00NTZ_BG-PD-5001-01.pdf` | 2,348,334 | PDF 1.x | Ben-Gurion Golden — visual evidence ONLY |
| `דוגמא שיפוט 2 כבישים ותנועה.xlsx` | 13,163 | xlsx | Estimate Golden — Judgment-2 example |
| `מחירון-לעבודות-עירוניות-ומסופים-ושבילי-אופניים-082025.xlsx` | 2,647,426 | xlsx | NTI urban price book 08/2025 |

DWG formats are AC1027/AC1032 — both loadable by Civil 3D 2027. No blocker.

---

## 3. Findings

### F-001 — `ACAD-HW-CL-M34.dwg` is a zero-byte file
SHA-256 = `e3b0c44...` (the empty-string hash). Likely a failed copy. **Not a Gate-A blocker** (`CL.dwg` is the
production CL input). Action: request exact re-copy of this one file from `P:\data\6422` if it turns out to be needed.

### F-002 — .NET SDK 10.0.3xx missing → baseline suite blocked
See §1 above. `SHR-DOTNET-SDK-MISSING`, severity BLOCKED for Phase A0+ coding; does not block Gate-0 discovery.

### F-003 — Judgment-2 Golden prices are NOT from the 08/2025 price book (OQ-008 evidence)
All 31 priced items in the Judgment-2 example were cross-checked against the 08/2025 urban price book:

```
0 identical / 31 different / 0 missing codes
```

- Every catalog code exists in the 08/2025 book, but **every price differs** (e.g. `U51.03.0010`: J2=140 vs 08/2025=170.128; `U51.04.0080`: J2=38 vs 45.11).
- Per-item ratios are inconsistent (1.016 … 1.215) → **no global conversion factor exists**, exactly as plan §4.7 warns.
- Two items priced in J2 have `-` (missing price) in 08/2025: `U40.02.2500` (J2=30000), `U51.32.0800` (J2=426).
- J2 prices are integers → older/other price-book snapshot or rounded source.

**Conclusion:** `GOLDEN_PRICE_SOURCE_MISSING` until the actual snapshot used by Judgment-2 is identified. Do not
price-reproduce the Golden against 08/2025.

### F-004 — 0.90 adjustment candidate: comprehensive quantitative evidence (OQ-009)
**All 31 of 31 quantities** in the Judgment-2 example are exactly `X × 0.9` where X has ≤1 decimal
(5.4=6×0.9, 3.6=4×0.9, 9064.35=10071.5×0.9, 28520.1=31689×0.9, …).

Status remains `UNVERIFIED_PROJECT_RULE` per plan §4.6/§8.8 — meaning, direction, scope and approval source
still unknown. Do NOT auto-apply. `Quantity_Cost` material (not yet in Input Package v1) is the priority source
to explain it.

### F-005 — Price book structure decoded (estimate inventory)
- Single sheet `מחירון עירוני (נת"צ עירונים מסו...`, 9,269 rows.
- Title rows 1–4: "פרסום אוגוסט 2025, מחירי יולי 2025", iroads.co.il reference.
- Header at row 100: `מק"ט | תיאור | יח' מידה | מחיר`; data from row 103.
- **9,082 U-prefixed catalog items** (`U02…U57` chapters observed).
- `-` used for missing price (e.g. `U02.01.0055`) → confirms plan §8.9: blank/`-` = `MISSING_PRICE`, never zero.
- Chapter-header rows carry unit `הערה` with price 0 — must be excluded from item parsing.

### F-006 — Judgment-2 workbook structure decoded
- Single sheet `יצוא השוואת הצעות` (A1:H46).
- Columns: `מספר | מס' קטלוגי | תאור | יח' מידה | כמות | מחיר | סה"כ` (+ empty col H); an `אומדן` super-header above price/total.
- Hierarchy encoded in the numbering: `00.00.00.0000` root → `01.00.00.0000` segment ("מקטע 01 - כביש 4 עד התחיה") → `01.40/01.51` chapters → sub-chapters → leaf items with catalog code `Uxx.xx.xxxx`.
- Units observed: יח', מ"ר, מטר, מ"ק, קומפ'.
- Totals are plain `כמות × מחיר` (verified numerically, e.g. 9064.35×14=126,900.9).

### F-007 — Golden PDF provenance and composition (visual evidence only)
- 1 page, plotted 2024-09-04 from **Autodesk Civil 3D 2023 (English)**, `pdfplot16.hdi`, sheet ≈ 2580×3685 pt (large format).
- Title block (Hebrew): project "נת"צ שדרות בן גוריון", sheet "חתכים טיפוסיים של מערכות", Netivei Israel logo.
- Composition: ~10 typical-section groups in a ~3-column grid; each group is a **pair** of stacked views (upper: section; lower: same section with subsurface utility systems — blue pipe profiles + colored vertical utility markers).
- Bottom-right: **plan/index map** of the route with marked section locations (red/black CL ticks) — confirms plan §7.11 layout scope includes a location index.
- Object-level questions (native SectionView vs drafted blocks, styles, bands) CANNOT be answered from the PDF — they require the Ben-Gurion source DWG (OQ-001/OQ-002/OQ-005/OQ-006 remain open).

---

## 4. Civil / CL object-level inventory — PENDING

Requires a Civil 3D runtime session (Civil 3D was not running during this pass; `civil3d-mcp` COM bridge is
available once it is). Next micro-task:

1. copy the 9 non-empty DWGs to a writable fixture area (`fixtures/civil-delivery/modiin-6422/`, hashes recorded);
2. open copies read-only in Civil 3D 2027;
3. produce `cl_inventory.json` per plan §4.4 (entities, layers, WCS endpoints, alignment intersections, labels);
4. produce `civil_inventory.json` per plan §4.3 (alignments, corridors, surfaces, styles, XREF graph, …);
5. answer Discovery questions 1–10 of §4.4 and select the 3 Gate-A CL cases.

Stub artifacts with status `PENDING_CIVIL_RUNTIME` are in `artifacts/discovery/`.

---

## 5. Gate-A readiness vs plan §4.9

| Condition | Status |
|---|---|
| 6422 files loadable in target Civil runtime | UNTESTED (formats OK; Civil session pending) |
| CL geometry interpretable | UNTESTED (CL.dwg present, 1 MB, plausible) |
| Target alignment identifiable for 3 cases | UNTESTED |
| Ben-Gurion source DWG | MISSING — **not a Gate-A blocker** by plan §4.9 |
| LandQ verified | NO — blocks only Gate-B LandQ portion |
| Estimate price source | MISSING (F-003) — not a Gate-A blocker |

**No true Gate-A blocker identified so far.** The two real blockers for *starting implementation* are
operational: (a) .NET 10 SDK for the baseline suite (F-002), (b) a Civil 3D session for object-level discovery.
