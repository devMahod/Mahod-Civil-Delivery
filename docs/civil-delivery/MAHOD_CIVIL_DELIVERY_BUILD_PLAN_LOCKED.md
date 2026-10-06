# MAHOD CIVIL DELIVERY — LOCKED BUILD PLAN

**File:** `MAHOD_CIVIL_DELIVERY_BUILD_PLAN_LOCKED.md`  
**Status:** LOCKED SOURCE OF TRUTH  
**Owner:** Mahod Engineering  
**Primary implementation repository:** `devMahod/MahodAI-Plugin`  
**First production Project Profile:** `6422` — Modiin BRT / early design  
**Product name:** **Mahod Civil Delivery**  
**Primary direct commands:** `MHD_SECTIONS`, `MHD_ESTIMATE`  
**Build order:** **Gate 0 → Gate A / MHD_SECTIONS → Gate B / MHD_ESTIMATE → Gate C / Golden → Gate D / MahodAI**  
**Date locked:** 2026-08-17
**Final execution revision:** 1.1 · 2026-08-18  
**Supported host strategy:** one codebase for Civil 3D 2026/2027 using the repository's existing version-targeting approach  
**First local GUI target:** Civil 3D 2027 where available; Civil 3D 2026 remains a required supported build/compatibility target  

---

# 0. AUTHORITY, SCOPE LOCK, AND NON-NEGOTIABLE RULES

This document is the single implementation authority for the Mahod Civil Delivery work described here.

Claude Code must not reopen the architecture unless a reproducible technical fact makes the locked design impossible. Missing project information becomes an `OPEN_QUESTION`; it does **not** trigger a redesign.

The project contains two independent engineering workflows on one existing Civil platform:

```text
                           MAHOD AI / CIVIL PLATFORM
                        Commands / Ribbon / Chat later
                                   |
                    +--------------+--------------+
                    |                             |
             MHD_SECTIONS                  MHD_ESTIMATE
                    |                             |
                    v                             v
        Civil-native Section Composer      Civil Quantity Adapter
                    |                             |
                    |                    Neutral Quantity Records
                    |                             |
                    |                             v
                    |                      LANDQ BACKEND
                    |                Rules / Mapping / Pricing /
                    |                Adjustments / QA / Export
                    |
                    +--------- shared platform contracts --------+
                              Project Profile
                              Provenance / Evidence
                              Run manifests
                              Status conventions
                              Golden infrastructure
```

## 0.1 Hard architectural rules

1. `MHD_SECTIONS` and `MHD_ESTIMATE` are **independent workflows**.
2. `MHD_SECTIONS` must work when LandQ is unavailable.
3. LandQ has **no role** in section creation, section sources, utilities, styling, labels, bands, layout, or section-sheet composition.
4. The Civil plugin is **not** the organizational pricing/estimate engine.
5. Civil produces measurements and Neutral Quantity Records.
6. If the existing LandQ contract is verified in Gate 0, LandQ is the sole organizational estimate/pricing backend for:
   - mapping/rules;
   - quantity adjustments;
   - catalog identity;
   - price-book snapshots;
   - price/project overrides;
   - approvals;
   - QA;
   - estimate aggregation;
   - Excel / Benarit-compatible export;
   - estimate audit history.
7. **No second estimate core may be created inside MahodAI/Civil.**
8. Do not create one shared engineering-calculation core for Sections and Estimate.
9. The only shared components between the two domains are:
   - versioned Project Profile;
   - contracts / DTOs / JSON schemas;
   - provenance/evidence;
   - run manifests;
   - logging/status conventions;
   - Golden-test infrastructure.
10. `MHD_SECTIONS` and `MHD_ESTIMATE` must be directly usable and acceptance-tested without an LLM.
11. **"Standalone" in this plan means direct deterministic commands/ribbon workflows inside Civil 3D without MahodAI/LLM. It does NOT mean a separate Windows EXE.**
12. `MHD_SECTIONS` requires the Civil 3D runtime because it creates real Civil objects (`SampleLine`, `Section`, `SectionView`). Do not advertise or implement plain-AutoCAD-only section creation.
25. MahodAI is an additional host/orchestrator over the same deterministic service APIs. The Sections AI route may be wired immediately after Gate A is stable; it must not wait for Estimate/LandQ. The Estimate AI route may be wired after Gate B is stable.
26. Natural-language success is never an engineering acceptance test.
13. No project-specific product code such as `ModiinQuantityEngine.cs`.
14. Modiin project 6422 is a `ProjectProfile`, not a product fork.
15. No silent engineering fallback.
16. No guessed engineering values.
17. No guessed catalog mapping.
18. No guessed price.
19. No hidden unit conversion.
20. No mutation of source project DWGs in `P:\data\6422`.
21. Work only on copies/test fixtures unless explicit written authorization says otherwise.
22. No throwaway second engine. Any PoC code/data that proves a production path must either:
    - become production architecture/test material; or
    - be isolated under a clearly disposable test-harness path and never become runtime dependency.
23. Do not extend the existing custom-block section generator into the final `MHD_SECTIONS` implementation.
24. Native Civil viability is a mandatory Gate before full Section Composer implementation.

---

# 1. VERIFIED CURRENT-STATE AUDIT — MAHODAI CIVIL

Claude Code must re-verify this section against the checked-out repository before changing code. If repository state has moved, update the Discovery Report with the actual SHA and differences. Do not silently assume this snapshot is current forever.

## 1.1 Repository baseline observed at plan lock

Observed repository:

```text
devMahod/MahodAI-Plugin
branch: main
latest observed main commit:
7df13b589764a2e5ae8bfc495e0df90a3b63d390
```

The immediately preceding validated fix commit reported:

```text
MahodAI.Core.Tests:          369 / 369
MahodAI.Civil3D.Plugin.Tests: 1045 / 1045
failed: 0
skipped: 0
```

**Mandatory first action on the feature branch:** rerun the complete existing suite. These historical counts are baseline evidence, not permission to skip local verification.

The plugin project is C#/.NET, x64, WPF/WinForms-capable, with Autodesk AutoCAD/Civil references and an opt-in live-deploy build path. Preserve the existing build/deploy discipline; ordinary builds must not silently overwrite the running plugin.

## 1.2 Existing relevant platform components

The current `ToolRegistry` already registers a large deterministic Civil tool surface, including relevant items:

```text
Discovery:
- ListObjectsTool
- ListLayersTool
- GetDrawingSummaryTool
- ListLayoutsTool

Alignment:
- GetAlignmentGeometryTool
- GetPointAtStationTool
- GetStationOffsetTool
- ListAlignmentsTool
- FindIntersectionsTool

Corridor:
- GetCorridorInfoTool
- GetCorridorCrossSectionTool
- SampleCrossSectionsTool
- ListCorridorsTool

Pipe Network:
- GetPipeNetworkInfoTool
- GetPipeDetailsTool
- GetStructureDetailsTool
- ListPipeNetworksTool

Creation:
- CreateSampleLinesTool
- CreateSampleLineGroupTool
- CreateSectionViewsTool
- CreateProfileViewTool
- CreateCorridorTool
- ConfigureProfileBandsTool
...

CrossSection:
- AuditCrossSectionsTool
- PreviewCrossSectionFixTool
- AcceptCrossSectionFixTool
- DiscardCrossSectionFixTool
```

Also relevant:

```text
MahodAI.Civil3D.Plugin/DrawingSummaryExtractor.cs
MahodAI.Civil3D.Plugin/Extractors/CorridorExtractor.cs
MahodAI.Civil3D.Plugin/Tools/ObjectFinder.cs
MahodAI.Civil3D.Plugin/Tools/HighlightManager.cs
MahodAI.Civil3D.Plugin/Tools/SerialToolQueue.cs
MahodAI.Civil3D.Plugin/MahodCommands.cs
MahodAI.Civil3D.Plugin/WebSocket/*
```

`DrawingSummaryExtractor` already extracts a substantial Civil snapshot including alignments, profiles, surfaces, corridors, pipe networks, feature lines, layers and corridor cross-section information. Gate 0 must determine exactly what section-specific discovery is still missing.

## 1.3 `SampleCrossSectionsTool` — reuse boundary

Current purpose:

- samples corridor geometry at regular or explicit stations;
- extracts offset/elevation/code information;
- includes multiple extraction paths through corridor feature lines and Applied Assemblies;
- is useful for analysis/verification of corridor geometry.

**Reuse:**

- existing ObjectFinder patterns;
- explicit-station handling concepts;
- corridor feature-code reading;
- AppliedAssembly fallback knowledge;
- deterministic returned DTO/result patterns;
- relevant tests.

**Do not treat it as the Section Composer.**

Sampling corridor geometry and creating real Civil Sample Lines/Sections/Section Views are different responsibilities.

## 1.4 `CreateSampleLinesTool` — reuse boundary

The existing tool:

- creates a `SampleLineGroup`;
- creates sample lines at interval stations;
- uses reflection/fallback behavior for Civil API compatibility;
- treats sample lines as optional because the current custom section-view path does not require them;
- can return a successful result when sample-line creation is skipped.

That behavior is **not acceptable** for `MHD_SECTIONS`.

For `MHD_SECTIONS`:

- real native Sample Lines are mandatory;
- failure to create required native Civil objects is not success;
- no `success=true` with “skipped” for a required Gate-A operation;
- explicit CL geometry, not interval-only perpendicular sampling, is the governing input.

Existing code may be reused only after its semantics are brought under the locked Section Composer contract and covered by Gate-A tests.

## 1.5 `CreateSectionViewsTool` — explicitly NOT the target

The existing `CreateSectionViewsTool` is a custom rendering path.

Its own source states that it:

```text
does NOT use native Civil 3D SectionView API
and instead draws custom blocks with polylines
```

