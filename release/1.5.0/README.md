# Mahod Civil Delivery 1.5.0

Local candidate. This release adds a separate infrastructure quantity review to the existing sections and estimate workflows.

- Open the **תשתיות** tab or run `MCD_INFRASTRUCTURE`.
- Confirm physical drawing units. The scan covers Model Space and loaded XREF occurrences, excluding named legend sources and ordinary symbol blocks. It does not apply the estimate tab's polygon boundary.
- Review grouped layers by proposed infrastructure topic. Confirm construction status and specification before assigning a metre-based catalogue item through Hebrew search.
- Exclude irrelevant groups reversibly. Export an unpriced partial draft at any time. Approved assignments with catalogue prices receive quantity × rate formulas.
- Export creates a new folder with the workbook, an independent DWG, entity source trace and review JSON. Review JSON is an audit record; it is not automatically reloaded into another session.
- The DWG is reopened internally and its per-layer counts and lengths must match the measurements before the exporter reports success. Use Zoom Extents when opening the independent drawing.

The official [price-book page](https://www.iroads.co.il/מידע-לספקים/מחירון-לעבודות-סלילה-וגישור/) displayed July 2026 as the current unified edition on 30 October 2026. The fresh download is bundled byte-for-byte with SHA-256 `884B052CE443546F0FD9F6CC18F478107CB8C3D19A5AD9039DE951845E549613`. It contains 9,783 items; 2,230 have no price. Compared with the previous bundle, item `02.01.0769` has rate 41 rather than 275. Existing project registrations retain their original snapshots and hashes. This is a dated verification, not an automatic future update.

`MCD_INFRA_AUDIT` creates an unapproved technical run under the current user's Documents / Mahod Delivery / Infrastructure. `MCD_IGOR_AUDIT` invokes the installed MAHOD DLL's read-only LWPOLYLINE calculation for comparison with native lengths in the active drawing. Its scope excludes 3D polylines, other curve types and XREF traversal. An absent or incompatible DLL is recorded as unavailable, never as a passing comparison.

The regular length calculation uses AutoCAD's native curve distance. It does not infer pipe diameter, material, excavation depth, centreline semantics, duplicate removal, or new construction from a layer name. Ordinary blocks require a separate count workflow for manholes and fittings.

Validation: both Civil 3D 2026 and 2027 builds, 65 targeted core/export regression tests, live Civil 3D 2027 source traversal and DWG read-back, installed Igor DLL comparison, workbook source reconciliation and rendered workbook review. The comparison receipt is private to each local run, outside source control.

Installer outputs are generated under `MahodCivilDelivery/out/distribution`. Setup payload self-verification and Defender checks must pass. Automatic plugin loading also depends on the host's existing APPAUTOLOAD setting; the installer does not alter it.
