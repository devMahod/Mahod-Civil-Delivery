# HA hatch failures — immutable recovered-source regression fixture

These two JSON files are **raw diagnostic data and derived checks**, not repaired geometry or native-Civil acceptance. No source DWG was changed and no native CAD/API was launched.

## Identity and selection

- Source: `C:\Users\arthurf\Downloads\Cutz\Materials\060926\6422-HA-MODEL-NATAZ.dwg`, 7,165,079 bytes.
- SHA-256 before and after: `8F2469EDD9F9D25FCC09C5563FAC1C2BD9B3CE17A9B06D1E9DAE032CF8175315`.
- Selection: all 27 `SEC-PROJECTION-GEOMETRY-UNSUPPORTED` findings in `sections-plan-20260906-122749-bc9cbdcc/section_plan.json`. Every finding's source checksum was checked against this same DWG. Every final source-entity handle was found uniquely in its recovered model space. Exact native finding text and provenance are embedded.
- Raw fixture: `ha-27-failed-hatches.acadsharp-partial.json`, SHA-256 `F2141DA8DF1675E8E5E68E27744550DF8892E3C5A693B97B11B5AEBB1F2B013C`.
- Derived checks: `ha-27-extraction-checks.json`, SHA-256 `8F0B07588BA9456032361ED44B922CDC59651ADD2207CD15AD86A88A45E280F1`.

The exporter refuses to overwrite either immutable output. A later interpretation or correction must use a new file/version; do not silently edit source coordinates or failure evidence.

## Data contract

All 27 hatches (26 `HW_HA_SIDEWALK`, one `PL-BIKE`) retain 54 ordered loops, their raw type flags and numeric flag values, source handle, layer, style, elevation and normal. Edges comprise 17 polylines, 259 circular arcs and 520 lines. All 82 polyline vertices are present in original order, with no deduplication or closing-point removal.

ACadSharp's `Hatch.BoundaryPath.Polyline.Vertices` is `XYZ`: **X/Y are hatch-OCS coordinates, Z is the vertex bulge, not elevation**. The installed package XML explicitly documents this. The fixture stores both raw XYZ and named X/Y/bulge, and verifies named bulges against the separate `Bulges` API. Hatch elevation and normal remain separate. All selected polyline coordinates/bulges are finite.

Arc center/radius/start/end angles/orientation are stored without alteration. Two endpoint representations are retained:

1. `signed_angle_start/end`: oriented loop diagnostics using angle for counter-clockwise edges and negative angle for clockwise edges.
2. `polygonal_api_endpoints`: independent ACadSharp `PolygonalVertexes(16)` first/last values. For clockwise arcs this API returns the opposite endpoint order, so do **not** silently treat those as the loop's oriented start/end.

Derived endpoint gaps use the first convention and preserve the actual endpoint coordinates in every join. This is not a substitute for native `Curve2d.EvaluatePoint` parity testing. No curve tessellation, repair, snap, simplification or auto-closing has been applied to the raw fixture.

## Findings useful for regression tests

All **12 native `chordLength` failures have positive finite raw source bulged chords**. None of the recovered raw polylines has a zero-length bulged chord. Minimum bulged chords range from 0.07664741261357876 m (`26239C`) upward. Therefore these raw files do not justify deleting a supposedly coincident source vertex. A possible additional native closing vertex is an adapter hypothesis, not established by this offline extraction.

`7D49`, loop 2, consists of a circular arc and its chord; both endpoint joins are exactly zero in the recovered diagnostic. This provides a concrete curved two-edge region regression rather than an open-contour fixture.

Some other loops genuinely fail the recovered endpoint checks: `26236C` loop 8 has a 14.295943911445237 m gap; `262379` loop 1 has 5.659493518857625 m; `26237C`, `26237E`, `262393`, and `262399` have millimetre-to-decimetre gaps. Conversely `262391` and `262398` have closure differences around 0.2 micrometres (about twice the current 1e-7 m threshold). Do not use one broad tolerance or ignore all `NotClosed` flags. Exact per-loop diagnostics are in the checks JSON.

All raw loops for the invalid-simple-region and touching-loop failures are retained, including holes and curve edges; this extraction does not decide which topology repairs are legitimate.

## Partial-reader limitation

ACadSharp 3.7.1 ran with `Failsafe=true`, unknown-object retention, ignored proxy graphics and CRC checks disabled, matching the earlier diagnostic reader. It reported **623 notifications**, all embedded: 27 errors (one `Prototype1b` failure and 26 scale-list entry errors) and 596 warnings. Recovering these selected 27 objects does not establish a complete DWG read, validated Civil geometry, quantity, or usable price mapping.

The local exporter and verification script are at `C:\Users\arthurf\Downloads\Cutz\Work\review-060926\failed-hatch-fixture`. Only that isolated console was built. It used `FileAccess.Read`/`FileShare.Read`, followed no XREF, checked source and runtime selection identities, and wrote new fixture files only. The read-only checker verified 27 matches, 12 chord-failure cases, 82 finite polyline vertices, matching bulge APIs, and zero raw invalid bulged chords. No product build or native acceptance test was run by this extraction task.