It directly samples EG/corridor geometry and composes AutoCAD block/polylines in a grid.

**Locked decision:**

- keep the existing tool for existing workflows unless a separate approved task changes it;
- do not break its current users/tests;
- do not extend it as the final implementation of `MHD_SECTIONS`;
- do not delete it merely because the new Section Composer exists;
- `MHD_SECTIONS` must first prove native Civil viability.

## 1.6 Existing CrossSection Reader/Audit/Fix — reuse the workflow pattern, not the data model

The current CrossSection tools operate on already drafted section sheets using known Mahod layer conventions and implement:

```text
AUDIT -> PREVIEW -> ACCEPT / DISCARD
```

The preview uses a working layer (`MAHOD_FIX`) and avoids editing native annotation before user approval.

**Reuse conceptually:**

- non-destructive planning;
- visible preview;
- explicit user apply/accept;
- deterministic reports;
- cleanup of temporary preview;
- direct tools callable without chat.

**Do not reuse as if these are native Civil Section Views.**

The existing Reader is a drafted-sheet decoder, not the new CL-to-Civil object model.

## 1.7 Tool registry and AI boundary

The current platform already has a central deterministic tool registry.

New capabilities must expose deterministic tools first. Only Gate D may wire them into MahodAI routing.

Do not duplicate business logic in:

- command handler;
- Tool class;
- chat pipeline;
- prompt.

The command, tool, and later AI pipeline must call the same service layer.

---

# 2. LANDQ CURRENT-STATE AUTHORITY AND BOUNDARY

## 2.1 Historical LandQ design evidence

An existing locked LandQ plan defines a standalone product concept:

```text
landq.mahodeng.co.il
expected repo: devMahod/mahod-landq

Backend FastAPI
-> jobs
-> Rule Engine
-> Pricing
-> QA Engine
-> Exporters

CadProcessor adapters
-> neutral source-agnostic quantity package
```

It also defines a source-agnostic quantity contract (`QPKG`) and versioned price-list/rule/project/run concepts.

Historical responsibilities include:

- rules as data (YAML/DB);
- mapping;
- versioned price list;
- QA;
- duplicate/overlap checks;
- unmapped-item handling;
- source-handle traceability;
- NTI-style Excel;
- Benarit-compatible Excel;
- audit output.

## 2.2 Current LandQ implementation is NOT assumed

At the time this build plan was locked, the historical LandQ architecture exists as project evidence, but the actual current LandQ repository/service/API contract must be verified in Gate 0.

Do not infer that the planned repo, API, schema, or deployment exists unchanged.

### Gate-0 rule

If Claude Code cannot obtain and verify the real current LandQ contract:

```text
LANDQ_CONTRACT_UNVERIFIED
```

Then:

- do not invent REST endpoints;
- do not invent database tables;
- do not create a replacement pricing engine;
- do not create a replacement Excel estimate engine in Civil;
- define/finalize the plugin-side Neutral Quantity Record JSON schema;
- define a narrow `ILandQBoundary`/adapter contract;
- mark the LandQ integration portion of Gate B as `BLOCKED_DEPENDENCY`;
- continue Section Composer work because it is independent.

## 2.3 Required Gate-0 LandQ check

Verify, read-only:

1. actual repository location;
2. actual deployed service, if any;
3. actual data contract:
   - whether the current QPKG exists;
   - whether it can directly represent Civil-generated records;
   - whether an adapter is required;
4. actual catalog/pricelist model;
5. actual rules/mapping model;
6. actual adjustment support;
7. actual approval/provenance support;
8. actual Excel export behavior;
9. actual project/run identifiers;
10. authentication and transport requirements;
11. backend version/hash to record in run manifests.

If the existing QPKG is sufficient, prefer adapting Civil output to that verified contract rather than creating a competing second neutral package.

---

# 2A. HOST / DELIVERY MODEL — FINAL CLARIFICATION

This project follows the same product pattern learned from Intergreen: deterministic engineering services first, then multiple user entry points over the same services.

## 2A.1 MHD_SECTIONS host model

`MHD_SECTIONS` is **Civil 3D-native**.

It must be available through:
- a direct Civil command;
- a Ribbon/button workflow;
- MahodAI orchestration calling the same service API.

The direct command/Ribbon path is the "standalone" workflow: it works without LLM/agent assistance, but it still runs **inside Civil 3D**.

Plain AutoCAD does not provide Civil `SampleLine`, `Section`, `SectionView`, Corridor or Civil Surface APIs. Therefore:
- do not create a fake AutoCAD-only Section Composer;
- if the plugin is ever loaded in plain AutoCAD, Section creation must be unavailable with a clear "Civil 3D required" message;
- do not duplicate the Section Composer into a second AutoCAD implementation.

## 2A.2 MHD_ESTIMATE host model

The first production quantity adapter runs inside Civil 3D and can measure both Civil objects and ordinary AutoCAD entities present in the Civil drawing.

Future source adapters may ingest plain DWG/AutoCAD/GIS/Excel/Revit geometry into the neutral quantity contract, but that is not required for Nataly's first release.

## 2A.3 Civil 3D 2026 / 2027

Use one source codebase and the existing MahodAI version-targeting strategy.

Required:
- build/package support for Civil 3D 2026 and 2027 where supported by the current repository;
- no duplicated engineering logic per Civil version;
- shared contracts/services remain common;
- host/build shims may differ only where Autodesk API/runtime requires them.

Because Arthur currently has Civil 3D 2027, use 2027 as the first available local real-GUI smoke target when practical.

Civil 3D 2026 remains a supported target:
- compile and run automated compatibility tests;
- perform a real 2026 GUI smoke when a healthy 2026 workstation is available;
- until that happens, label 2026 honestly as `BUILT/TESTED_AS_AVAILABLE` rather than falsely claiming GUI verification.

The installer/package must clearly state which versions were actually GUI-smoked versus only built/tested headlessly.

---

# 3. BUILD ORDER — LOCKED

Implementation order is fixed, but the two workflows are released independently:

```text
Gate 0   Discovery / Read-only inventory
  ->
Gate A   MHD_SECTIONS / Native Civil viability + production Section Composer
  ->
Gate A-AI   MahodAI Sections orchestration over the SAME accepted Sections APIs
  ->
NATALY SECTIONS RELEASE PACKAGE
  ->
Gate B   MHD_ESTIMATE / Civil measurement + Neutral Quantity Records + verified LandQ boundary
  ->
Gate B-AI   MahodAI Estimate orchestration over the SAME accepted Estimate APIs
  ->
Gate C   Golden reproduction / closure using all evidence actually available
  ->
Gate D   Final combined MahodAI parity/regression gate
```

Do not delay Gate A or Gate A-AI for LandQ, pricing, Estimate, or the Ben-Gurion source DWG.

`MHD_SECTIONS` must be releasable to Nataly once Gate A + Gate A-AI + packaging/smoke are green.

No full-scale estimate rule expansion before Gate B representative items pass.

---

# 4. GATE 0 — DISCOVERY, READ ONLY

## 4.1 Purpose

Gate 0 exists to replace assumptions with project evidence before production implementation.

No production Section Composer or Estimate behavior is to be implemented before the Discovery outputs are complete.

Diagnostic scripts/tools are allowed only when they are:

- read-only;
- clearly isolated;
- reproducible;
- retained as tests/discovery utilities if useful;
- never allowed to mutate source project files.

## 4.2 Required project inputs

### Golden Sections pack — Ben-Gurion

The real source DWG is highly valuable for full Golden closure, but it is **NOT a prerequisite for starting Gate A or releasing the first Nataly Sections release**.

If/when available, obtain the source DWG corresponding to:

```text
VSC-UT-ALL-00NTZ_BG-PD-500X.dwg
```

The available PDF is visual evidence only. Until the source DWG is available, use the supplied PDF for visual target evidence and use the real 6422 materials (especially `6422-HW-CS.dwg`) for actual current-project Civil object/style/layout evidence.

The full Ben-Gurion Golden pack, when available, should include as applicable:

- host DWG;
- XREFs;
- Data Shortcuts;
- DWT/template;
- plot/style dependencies;
- Civil styles;
- label styles;
- band styles/band sets;
- referenced surfaces;
- referenced corridors;
- referenced pipe/pressure networks;
- any utility coordination files;
- fonts/linetypes only as normal project dependencies — do not redistribute restricted font files in artifacts;
- eTransmit or equivalent complete dependency package.

### Modiin 6422 working pack

Start from the supplied **Input Package v1** containing the collected 6422 DWGs, CL, utility drawings, section/profile drawings, PDF and estimate/catalog files.

Treat every supplied source as immutable. Create writable copies for Civil testing.

Do not request another broad project dump before beginning. If a supplied DWG exposes an unresolved dependency that truly blocks a required real section, request that **exact referenced file only**.

### CL

Use:

- `CL.dwg` as the production/source input;
- optionally `CL.dxf` for transparent diagnostic inspection if convenient; or
- a read-only probe output that reports entities, handles, endpoints, layers, transforms, nearby labels and source/XREF provenance.

Do not block Gate A merely because `CL.dxf` is absent; the product must ultimately read the DWG/Civil data natively.

The final runtime solution must read actual Civil/AutoCAD drawing data; DXF is a discovery convenience, not a required production architecture.

### Quantity pack

Obtain at least one real Modiin PD drawing/sample containing geometry for 5–10 representative quantity items.

### Quantity_Cost

Inspect project `Quantity_Cost` material if present.

This is a priority input because it may contain:

- source measured quantities;
- adjustment rules;
- rounding;
- cost assumptions;
- prior price sources;
- segment allocations;
- evidence explaining the observed Golden quantity adjustment candidate.

### Estimate Golden material

Already supplied / to be retained as Golden evidence:

- Judgment-2 example workbook;
- NTI catalog/price-book workbook;
- any older/original price-book snapshot actually used by the Ben-Gurion Golden estimate;
- source workbook hash and metadata.

