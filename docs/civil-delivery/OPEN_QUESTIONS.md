# OPEN QUESTIONS — Mahod Civil Delivery

Status legend: OPEN / EVIDENCE_COLLECTED / RESOLVED / BLOCKED
Updated: 2026-08-18 (Gate 0 v1)

| ID | Question | Status | Evidence / notes (2026-08-18) |
|---|---|---|---|
| OQ-001 | Ben-Gurion Golden source DWG package location | OPEN | Only the plotted PDF is in Input Package v1. Plotted from Civil 3D 2023 on 2024-09-04 (F-007) — a source DWG demonstrably existed. Not a Gate-A blocker. |
| OQ-002 | Golden utility entity types (native networks vs projected) | OPEN | Unanswerable from PDF. Needs source DWG. |
| OQ-003 | CL numbering rule (`1086` == station `1+086`?) | OPEN | Needs CL.dwg object-level pass in Civil session. |
| OQ-004 | Approved CL/alignment intersection tolerance | OPEN | Needs Civil session + Nataly confirmation. |
| OQ-005 | Golden sheet composition space (Model/Paper) | OPEN | PDF shows ~3-col grid of paired sections + plan index map (F-007); space/mechanism unknown without DWG. |
| OQ-006 | Authoritative styles/bands/labels + template source | OPEN | Needs source DWG/DWT. `6422-HW-CS.dwg` is the current-project style evidence to inventory first. |
| OQ-007 | LandQ current contract/repo/service state | BLOCKED | No local repo; 4 GitHub name candidates 404 (see LANDQ_CONTRACT_AUDIT.md). Status: `LANDQ_CONTRACT_UNVERIFIED`. |
| OQ-008 | Golden price-book snapshot identity | EVIDENCE_COLLECTED | **Proven NOT 08/2025**: 0/31 price matches, per-item ratios inconsistent, 2 J2-priced items are `-` in 08/2025 (F-003). Actual snapshot still unknown → `GOLDEN_PRICE_SOURCE_MISSING`. |
| OQ-009 | 0.90 adjustment meaning/scope/approval | EVIDENCE_COLLECTED | **31/31 J2 quantities are exactly X×0.9** (F-004). Meaning/approval unknown; remains `UNVERIFIED_PROJECT_RULE`; needs `Quantity_Cost`. |
| OQ-010 | Authoritative `Quantity_Cost` file for early design | OPEN | Not present in Input Package v1. Priority request to Arthur/Nataly. |
| OQ-011 | Layers/codes/entity types for first 5–10 Gate-B items | OPEN | Candidate items now known from J2 (kerbs=מטר, milling=מ"ר, מצע=מ"ק, shelters=יח', markings). Source geometry mapping needs Civil session on PD drawings. |
| OQ-012 | 6422 XREF quantity policy (host-only vs selected XREFs) | OPEN | Needs Civil session + project convention. |
| OQ-013 | Supported Civil versions | RESOLVED (plan) | One codebase 2026+2027; first GUI smoke on 2027. |
| OQ-014 | `ACAD-HW-CL-M34.dwg` is 0 bytes — is this file needed at all? | OPEN | F-001. If needed → request exact re-copy from `P:\data\6422`. Not a Gate-A blocker (CL.dwg present). |
| OQ-015 | Approval to install .NET SDK 10.0.3xx on this workstation | RESOLVED | Arthur authorized via EXECUTE_TO_PRODUCT_NOW directive (2026-08-18); SDK 10.0.400 installed via winget; baseline green. |
