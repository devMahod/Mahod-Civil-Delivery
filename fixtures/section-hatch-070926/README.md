# Current local HA — PLAN44 endpoint-closure regression

This is immutable, partial-reader diagnostic data, not native Civil acceptance or repaired DWG geometry. No source DWG, profile or server path was changed or written.

- Local source: `C:/Users/arthurf/MahodCivilDelivery_Work/6422-local-mirror/Drawing/Data Files/HW/PD/6422-HA-MODEL-NATAZ.dwg`.
- Source SHA-256 before and after: `5C23EC4AEF9ECC3A6A3CF75EDAB8B9ADCA60B7344AA941ACF1EB9B1186290539`.
- Selection: all seven distinct global `SEC-PROJECTION-GEOMETRY-UNSUPPORTED` hatch findings in `sections-plan-20260907-114112-6a6d1f6e/section_plan.json`. Each source handle and checksum was matched before extraction.
- Raw fixture `ha-7-plan44-failures.acadsharp-partial.json`, SHA-256 `DCB2F17980C0E6E211D8C68E8AEB63B9C79F8E382447EE59839045FC330AF6D4`.
- Pure managed ACadSharp 3.7.1, `FileAccess.Read`/`FileShare.Read`, no XREF traversal, writer or native CAD. All 623 notifications are preserved: 27 errors and 596 warnings. Full DWG readability is not claimed.
- Original September6 fixture SHA `F2141DA8...` and source SHA `8F2469ED...` remain untouched and distinct. No historical raw geometry was substituted for the current file.

## Exact current 262391 evidence

One `External` loop (not `NotClosed`), 105 ordered Line/Arc edges, Outer fill style, normal `(0,0,1)`, elevation0. The final Arc endpoint is `(204611.6913784412,649504.586182021)`; the first Arc endpoint is `(204611.6913786193,649504.5861818772)`. Their distance is **2.2887856434147577e-7 m**, or **0.229 micrometres**. Every interior join differs by at most `2.9103830456733704e-11 m`.

The default `JoinClosedEdges` API and its 1e-7m threshold are unchanged. The native adapter opts into `JoinClosedEdgesWithEndpointTolerance` with a hard maximum of **1e-6m**, only after the existing NotClosed/SelfIntersecting/Duplicate/Text loop flags have been rejected. This is endpoint coincidence recognition, not a wider polygon, seam, coverage, dimension, area or ROW tolerance. It retains the original first endpoint; no averaging, source edit or hole removal occurs. Every complete loop still passes unchanged region-validity/topology checks.

The source-linked isolated replay passed **10 behavioral cases**: current262391 complete region valid; the other six current fixture hatches still fail; current disconnected loops remain rejected; hole stays empty; selfintersection still fails; 1.01micrometre, millimetre and metre closure/interior gaps fail; callers cannot expand the bound. Current262391's whole loop is retained, rather than dropping an invalid fragment. Native `Curve2d.EvaluatePoint` parity and a new real PLAN remain separate acceptance steps.

This corrects one reader rejection. It does not approve the three still-unnamed spans in `cl-7D23`, resolve other HA failures or clear the independent global annotation-registry conflict.

## Reproduction and limits

Extractor: `C:/Users/arthurf/Downloads/Cutz/Work/review-070926/ha-closure-5c23/CurrentHatchFixture.csproj`; refuses to overwrite its fixture. Re-running requires a new output path, not replacing this immutable file.

Pure source-linked replay: `C:/Users/arthurf/Downloads/Cutz/Work/review-070926/ha-closure-5c23/replay/Replay.csproj`; executed with `dotnet run --project <that path> -c Release --nologo`. It compiled only its isolated output, linked the current helper/region implementation and actual `SectionHatchMicrometricClosureTests.cs`, and referenced the staged44 Core for unchanged dependencies. Its explicit CS0436 suppression is limited to the isolated source overrides; it is not a product build or a native test. Root owns the actual product test/build lanes.

Raw arc endpoints use the same documented signed-angle diagnostic as the older fixture; the native adapter uses its real StartPoint/EndPoint/EvaluatePoint APIs. Partial-reader evidence establishes the exact current stored geometry and bounded replay, not complete native geometry acceptance.