## 4.3 Discovery inventory — Civil

For every relevant DWG, report at minimum:

```text
drawing path/copy path
drawing checksum
DWG version
units / INSUNITS
coordinate system / coordinate evidence
XREF graph
XREF status
XREF transform matrix
Data Shortcut references
Alignments
Profiles
Corridors
Baseline/regions
Surfaces
Corridor surfaces
Feature Lines
Sample Line Groups
Sample Lines
Section Sources
Sections
Section Views
Pipe Networks
Pressure Networks
Structures
projected AutoCAD objects
Blocks / nested blocks
Polylines / 3D polylines
styles
section styles
sample-line styles
section-view styles
band styles / band-set styles
label styles
layouts / paper size / placement
relevant layers
```

Do not collapse “utilities” into one category. Record actual entity types.

## 4.4 Discovery inventory — CL

For each CL instruction candidate, record:

```text
source drawing
source XREF, if any
source handle
entity type
layer
WCS endpoints after transform
source-space endpoints
transform matrix
length
angle
nearby text/labels
candidate section number text
intersections with each candidate alignment
candidate station(s)
candidate signed endpoint offsets
degenerate/zero-length flag
```

Discovery must answer, not assume:

1. Is each CL a Line, Polyline segment, Block geometry, or mixed?
2. Is CL geometry in host model space or XREF?
3. Does it physically cross the intended alignment?
4. Are there multiple alignments?
5. Can one CL cross one alignment more than once?
6. Does section numbering correlate with station?
7. Is `1086` intended as station `1+086`, or is that only a coincidence?
8. Are extents defined by CL endpoints?
9. Is skew intentional and must it be preserved exactly?
10. What tolerance is valid for “intersection” if a drafting gap exists?

Until answered, numbering/station correlation remains a validation hypothesis only.

## 4.5 Discovery inventory — Golden Section object model

For the Ben-Gurion Golden source DWG, answer:

- Are the visible section views true Civil `SectionView` objects?
- Which `SampleLineGroup` owns them?
- Which `SampleLine` corresponds to each shown section?
- Which sources are native sampled Section Sources?
- Which utilities are Pipe Networks?
- Which are Pressure Networks?
- Which are Feature Lines?
- Which are AutoCAD polylines/blocks/projected entities?
- How are projected utilities represented?
- Which style names/IDs are used?
- Which band set and labels are used?
- Are views placed in Model Space, Paper Space, or model-space sheet composition?
- How is the plan/index of section locations produced?
- Is section numbering station-derived or explicitly labelled?
- Are annotations native Civil labels, AutoCAD text, blocks, or mixed?

## 4.6 Discovery inventory — Estimate / pricing

Report:

```text
Golden workbook structure
sheet names/order
chapter/subchapter structure
catalog item codes
units
formulas
prices
price source/version if identifiable
missing-price conventions
manual/project overrides
Quantity_Cost evidence
quantity calculation source
adjustment evidence
rounding evidence
segment allocation
```

### Observed Golden adjustment candidate

Record exactly:

```text
Observed Golden adjustment candidate: 0.90
Status: UNVERIFIED_PROJECT_RULE
```

Do **not** place it into production ProjectProfile rules until evidence from `Quantity_Cost` or another authoritative calculation source proves:

- what the factor means;
- direction of application;
- scope;
- which item types it applies to;
- whether it applies before/after rounding;
- who/what approved it.

The fact that Golden count-like quantities such as 5.4 and 3.6 become integers under a 0.90 relationship is useful evidence, not sufficient authority to apply a blanket project rule.

## 4.7 Price discovery rule

`CatalogItem` and `PriceRecord` are separate concepts.

Discovery must identify the actual price-book version used by the Golden estimate.

Do not attempt to reconstruct an old price book by applying a global/subchapter conversion factor to 08/2025.

Price changes can be item-specific.

A Golden numerical price reproduction is valid only with the actual Golden price source/snapshot or an independently proven equivalent source.

## 4.8 Gate-0 outputs

Create:

```text
docs/civil-delivery/GATE_0_DISCOVERY_REPORT.md
docs/civil-delivery/OPEN_QUESTIONS.md
docs/civil-delivery/CURRENT_STATE_AUDIT.md
docs/civil-delivery/LANDQ_CONTRACT_AUDIT.md
artifacts/discovery/discovery_manifest.json
artifacts/discovery/cl_inventory.json
artifacts/discovery/civil_inventory.json
artifacts/discovery/estimate_inventory.json
```

Every input artifact in the manifest must include:

- path/reference;
- SHA-256 where feasible;
- last-modified metadata where useful;
- role;
- read-only/source vs fixture/copy;
- provenance note.

## 4.9 Gate-0 stop condition

Proceed to Gate A as soon as the supplied 6422 package supports at least the 3 required real/adversarial section cases.

The following are true Gate-A blockers:

- the supplied 6422 files cannot be loaded in the target Civil runtime;
- CL geometry cannot be interpreted at all;
- no intended target alignment can be identified for any 3 viable Gate-A cases;
- an exact unresolved dependency is proven to block one of the required Gate-A cases and no alternative real case can satisfy the Gate.

The following are **NOT** Gate-A blockers:

- missing Ben-Gurion source DWG;
- missing optional CL DXF;
- unverified LandQ;
- missing estimate price source;
- unrelated missing XREFs that do not affect the selected Gate-A cases.

The Ben-Gurion source DWG blocks only claims that genuinely require object-level Ben-Gurion Golden evidence.

LandQ contract uncertainty blocks only the LandQ-integration portion of Gate B.

When an exact missing dependency is required, request only that exact file and continue all independent work.

---

# 5. SHARED PLATFORM CONTRACTS — NO SHARED ENGINEERING CORE

Shared code is limited to data contracts and operational infrastructure.

## 5.1 Proposed repository folder layout

Use the existing repository; do not create a new “Modiin” repository.

Recommended locked layout:

```text
MahodAI.Civil3D.Plugin/
  CivilDelivery/
    Shared/
      ProjectProfile.cs
      ProjectProfileLoader.cs
      DeliveryStatus.cs
      DeliveryFinding.cs
      ProvenanceRef.cs
      RunManifest.cs
      ArtifactHash.cs
      DrawingFingerprint.cs
      OwnershipMetadata.cs
      RunManifestWriter.cs

    Sections/
      Contracts/
        ClSourceRecord.cs
        SectionPlanRequest.cs
        SectionPlanRecord.cs
        SectionPlan.cs
        SectionPreviewResult.cs
        SectionApplyResult.cs
        SectionVerifyResult.cs
        SectionSourcePlan.cs
        UtilitySourcePlan.cs
        SectionLayoutPlan.cs

      Services/
        ClInstructionReader.cs
        XrefTransformResolver.cs
        AlignmentCandidateResolver.cs
        SectionGeometryResolver.cs
        NativeSampleLineGroupService.cs
        NativeSampleLineService.cs
        NativeSectionSourceService.cs
        NativeSectionViewService.cs
        SectionStyleResolver.cs
        SectionBandResolver.cs
        SectionLabelResolver.cs
        UtilitySectionAdapterRegistry.cs
        SectionLayoutComposer.cs
        SectionOwnershipService.cs
        SectionPlanService.cs
        SectionPreviewService.cs
        SectionApplyService.cs
        SectionVerifyService.cs
        SectionGoldenEvidenceService.cs

      Adapters/
        IUtilitySectionAdapter.cs
        NativePipeNetworkSectionAdapter.cs
        NativePressureNetworkSectionAdapter.cs
        ProjectedPolylineSectionAdapter.cs        # only if Gate A proves needed
        ProjectedBlockSectionAdapter.cs           # only if Gate A proves needed
        FeatureLineSectionAdapter.cs              # only if Gate A proves needed

      UI/
        SectionsWorkflowControl.xaml
        SectionsWorkflowControl.xaml.cs

    Estimate/
      Contracts/
        NeutralQuantityRecord.cs
        QuantitySourceRef.cs
        QuantityMeasurement.cs
        QuantityExtractionRequest.cs
        QuantityExtractionRun.cs
        LandQSubmissionEnvelope.cs
        LandQSubmissionResult.cs

      Services/
        CivilQuantityPlanService.cs
        CivilQuantityExtractionService.cs
        QuantityUnitValidator.cs
        QuantitySourceIdentityService.cs
        DuplicateQuantityRiskDetector.cs
        QuantityEvidenceService.cs
        LandQBoundaryService.cs

      Extractors/
        ICivilQuantityExtractor.cs
        PolylineLengthQuantityExtractor.cs
        HatchAreaQuantityExtractor.cs
        BlockCountQuantityExtractor.cs
        CorridorQuantityExtractor.cs
        SurfaceVolumeQuantityExtractor.cs
        CivilQtoQuantityExtractor.cs

      Integration/
        ILandQBoundary.cs
        VerifiedLandQAdapter.cs                    # only after Gate-0 contract verification

      UI/
        EstimateWorkflowControl.xaml
        EstimateWorkflowControl.xaml.cs

  Tools/
    CivilDelivery/
      Sections/
        PlanSectionsTool.cs
        PreviewSectionsTool.cs
        ApplySectionsTool.cs
        VerifySectionsTool.cs

      Estimate/
        PlanEstimateQuantitiesTool.cs
        ExtractEstimateQuantitiesTool.cs
        SubmitEstimateToLandQTool.cs              # only after verified contract
        GetEstimateTraceTool.cs

  Commands/
    MhdSectionsCommand.cs
    MhdEstimateCommand.cs

contracts/
  civil-delivery/
    project-profile.schema.json
    run-manifest.schema.json
    section-plan.schema.json
    neutral-quantity-record.schema.json
    finding.schema.json

profiles/
  civil-delivery/
    6422/
      project-profile.yaml
      README.md
      section-source-map.yaml
      quantity-source-map.yaml
      golden-input-manifest.yaml

docs/
  civil-delivery/
    MAHOD_CIVIL_DELIVERY_BUILD_PLAN_LOCKED.md
    GATE_0_DISCOVERY_REPORT.md
    CURRENT_STATE_AUDIT.md
    LANDQ_CONTRACT_AUDIT.md
    OPEN_QUESTIONS.md

fixtures/
  civil-delivery/
    golden-ben-gurion/
    modiin-6422/
    synthetic/

artifacts/
  civil-delivery/
    gate-a/
    gate-b/
    gate-c/
    gate-d/
```

### Folder rule

Do not add adapter files merely because they are listed above. Utility adapters beyond native sources are created only when Gate A demonstrates a real unsupported entity type.

## 5.2 Shared status model

Use one status vocabulary across both domains, but domain-specific error codes.

### Record/workflow status

```text
DISCOVERED
READY
PREVIEW_READY
APPLIED
VERIFIED
WARNING
REVIEW_REQUIRED
BLOCKED
FAILED
```

### Finding severity

```text
INFO
WARNING
REVIEW_REQUIRED
ERROR
```

### Rules

- `WARNING` never silently changes an engineering result.
- `REVIEW_REQUIRED` requires explicit user resolution/approval before the affected action can be applied/exported.
- `ERROR` means the operation failed or the data is invalid.
- `BLOCKED` means a required dependency or unresolved issue prevents the required output.
- Batch status must reflect the worst unresolved required record.
- Do not downgrade an error merely to keep a Gate green.

## 5.3 Required finding model

`DeliveryFinding` must contain:

```text
finding_id
code
domain                  # sections | estimate | shared
severity
status
title
message
project_profile_id
run_id
source_refs[]
affected_record_ids[]
evidence_refs[]
recommended_action
created_at_utc
resolved_at_utc?
resolution?
resolved_by?
```

No free-floating warning without source/evidence when source can be identified.

## 5.4 Provenance model

`ProvenanceRef` must support:

```text
source_kind
source_path_or_uri
drawing_checksum
source_handle
source_subentity_path?
xref_path?
xref_transform?
entity_type?
layer?
station_from?
station_to?
measurement_method?
input_hash?
tool_version
project_profile_version
run_id
```

Every final Section Plan record and Neutral Quantity Record must be traceable to source.

## 5.5 Run manifest

Every Plan/Apply/Verify/Estimate extraction run must emit a machine-readable manifest.

Required fields:

```text
schema_version
run_id
feature                  # sections | estimate
operation                # discover | plan | preview | apply | verify | extract | submit
started_at_utc
completed_at_utc
user
machine
civil_version
plugin_git_sha
plugin_build_version
project_profile_id
project_profile_hash
input_drawings[]
input_hashes[]
landq_version_or_contract_hash?   # estimate only, once verified
result_status
record_counts
finding_counts
artifacts[]
```

This is evidence, not telemetry. No central telemetry project is created as part of this scope.

---

# 6. PROJECT PROFILE — GENERIC PRODUCT, PROJECT-SPECIFIC DATA

## 6.1 Profile identity

Modiin is represented as:

```text
profile_id: 6422
```

No Modiin-specific class names.

## 6.2 Profile storage

Use version-controlled YAML for deterministic project configuration that is appropriate to store in the repository/approved project configuration location.

Do not store secrets in ProjectProfile.

If some project values are approved later from the UI, persist them in a controlled project-profile/sidecar mechanism with provenance, not hidden machine-local state.

## 6.3 Minimum ProjectProfile schema

Conceptual schema:

```yaml
schema_version: 1
profile_id: "6422"
project_name: null
project_stage: "early-design"

provenance:
  source: null
  version: 1
  created_at_utc: null
  created_by: null
  approved_at_utc: null
  approved_by: null
  source_hashes: {}

sections:
  cl:
    source_files: []
    layer_patterns: []
    allowed_entity_types: []
    intersection_tolerance_m: null
    numbering:
      mode: null
      label_layer_patterns: []
      station_validation_enabled: false
      station_validation_tolerance_m: null

  alignments:
    allowed_names: []
    explicit_source_to_alignment: {}

  sources:
    sampled_source_rules: []
    utility_source_rules: []

  styles:
    template_source: null
    sample_line_group_style: null
    sample_line_style: null
    section_view_style: null
    band_set_style: null
    label_styles: {}

  layout:
    strategy: null
    target_space: null
    sheet_size: null
    columns: null
    rows: null
    spacing: null
    plan_index_enabled: null

estimate:
  landq:
    project_ref: null
    contract_version: null

  quantity_sources:
    rules_ref: null
    source_scope_policy: null
    xref_policy: null

  catalog:
    catalog_version: null

  pricing:
    price_book_snapshot_id: null
    price_book_hash: null

  approved_adjustments: []
  project_overrides: []
```

`null` is preferable to an invented default for engineering/project rules.

## 6.4 ProjectProfile validation

Before Plan/Extract:

- validate schema version;
- validate referenced style names exist;
- validate referenced source files/hash where required;
- validate units;
- validate LandQ references only when estimate integration is used;
- validate that adjustments are approved/verified before auto-application;
- produce explicit findings for unresolved fields.

---

# 7. MHD_SECTIONS — CIVIL-NATIVE SECTION COMPOSER

# 7.1 Product contract

`MHD_SECTIONS` performs:

```text
CL
-> PLAN
-> PREVIEW
-> APPLY
-> VERIFY
```

### PLAN

- read-only;
- resolves CL records;
- resolves candidate alignment(s);
- calculates station/skew/extents;
- resolves proposed native Section Sources;
- resolves styles;
- plans layout;
- detects existing tool-managed objects;
- reports findings;
- writes plan artifacts only outside the source DWG.

### PREVIEW

- no persistent Civil-object creation;
- visually shows:
  - CL;
  - chosen/candidate alignment;
  - station;
  - swath/extents;
  - proposed view placement;
  - unresolved utilities/sources;
- use transient/non-destructive graphics where practical;
- clearing preview must leave the DWG unchanged.

### APPLY

Creates/updates only approved tool-managed objects.

### VERIFY

Re-reads the actual Civil database after Apply and proves that created objects match the approved plan.

## 7.2 CL record contract

`ClSourceRecord` must include:

```text
record_id
source_drawing
source_drawing_hash
source_handle
source_xref?
source_entity_type
source_layer
source_endpoints
wcs_endpoints
xref_transform?
length
angle
nearby_labels[]
candidate_section_number?
candidate_alignments[]
selected_alignment?
intersection_points[]
station?
alignment_tangent_angle?
skew_angle?
left_extent?
right_extent?
status
findings[]
```

## 7.3 Alignment resolution — no silent selection

Algorithm contract:

1. transform CL geometry to host WCS;
2. enumerate valid candidate alignments from the active Civil document/profile rules;
3. compute actual CL-segment/alignment intersection evidence;
4. capture every candidate;
5. compute station/offset only for geometrically valid candidates;
6. if exactly one valid candidate remains, it may become `selected_alignment`;
7. if zero or more than one valid candidate remains:
   - do not pick nearest silently;
   - return `REVIEW_REQUIRED` or `BLOCKED`, according to whether user resolution can safely proceed.

Required findings include:

```text
SEC-CL-NO-INTERSECTION
SEC-CL-MULTIPLE-INTERSECTIONS
SEC-ALIGNMENT-AMBIGUOUS
SEC-XREF-TRANSFORM-INVALID
SEC-CL-DEGENERATE
SEC-CL-SOURCE-MISSING
SEC-CL-STATION-LABEL-MISMATCH
SEC-STYLE-MISSING
SEC-SOURCE-MISSING
SEC-UTILITY-UNSUPPORTED
```

## 7.4 Station, skew and extents

The CL geometry is authoritative for the Gate-A geometry test.

Store and verify:

- actual alignment station at crossing;
- alignment tangent direction at crossing;
- CL angle;
- skew relative to the alignment normal;
- signed endpoint offsets;
- left extent;
- right extent.

Do not coerce every CL to perpendicular.

Do not replace CL extents with generic 20/25 m defaults when the CL endpoints encode the intended swath.

If Discovery proves that CL endpoints do **not** encode extents, record the actual project rule in ProjectProfile. Do not guess.

## 7.5 Native Civil Viability Gate — mandatory before full implementation

Use exactly 3 real CL cases selected from the actual project/Golden evidence.

They must not be three easy copies of the same scenario.

### Case A1 — native sampled sources

Must include:

- Alignment;
- Surface and/or Corridor;
- native sampled source;
- real SampleLine;
- real Section;
- real SectionView.

### Case A2 — non-trivial utility

Must include at least one utility/source that is not merely a simple native Pipe Network case, for example a real discovered:

- Polyline;
- BlockReference;
- Feature Line;
- projected object;
- other coordination entity.

The exact type must come from Discovery.

The goal is to prove the boundary between native sampled sources and utility adapters.

### Case A3 — adversarial geometry

Must exercise at least one:

- meaningful skew;
- more than one candidate Alignment;
- geometry edge case;
- XREF-transformed CL;
- other real ambiguity discovered in the project.

### Additional synthetic/real tests

Mandatory tests:

```text
no intersection
multiple intersections
multiple candidate alignments
invalid XREF transform
missing XREF source
zero-length/degenerate CL
missing style
missing section source
rerun with identical inputs
rerun after CL geometry change
rerun after style/profile change
```

## 7.6 Native object target

Final Section Composer must create real Civil objects as applicable:

```text
SampleLineGroup
SampleLine
sampled Section Sources
Section
SectionView
```

The Gate must prove this on the target Civil version(s).

Do not satisfy Gate A with:

- custom BlockReference section frames;
- sampled polylines masquerading as Section Views;
- screenshots only.

## 7.7 Section Source strategy

For each planned source, record:

```text
source_object_id/handle
source_name
source_type
native_sample_capability
planned_sample_state
style_mapping
adapter_required
status
```

Prefer native sampled-source workflow when it represents the source correctly.

If an entity type cannot be represented correctly through the native sampled-source workflow:

- document the exact failure;
- preserve a reproducible fixture;
- create an adapter only for that entity type.

Do not abandon native Section Views because one utility type needs projection.

## 7.8 Utility adapter contract

`IUtilitySectionAdapter` responsibilities:

- declare supported source entity type(s);
- inspect/plan read-only;
- produce proposed section representation;
- apply only within a real SectionView workflow;
- preserve source provenance;
- verify the output;
- remove/update only tool-owned output;
- return explicit unsupported/review statuses.

Adapters must not contain unrelated Section Composer logic.

## 7.9 Style, bands and labels

Do not invent visual standards from the PDF.

Resolve style names/IDs from the Golden source DWG/DWT.

`SectionStyleResolver`, `SectionBandResolver`, `SectionLabelResolver` must:

- resolve by configured ProjectProfile reference;
- verify the object exists;
- record source/template provenance;
- fail explicitly if required style is missing;
- never silently fall back to Autodesk default style.

If approved workflow requires importing styles from a DWT/source drawing, that import must be explicit, testable and recorded in the run manifest.

## 7.10 Section numbering

Do not assume `1086 == 1+086`.

If Discovery proves numbering is station-based, encode the exact rule in ProjectProfile.

If numbering is explicit text/attribute, read it from the source and validate against geometry.

If explicit text and geometry disagree beyond the approved tolerance:

```text
SEC-CL-STATION-LABEL-MISMATCH
-> REVIEW_REQUIRED
```

## 7.11 Section layout and sheet composition

The final product is **Section Composer**, not “Create Section View”.

Required output may include, as proven by the Golden:

- selected locations;
- numbering;
- utilities;
- styles;
- labels;
- bands;
- layout;
- sheet composition;
- plan/index of section locations.

`SectionLayoutComposer` must use the discovered Golden composition rules.

It must not assume:

- fixed number of columns;
- fixed model-space grid;
- paper-space placement;
- paper size;
- spacing;
- scale;

until Discovery proves them.

## 7.12 Ownership metadata and idempotency

Tool-created Civil objects need reliable ownership/provenance.

### Ownership mechanism

Use an AutoCAD/Civil-safe persistent metadata mechanism:

- `ExtensionDictionary` / `Xrecord` as the authoritative metadata store;
- optionally a small registered XData marker for fast discovery if useful.

Do not rely on object names alone.

Recommended logical metadata:

```text
schema_version
owner = "MahodCivilDelivery"
feature = "sections"
role
project_profile_id
run_id
source_cl_drawing_hash
source_cl_handle
logical_key
input_fingerprint
created_by_tool_version
created_at_utc
```

### Logical key

A stable logical key must identify a generated object across reruns.

At minimum base it on:

```text
project_profile_id
+
source CL identity (drawing hash + handle)
+
selected alignment identity
+
generated object role
```

### Rerun behavior

On rerun:

1. find existing managed objects by ownership metadata;
2. compare input fingerprint;
3. unchanged:
   - reuse;
   - do not duplicate;
4. changed:
   - plan update/replace;
   - preview the change;
   - apply only after approval;
5. orphaned generated object:
   - report;
   - remove only through explicit tool-managed cleanup;
6. manual user-created object:
   - never delete because it merely has a similar name/location.

## 7.13 Transaction safety

PLAN/PREVIEW: no persistent database transaction that changes the drawing.

APPLY:

- acquire document lock;
- use a controlled transaction;
- Gate-A batch should be atomic where practical;
- runtime failure must rollback rather than leave half-created required objects;
- no broad swallowed exceptions;
- all caught failures produce a finding/result.

VERIFY occurs in a new read transaction after Apply commits.

## 7.14 Direct command UX

`MHD_SECTIONS` must be usable without MahodAI.

Minimal accepted UX:

1. choose/load Project Profile;
2. select CL source or configured CL source;
3. PLAN;
4. show table of records/status/findings;
5. PREVIEW selected/all READY records;
6. APPLY approved READY records;
7. VERIFY;
8. save run/evidence report.

The UI can be WPF using the existing plugin patterns.

The service layer must not depend on the WPF control.

---

# 8. MHD_ESTIMATE — CIVIL QUANTITY ADAPTER TO LANDQ

# 8.1 Product contract

`MHD_ESTIMATE` is a separate track.

Civil responsibilities end at deterministic measurement, classification hints/provenance, duplicate-risk detection, and submission packaging.

Canonical flow:

```text
Source Object
-> Measurement
-> Raw Quantity
-> Neutral Quantity Record
-> [verified LandQ boundary]
-> Adjustment(s)
-> BOQ Quantity
-> Catalog Item
-> Price Record
-> Effective Price
-> Estimate Line
-> Excel / audit
```

The right-hand side after the neutral boundary belongs to LandQ once its current contract is verified.

## 8.2 Neutral Quantity Record

Minimum `NeutralQuantityRecord` contract:

```text
schema_version
record_id
project_profile_id
run_id

source:
  drawing_path_or_ref
  drawing_hash
  object_handle
  subentity_path?
  xref?
  entity_type
  layer
  block_effective_name?
  corridor_name?
  baseline_name?
  station_from?
  station_to?

measurement:
  kind                 # length | area | volume | count | other-approved-kind
  method
  raw_value
  unit
  geometry_evidence?
  parameters{}

classification:
  source_class
  candidate_rule_key?
  tags[]

provenance:
  extractor
  extractor_version
  project_profile_version
  measured_at_utc

status
findings[]
confidence?             # only if explicitly meaningful; never replaces status
```

## 8.3 Measurement methods

Gate B must support only the methods required for the representative items first.

Possible adapters, only when supported by actual Gate-B items:

### Direct AutoCAD geometry

- Polyline length;
- Hatch/closed boundary area;
- Block count.

### Civil geometry

- corridor shapes/codes;
- corridor feature lines;
- surface/cut-fill volumes;
- Civil QTO/material data where appropriate.

### Manual/rule source

A required estimate item may have no reliable source geometry.

Such an item must not be invented by Civil.

Represent it through the verified LandQ/manual rule workflow with explicit provenance, not a fake source object.

## 8.4 Civil QTO is a source, not the estimate engine

Civil QTO/material calculations may supply records for:

- earthworks;
- subbase;
- pavement layers;
- corridor material shapes;
- cut/fill.

It does not replace LandQ or the neutral boundary because the estimate also contains:

- kerbs;
- milling/demolition;
- road markings;
- signs;
- shelters;
- poles;
- blocks/counts;
- lengths;
- areas;
- project/manual items.

## 8.5 Units — hard gate

Every measurement has an explicit unit.

No silent conversion between:

```text
m
m²
m³
unit/count
kg
tonne
dunam
etc.
```

If a mapped catalog/rule expects a different dimension:

```text
EST-UNIT-MISMATCH
-> REVIEW_REQUIRED / BLOCKED for automatic mapping
```

A dimensional conversion may occur only through an explicit verified rule with parameters and provenance, e.g. length × approved width -> area.

## 8.6 Duplicate/double-count control

Different Civil/AutoCAD sources can describe the same physical work.

Before submission, detect and report at least:

- same drawing hash + same handle emitted twice unintentionally;
- host object plus duplicate XREF representation;
- overlapping Hatches/areas for same classification;
- corridor material + independent polygon describing same item;
- nested/dynamic block duplication;
- repeated source package/drawing submission.

Do not globally “union everything”.

Duplicate policy is rule/project-specific and must be explicit.

Use findings such as:

```text
EST-DUPLICATE-SOURCE
EST-XREF-DOUBLECOUNT-RISK
EST-OVERLAP-RISK
EST-CROSS-SOURCE-DUPLICATE-RISK
```

Fan-out from one source into multiple legitimate estimate items is allowed only through an explicit verified LandQ/rule mapping. It is not a duplicate by itself.

## 8.7 Catalog and mapping

Civil does not own organizational catalog mapping.

Unknown mapping:

```text
EST-UNMAPPED
-> REVIEW_REQUIRED
```

AI may later propose candidates, but only engineer approval creates deterministic mapping in the organizational mapping store.

No prompt output becomes a saved rule automatically.

## 8.8 Adjustment chain

The estimate model must retain ordered adjustments:

```text
Measured / Raw Quantity
-> Adjustment 1
-> Adjustment 2
...
-> BOQ Quantity
```

Each adjustment in LandQ must retain, as supported by the verified backend:

```text
factor/formula
reason
source
approval metadata
rule/version
order
```

### 0.90

Locked treatment:

```text
Observed Golden adjustment candidate: 0.90
```

It remains `UNVERIFIED_PROJECT_RULE` until authoritative project evidence proves it.

Do not apply it merely because it reproduces some or all Golden quantities.

## 8.9 CatalogItem != PriceRecord

Required conceptual separation:

### CatalogItem

```text
item_code
description
unit
chapter
subchapter
catalog_version/identity
```

### PriceRecord

```text
item_code
price
price_book_id
effective/source date
source file/hash
project scope?
override?
reason?
approval metadata?
```

### Effective Price

Derived only from the selected, versioned price source and approved override chain.

No guessed price.

No conversion formula from one price-book edition to another.

A blank/`-` catalog price means:

```text
MISSING_PRICE
```

It never means zero.

## 8.10 Price Book snapshot

The selected project price book must be a complete per-item snapshot/version reference, not “current catalog + global factor”.

ProjectProfile stores the selected snapshot identity/hash.

Changing price-book version is an explicit operation with a delta/audit, not a silent refresh.

## 8.11 Excel boundary

Final estimate workbook generation belongs to the verified LandQ backend.

Required behavior:

- copy the approved source/template;
- never overwrite the source workbook;
- preserve required Judgment/NTI structure;
- preserve sheet order;
- preserve styles;
- preserve formulas where required;
- preserve familiar engineering view;
- add controlled traceability/audit output;
- keep source workbook hash/version;
- return generated workbook hash.

Do not create a second Civil-side Excel estimate writer if LandQ owns export.

If LandQ is not yet verified/available, Gate B may stop at accepted Neutral Quantity Records and a blocked integration status. Do not solve the blockage by building another exporter in the plugin.

## 8.12 Direct command UX

`MHD_ESTIMATE` must be usable without an LLM.

Minimum deterministic workflow:

1. load Project Profile;
2. choose configured drawing/scope;
3. PLAN quantity extraction;
4. show sources/methods/units/findings;
5. extract Neutral Quantity Records;
6. run duplicate/unit preflight;
7. if LandQ contract is verified:
   - submit to LandQ;
   - display run status;
   - retrieve trace/output;
8. if not verified:
   - show `BLOCKED_DEPENDENCY: LANDQ_CONTRACT_UNVERIFIED`;
   - retain/export the neutral package as a diagnostic artifact only;
   - do not fabricate final estimate/prices.

---

# 9. GATE A — NATIVE SECTIONS ACCEPTANCE

## 9.1 Required output

Exactly 3 selected real CL records must produce 3 real Civil Section Views and pass all required checks.

## 9.2 Acceptance checklist per section

```text
CL source identity                 PASS
XREF transform, if applicable      PASS
alignment resolution               PASS
station                            PASS
skew                               PASS
left extent                        PASS
right extent                       PASS
SampleLineGroup                    PASS
SampleLine                         PASS
required Section Sources           PASS
Section                            PASS
SectionView                        PASS
utilities                          PASS
styles                             PASS
bands                              PASS
labels                             PASS
numbering                          PASS
layout                             PASS
ownership/provenance metadata      PASS
rerun/idempotency                  PASS
VERIFY actual-vs-plan              PASS
visual Golden comparison           PASS
```

No PASS by screenshot alone for object-level checks.

## 9.3 Required negative/adversarial tests

```text
no intersection                    expected REVIEW/BLOCKED
multiple intersections             expected REVIEW
alignment ambiguity                expected REVIEW
invalid XREF transform              expected BLOCKED
missing required source             expected REVIEW/BLOCKED
missing required style              expected BLOCKED
degenerate CL                       expected ERROR/REVIEW
second identical run                0 duplicates
changed CL rerun                    controlled update/replace
manual nearby SectionView           not deleted
```

## 9.4 Gate-A evidence artifacts

Create:

```text
artifacts/civil-delivery/gate-a/section_plan.json
artifacts/civil-delivery/gate-a/apply_result.json
artifacts/civil-delivery/gate-a/verify_result.json
artifacts/civil-delivery/gate-a/run_manifest.json
artifacts/civil-delivery/gate-a/object_inventory_before.json
artifacts/civil-delivery/gate-a/object_inventory_after.json
artifacts/civil-delivery/gate-a/GATE_A_NATIVE_SECTIONS_REPORT.md
artifacts/civil-delivery/gate-a/screenshots/
```

For screenshots, use real Civil GUI captures only. Do not fabricate.

## 9.5 Gate-A Definition of Done

Gate A passes only when:

- all existing baseline tests remain green;
- new unit/integration tests are green;
- all 3 required real cases pass;
- required adversarial cases behave as specified;
- no duplicated generated objects on rerun;
- no manual objects are damaged;
- output is real Civil native section infrastructure;
- the non-trivial utility case is demonstrated;
- visual comparison against the Golden is accepted;
- all remaining limitations are explicitly documented.

If native SectionView creation fails on the target Civil version, stop and document the reproducible failure before proposing any replacement architecture. Do not silently fall back to custom blocks.

---

# 10. GATE B — ESTIMATE TRACE ACCEPTANCE

## 10.1 Representative set

Select 5–10 representative items from actual Modiin/Golden evidence.

The set should cover more than one measurement method, preferably:

- area;
- length;
- count;
- Civil/corridor/QTO or volume;
- at least one item that exercises a mapping/price/adjustment edge case.

Do not select only “easy” items.

## 10.2 Required trace per item

Prove:

```text
Source
-> Measurement
-> Raw Quantity
-> Neutral Quantity Record
-> Adjustment(s), if verified/applicable
-> BOQ Quantity
-> Catalog Item
-> Price Record
-> Effective Price
-> Estimate Line
-> Excel row / audit trace, when LandQ export is verified
```

Every arrow needs evidence.

## 10.3 Required failure cases

At minimum test:

```text
unknown mapping                 -> REVIEW_REQUIRED
unit mismatch                   -> REVIEW_REQUIRED/BLOCKED
missing price                   -> MISSING_PRICE
unverified 0.90 adjustment      -> not auto-applied
duplicate source handle         -> flagged
host + XREF duplicate risk      -> flagged
price-book version change       -> explicit, audited
blank/"-" price                 -> not zero
source object missing           -> explicit finding
```

## 10.4 Gate-B evidence artifacts

```text
artifacts/civil-delivery/gate-b/neutral_quantity_records.json
artifacts/civil-delivery/gate-b/quantity_preflight.json
artifacts/civil-delivery/gate-b/duplicate_risk_report.json
artifacts/civil-delivery/gate-b/landq_submission.json        # only if verified
artifacts/civil-delivery/gate-b/landq_result.json            # only if verified
artifacts/civil-delivery/gate-b/run_manifest.json
artifacts/civil-delivery/gate-b/GATE_B_ESTIMATE_TRACE_REPORT.md
artifacts/civil-delivery/gate-b/generated_estimate.xlsx      # only from verified LandQ export
```

## 10.5 Gate-B Definition of Done

If LandQ is verified:

- 5–10 representative items trace end to end;
- no second estimate engine exists in Civil;
- units are explicit/validated;
- unmapped/missing-price cases are not hidden;
- generated workbook follows the approved template-copy rules;
- source workbook is unchanged;
- trace reaches source object handles;
- all tests green.

If LandQ is not verified:

Gate B may achieve:

```text
CIVIL_QUANTITY_ADAPTER_READY
LANDQ_INTEGRATION_BLOCKED
```

but not full Gate-B PASS.

Do not claim end-to-end estimate completion.

---

# 11. GATE C — BEN-GURION GOLDEN REPRODUCTION

## 11.1 Purpose

The Ben-Gurion materials are the Golden reference for product behavior and evidence structure.

Gate C validates what the available source evidence can actually prove.

## 11.2 Sections Golden

With the actual source DWG/dependencies:

Validate:

- CL/location interpretation;
- section composition;
- sampled sources;
- utility representation;
- styles;
- labels/bands;
- numbering;
- layout;
- plan/index if present;
- visual output.

Prefer object-level comparison plus rendered visual comparison.

## 11.3 Estimate Golden

Validate:

- workbook structure;
- chapter/subchapter organization;
- catalog identity;
- units;
- price model;
- price-book snapshot behavior;
- adjustment model;
- trace/audit model.

### Numerical quantity claim restriction

Do not claim full quantity Golden reproduction unless the original source geometry/calculation inputs required to reproduce those quantities exist.

A Golden output workbook alone is not enough to prove the source measurement method.

## 11.4 Golden price restriction

Do not compare Golden prices against 08/2025 as though the latter were the Golden price source.

If the actual Golden price-book snapshot cannot be found:

```text
GOLDEN_PRICE_SOURCE_MISSING
```

Document structural/model validation only.

## 11.5 Gate-C artifacts

```text
artifacts/civil-delivery/gate-c/GOLDEN_REPRODUCTION_REPORT.md
artifacts/civil-delivery/gate-c/golden_input_manifest.json
artifacts/civil-delivery/gate-c/section_comparison.json
artifacts/civil-delivery/gate-c/estimate_comparison.json
artifacts/civil-delivery/gate-c/price_book_comparison.json
artifacts/civil-delivery/gate-c/screenshots/
```

## 11.6 Gate-C Definition of Done

Gate C passes when every claimed Golden property has:

- a known source;
- an explicit comparison;
- a result;
- no unsupported “looks similar” assertion;
- all unavailable evidence clearly marked.

---

# 12. MAHODAI ORCHESTRATION — PER CAPABILITY, SAME APIs

MahodAI is a host/orchestration layer, not an engineering engine.

## 12.1 Sections AI route — after Gate A

As soon as the deterministic Sections APIs/direct command are stable and Gate A is green, wire the Sections intents into the existing MahodAI Tool Registry.

Minimum Sections intents:

```text
"תכין חתכים לפי CL"
"תראה לי את תוכנית החתכים לפני יצירה"
"תראה אילו חתכים דורשים בדיקה"
"תציג את חתך <id>"
```

The AI route must call the SAME deterministic services used by `MHD_SECTIONS`.

It may:
- resolve user intent;
- gather non-engineering interaction inputs;
- call PLAN/PREVIEW/APPLY/VERIFY tools;
- explain findings.

It may not:
- select an ambiguous alignment silently;
- invent a style/source;
- bypass REVIEW_REQUIRED/BLOCKED;
- create a separate geometry engine.

A Sections AI route can therefore ship in the first Nataly Sections release without waiting for Estimate/LandQ.

## 12.2 Estimate AI route — after Gate B

After the deterministic Estimate APIs are stable, wire:

```text
"תכין אומדן מוקדם"
"תראה מאיפה הגיעה הכמות"
"אילו סעיפים לא ממופים"
"איפה חסר מחיר"
```

AI may propose mapping candidates, but cannot invent or auto-approve quantities, adjustments, catalog codes or prices.

## 12.3 Direct/API parity

For every supported AI intent:

1. run the direct command/API first and capture deterministic result;
2. run the natural-language route against the same fixture;
3. verify it invokes the same deterministic operation;
4. verify engineering outputs/statuses are identical;
5. verify REVIEW/BLOCKED remains visible.

Natural-language wording quality is secondary to deterministic parity.

## 12.4 Agent repository

Agent/backend changes occur only when required for routing/orchestration and on a separate feature branch from current agent main.

Do not duplicate plugin/Civil logic in Python.

## 12.5 Final Gate D

Gate D is the final combined parity/regression gate after both capability-specific AI routes exist. It is not a reason to delay the Sections AI route or the Nataly Sections release.

---

# 13. EXACT IMPLEMENTATION PHASES

## Phase 0.0 — Branch and baseline

1. read repository `CLAUDE.md`;
2. create feature branch from current `main`;
3. record base SHA;
4. run full existing test suite;
5. record exact counts;
6. no code if baseline is red — investigate first.

Recommended branch:

```text
feature/civil-delivery
```

If team branch policy requires another naming convention, use repository policy; record it in the manifest.

## Phase 0.1 — Discovery

Produce Gate-0 outputs, no production mutation logic.

## Phase A0 — Native Civil spike as a controlled Gate harness

Implement only the minimum reusable services necessary to prove:

- CL parsing;
- alignment resolution;
- native SampleLine from exact CL geometry;
- Section Sources;
- native SectionView;
- one utility path;
- style application;
- verification.

The spike must use the planned production service boundaries. Do not create a one-off standalone executable that will be discarded.

## Phase A1 — Shared contracts/status/provenance

Add:

- ProjectProfile;
- findings;
- run manifest;
- ownership metadata;
- schemas;
- fixture helpers.

No engineering calculation shared across domains.

## Phase A2 — Section PLAN

Implement read-only SectionPlan generation with all ambiguity findings.

## Phase A3 — Section PREVIEW

Implement transient/non-destructive preview.

## Phase A4 — Section APPLY

Implement native object creation/update with ownership metadata and atomic transaction behavior.

## Phase A5 — Section VERIFY

Implement actual-vs-plan verification.

## Phase A6 — Section Composer layout/Golden styling

Only after native viability is green.

## Phase A7 — `MHD_SECTIONS` direct workflow

Register command/tool/UI without AI routing.

Run Gate A.

## Phase A8 — Sections MahodAI routing

Wire Sections intents into MahodAI Tool Registry using the same accepted services. Add direct-vs-AI parity tests.

## Phase A9 — Nataly Sections release packaging

Build/install/package the first Nataly release once:
- Gate A is green;
- Sections AI parity is green (or explicitly environment-blocked while direct workflow remains green);
- real Civil smoke for the available target version is complete;
- installer/docs are ready.

Do not wait for Gate B/LandQ to package Sections.

## Phase B0 — LandQ contract verification

This is already investigated in Gate 0; refresh if backend changed.

## Phase B1 — Neutral Quantity Records + schema

Implement plugin-side contract and unit/provenance validation.

## Phase B2 — Representative Civil extractors

Implement only extractors required by the 5–10 Gate-B cases.

## Phase B3 — duplicate/double-count preflight

Implement source identity and overlap/duplication risk rules.

## Phase B4 — LandQ adapter

Only after actual contract verification.

Do not create if LandQ remains unavailable.

## Phase B5 — `MHD_ESTIMATE` direct workflow

No LLM dependency.

Run Gate B.

## Phase C — Golden

Run Ben-Gurion comparisons and close/record Open Questions.

## Phase D — MahodAI

Only now modify agent routing/orchestration.

---

# 14. TEST STRATEGY

# 14.1 Existing tests are a hard regression gate

Every phase must preserve existing plugin/core tests.

No expected-value tuning merely to recover green.

## 14.2 Pure unit tests

Add unit tests for pure logic that does not require Civil GUI:

### Shared

- ProjectProfile schema/validation;
- status aggregation;
- finding serialization;
- provenance serialization;
- run manifest determinism;
- logical-key generation;
- fingerprint generation.

### Sections

- CL endpoint transform math;
- intersection candidate handling;
- left/right signed extent calculation;
- skew calculation;
- ambiguity classification;
- numbering/station validation;
- ownership logical keys;
- idempotency decision logic.

### Estimate

- source identity;
- unit validator;
- neutral-record serialization;
- duplicate handle detection;
- source overlap risk classification where geometry is available;
- no silent unit conversion;
- blank/missing price status contract from LandQ responses.

## 14.3 Civil integration tests

Use controlled DWG fixtures.

Verify real object types and properties, not only returned JSON.

Examples:

- SampleLineGroup created;
- SampleLine belongs to expected group;
- station/skew/extents match plan;
- Section Sources sampled as planned;
- SectionView exists and references the expected SampleLine;
- style IDs/names match;
- ownership metadata round-trips;
- rerun creates no duplicate;
- update path changes only tool-owned objects.

## 14.4 Real-DWG tests

Real-DWG Gates must run on copies of:

- Golden Ben-Gurion;
- Modiin 6422.

Store fixture hashes and test-copy creation instructions.

Do not commit confidential large DWGs to a repository if policy forbids it. Store manifest/path/hash and secure fixture location instead.

## 14.5 Visual Golden tests

For section composition:

- capture deterministic PDF/PNG output from actual Civil/plot workflow when feasible;
- compare layout, labels, utilities, view placement and major geometry;
- automated image diff can assist;
- final engineering/visual acceptance still requires a real GUI review.

Do not use OCR as the source of Civil object truth.

## 14.6 Excel tests

Executed in/against the verified LandQ backend:

- source/template remains unchanged;
- generated copy opens without repair warning;
- required sheet names/order preserved;
- required formulas/styles preserved;
- item codes/units preserved;
- price snapshot identity recorded;
- missing price not written as zero;
- audit/trace output present;
- Golden price comparison uses the correct source;
- workbook hash recorded.

## 14.7 GUI smoke tests

Gate A smoke:

```text
1. install/deploy feature build to test environment
2. open writable Golden/6422 fixture
3. run MHD_SECTIONS
4. load profile
5. PLAN
6. inspect 3 records
7. PREVIEW
8. APPLY
9. VERIFY
10. inspect native objects in Prospector/properties
11. run command again
12. prove no duplicates
13. edit one test CL in the fixture
14. rerun PLAN/PREVIEW/APPLY
15. prove controlled update
16. close/reopen drawing
17. prove ownership metadata persists
```

Gate B smoke:

```text
1. open quantity fixture
2. run MHD_ESTIMATE
3. inspect planned source items
4. extract 5–10 representative Neutral Quantity Records
5. inspect unit/duplicate findings
6. submit to verified LandQ
7. inspect trace
8. retrieve generated workbook if supported
9. prove original workbook unchanged
```

Gate D smoke compares direct command vs natural-language orchestration.

---

# 15. GOLDEN FIXTURES

## 15.1 Golden Section fixture

Must include a manifest:

```text
fixture_id
host_dwg
host_sha256
dependencies[]
civil_version
expected sections[]
expected section numbers
expected stations, if proven
expected source types
expected styles
expected layout evidence
reference screenshots/pdf
```

## 15.2 Modiin 6422 fixture

Use copied, sanitized where necessary, controlled inputs.

Manifest:

```text
source project path
copy date
copy checksum
selected alignment(s)
selected 3 CL records
selected quantity source records
known limitations
```

## 15.3 Synthetic fixtures

Create minimal fixtures for adversarial cases:

- two alignments crossing one CL;
- one alignment intersected twice;
- XREF-transformed CL;
- degenerate line;
- missing style;
- unsupported utility;
- duplicate quantity source;
- unit mismatch.

Synthetic fixtures supplement real data; they do not replace Real-DWG Gate evidence.

---

# 16. REQUIRED ERROR / FINDING CODES

This is the initial locked code family. Claude Code may add more specific codes when real reproducible conditions require them, but must not replace explicit statuses with generic exceptions.

## Sections

```text
SEC-CL-NO-INTERSECTION
SEC-CL-MULTIPLE-INTERSECTIONS
SEC-ALIGNMENT-AMBIGUOUS
SEC-XREF-TRANSFORM-INVALID
SEC-XREF-MISSING
SEC-CL-DEGENERATE
SEC-CL-SOURCE-MISSING
SEC-CL-STATION-LABEL-MISMATCH
SEC-SAMPLELINE-GROUP-CREATE-FAILED
SEC-SAMPLELINE-CREATE-FAILED
SEC-SOURCE-MISSING
SEC-SOURCE-SAMPLING-FAILED
SEC-SECTIONVIEW-CREATE-FAILED
SEC-STYLE-MISSING
SEC-BAND-STYLE-MISSING
SEC-LABEL-STYLE-MISSING
SEC-UTILITY-UNSUPPORTED
SEC-UTILITY-PROJECTION-FAILED
SEC-OWNERSHIP-CONFLICT
SEC-VERIFY-MISMATCH
SEC-LAYOUT-UNRESOLVED
```

## Estimate

```text
EST-SOURCE-MISSING
EST-MEASUREMENT-FAILED
EST-UNIT-UNKNOWN
EST-UNIT-MISMATCH
EST-DUPLICATE-SOURCE
EST-XREF-DOUBLECOUNT-RISK
EST-OVERLAP-RISK
EST-CROSS-SOURCE-DUPLICATE-RISK
EST-UNMAPPED
EST-ADJUSTMENT-UNVERIFIED
EST-MISSING-PRICE
EST-PRICE-SOURCE-UNVERIFIED
EST-LANDQ-CONTRACT-UNVERIFIED
EST-LANDQ-SUBMISSION-FAILED
EST-EXPORT-FAILED
EST-TRACE-INCOMPLETE
```

---

# 17. SAFETY, BRANCHING AND PROJECT-DATA RULES

1. Work on a new feature branch from current MahodAI main.
2. Do not develop directly on production branch.
3. Do not modify source files under:
   ```text
   P:\data\6422
   ```
4. Use copies/fixtures.
5. Read-only network access, if available, is acceptable for Discovery.
6. Do not allow an agent/tool to recursively mutate project folders.
7. No automatic delete of Civil objects not positively identified as tool-owned.
8. No silent live deployment from ordinary build.
9. Preserve current MahodAI tools.
10. Do not refactor unrelated code.
11. Do not change existing `CreateSectionViewsTool` behavior as part of this scope unless required to prevent naming/tool conflicts and covered by regression tests.
12. Do not add cloud Autodesk dependency for this product.
13. No secrets in profile/manifests.
14. Do not package restricted font files into evidence bundles.
15. All external/deployed backend changes require the same branch/review discipline as plugin changes.

---

# 18. STOP CONDITIONS

Claude Code must stop the affected Gate and report, rather than workaround silently, when any of these occurs.

## Gate 0 stop

- incomplete Golden source pack;
- unreadable CL;
- source DWG cannot load in target Civil;
- project data would require mutating originals.

## Gate A stop

- native SectionView cannot be reliably created on the supported target Civil version;
- Section Sources cannot be controlled as required;
- ownership metadata cannot be persisted safely;
- required utility representation has no safe native/projection adapter path;
- Verify cannot prove actual-vs-plan;
- rerun duplicates or damages manual objects.

A Gate-A stop triggers a written technical failure report. It does **not** authorize falling back to the old custom-block architecture.

## Gate B stop

- LandQ contract unavailable/unverified;
- units cannot be proven;
- source objects cannot be uniquely identified;
- duplicate risk cannot be surfaced;
- required price source absent;
- the only way to proceed would be a second pricing/estimate engine in Civil.

## Gate C stop

- Golden claim lacks its source input;
- Golden price source missing for numerical price reproduction;
- visual-only PDF is being treated as object-model evidence.

## Gate D stop

- AI path changes deterministic engineering output;
- routing hides REVIEW/BLOCKED findings;
- chat performs calculations that direct tools do not.

---

# 19. REQUIRED DELIVERABLES BY GATE

## Gate 0

```text
GATE_0_DISCOVERY_REPORT.md
CURRENT_STATE_AUDIT.md
LANDQ_CONTRACT_AUDIT.md
OPEN_QUESTIONS.md
discovery_manifest.json
cl_inventory.json
civil_inventory.json
estimate_inventory.json
```

## Gate A

```text
MHD_SECTIONS direct command
Section Composer service layer
Section Plan/Preview/Apply/Verify contracts
ProjectProfile section config
ownership metadata
Gate-A unit/integration tests
3 real Native Civil cases
adversarial fixtures
GATE_A_NATIVE_SECTIONS_REPORT.md
run/evidence artifacts
real screenshots
```

## Gate B

```text
MHD_ESTIMATE direct command
NeutralQuantityRecord schema
Civil Quantity Adapter
representative extractors
unit preflight
duplicate-risk preflight
verified LandQ adapter OR explicit blocked dependency
5–10 representative traces
GATE_B_ESTIMATE_TRACE_REPORT.md
run/evidence artifacts
```

## Gate C

```text
GOLDEN_REPRODUCTION_REPORT.md
Ben-Gurion section comparison
workbook structure comparison
catalog identity comparison
price-source model comparison
evidence/trace comparison
input hashes
real screenshots
```

## Gate D

```text
MahodAI orchestration
routing tests
direct-vs-AI parity tests
multilingual routing tests as required by current platform
GATE_D_MAHODAI_REPORT.md
```

---

# 20. DEFINITION OF DONE — PRODUCT LEVEL

Mahod Civil Delivery is not “done” because code builds.

For this locked scope, Done requires:

## Sections

- `MHD_SECTIONS` runs without LLM;
- reads real CL instructions;
- resolves/blocks ambiguity correctly;
- creates real native Civil section infrastructure;
- represents required utilities;
- applies Golden styles/bands/labels/layout;
- uses Plan → Preview → Apply → Verify;
- is idempotent;
- preserves manual work;
- provenance persists;
- Gate A and Golden C pass.

## Estimate

- `MHD_ESTIMATE` runs without LLM;
- extracts representative Civil quantities deterministically;
- creates versioned Neutral Quantity Records;
- validates units;
- flags duplicate risk;
- retains source provenance;
- uses verified LandQ as sole backend for mapping/pricing/adjustment/export;
- never creates a second estimate engine;
- missing mapping/price/adjustment evidence remains visible;
- Gate B and Golden C pass.

## AI

- MahodAI calls the same accepted APIs;
- no duplicated calculations;
- no hidden review states;
- Gate D parity passes.

## Regression

- existing MahodAI tests remain green;
- new tests remain green;
- no unrelated production tool regressions.

## Evidence

Every Gate returns numerical/object evidence, not only prose claims.

---

# 21. INITIAL OPEN QUESTIONS — TO BE RESOLVED IN GATE 0, NOT BY ARCHITECTURE DISCUSSION

These questions are known at lock time. Their existence does not reopen the architecture.

### OQ-001 — Golden Section source DWG
Where is the complete source package for `VSC-UT-ALL-00NTZ_BG-PD-500X.dwg`?

### OQ-002 — Golden utility entity types
Which visible utilities are native Pipe/Pressure Networks and which are projected AutoCAD/FeatureLine objects?

### OQ-003 — CL numbering
Does section number `1086` mean station `1+086`, and what is the authoritative numbering rule?

### OQ-004 — CL tolerance
What drafting/intersection tolerance is approved when CL does not mathematically intersect an alignment?

### OQ-005 — Golden section layout
Is the final sheet composed in Model Space, Paper Space, or another Civil sheet workflow?

### OQ-006 — Golden styles
Which exact Sample Line / Section View / Band / Label styles and template source are authoritative?

### OQ-007 — LandQ implementation contract
What is the current repository/service/API/schema state of LandQ? Is the planned QPKG contract actually implemented and sufficient?

### OQ-008 — Golden price book
Which exact price-book snapshot/version supplied the Judgment-2 Golden prices?

### OQ-009 — 0.90 adjustment
What does the observed 0.90 quantity relationship represent, what is its scope, and what source approves it?

### OQ-010 — Quantity_Cost
Which Modiin `Quantity_Cost` file is the authoritative calculation/pricing source for early design?

### OQ-011 — Modiin quantity-source rules
Which layers/Civil codes/entity types correspond to the first 5–10 Gate-B items?

### OQ-012 — XREF quantity policy for 6422
Does Modiin follow host-only quantity sourcing, or are selected XREFs authoritative? Do not inherit LandQ landscape E3 blindly.

### OQ-013 — supported Civil versions — RESOLVED
One codebase must support both Civil 3D 2026 and 2027 using the existing MahodAI version-targeting strategy. GUI-test claims remain version-specific: use 2027 for the first local smoke where available and obtain a real 2026 smoke on a healthy 2026 workstation before claiming 2026 GUI validation.

---

# 22. CLAUDE CODE EXECUTION INSTRUCTION

Claude Code must execute this plan in order.

Before coding:

1. read this file;
2. read repository `CLAUDE.md`;
3. verify current main SHA;
4. run current full suite;
5. create the feature branch;
6. perform Gate 0 read-only against Input Package v1;
7. continue automatically into the next unblocked phase.

Do not return a Gate-0-only memo and wait for another architecture approval.

After Gate 0:

```text
Gate A first.
MHD_SECTIONS must not wait for LandQ or the Ben-Gurion source DWG.
```

After Gate A is green:

```text
wire Sections MahodAI parity
-> build Nataly Sections release package
-> continue Gate B
```

After Gate B is stable:

```text
wire Estimate MahodAI parity
```

Gate C closes Golden claims to the extent the required Golden evidence is actually available.

Final Gate D performs combined MahodAI parity/regression; it does not delay the first Sections release.

No alternative architecture proposals are requested after this locked plan.

If a reproducible fact contradicts a required implementation assumption:

```text
STOP THE AFFECTED GATE
DOCUMENT THE EVIDENCE
DO NOT INVENT A FALLBACK
```

The standard for every claimed PASS is:

```text
source
+ deterministic run
+ test
+ evidence artifact
+ actual result
```

Not:

```text
"implemented"
"looks correct"
"the API should work"
"the AI produced an answer"
```

---

# 23. FINAL LOCK

The architecture is closed:

```text
MHD_SECTIONS
= Civil-native Section Composer
= independent of LandQ
= independent of LLM

MHD_ESTIMATE
= Civil Quantity Adapter
-> Neutral Quantity Records
-> verified LandQ backend
= no second estimate engine

Modiin 6422
= Project Profile / first production case
= not a product fork
```

**Build/release order remains:**

```text
Gate 0
-> MHD_SECTIONS Gate A
-> Sections MahodAI parity
-> NATALY SECTIONS RELEASE
-> MHD_ESTIMATE Gate B
-> Estimate MahodAI parity
-> Golden Gate C (only claims supported by available Golden evidence)
-> final combined MahodAI Gate D
```

**Host model:**

```text
MHD_SECTIONS
= direct Civil 3D command/ribbon (works without AI)
+ MahodAI orchestration over the same deterministic services
= Civil 3D 2026/2027 one codebase
!= plain-AutoCAD Section Composer
!= standalone Windows EXE

MHD_ESTIMATE
= direct Civil 3D workflow
+ MahodAI orchestration over the same deterministic services
= Civil Quantity Adapter
-> Neutral Quantity Records
-> verified LandQ backend
```

End of locked build plan.
