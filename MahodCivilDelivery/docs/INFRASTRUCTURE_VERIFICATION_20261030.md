# Infrastructure BOQ — Arthur's verification ledger

Target: Sunday, 1 November 2026. Status: IN PROGRESS, NOT ACCEPTED.

Base: `origin/codex/infrastructure-boq-20261030` (`94d82ad`). Worktree: `C:/WORK/infra-boq`, branch `codex/infrastructure-boq-polish-20261030`.
Existing MahodAI workspace changes are retained. No parent pointers, pushes or releases are authorized by this work.

| ID | Finding | Change | Verification |
|---|---|---|---|
| INF-001 | Changing an inferred topic updated the BOQ group but left DWG layer names and source receipts under the old topic. | Apply the reviewed category to DWG output and source receipts without changing measured geometry. | New regression test passed; 11 infrastructure tests passed. Live verification pending. |
| INF-002 | Infrastructure screen always loaded NTI; other imported price books could not be selected. | Excel price-book selection, visible identity/edition, explicit replacement confirmation and withdrawal of approvals when hash changes. | LIVE VERIFIED: selected synthetic Excel catalogue, confirmed replacement, active label changed to BOQ-IMPORT-TEST with one item. Approval withdrawal branch not yet manually exercised. Import does not establish official provenance. |
| INF-003 | The integrated MahodAI project lacked the newly required XLS reader and embedded NTI workbook. | Added ExcelDataReader 3.8.0 and the exact resource name required by BundledPriceBooks. | 2026 and 2027 builds pass; live 2027 UI loads 9,783 NTI items. |
| INF-004 | Igor comparison existed only behind standalone audit commands, absent from the infrastructure review UI. | Added an explicit read-only comparison action and export receipt. Missing DLL, scope limitations and not-run states remain explicit. | Live verification pending. Only host Model Space LWPOLYLINE is supported by the original routine. |
| PRES-001 | Sync could regress the existing native Database identity fix in three chat-tool files. | Carried upstream commit 5b5a109 into this branch before syncing. Remaining baseline differences were line endings. | Source comparison verified; chat smoke test pending. |
| INF-005 | LIVE: reopening the exported metre-coordinate DWG in Civil 3D yields INSUNITS=Feet. The export set INSUNITS but inherited imperial MEASUREMENT. | Sets MEASUREMENT=Metric and adds unit-metadata checks to the export readback gate. | FIXED, LIVE VERIFIED: fresh process export/reopen reports Meters, factor 1; independent verifier PASS, INSUNITS=6, MEASUREMENT=1, all 12,000 lengths preserved. |
| INF-006 | LIVE: exported DWG initially opens off-network and all newly created layers have default white color. | Assign category colours and save a viewport centred on exported extents. | FIXED, LIVE VERIFIED: reopened Corrected-lines.dwg shows coloured network immediately. |

## External blockers and reference evidence

- Official NTI workbook already present in the starting branch: July 2026, SHA-256 `884B052CE443546F0FD9F6CC18F478107CB8C3D19A5AD9039DE951845E549613`; 9,783 items. Existing test confirms hash, item count and corrected rate for 02.01.0769. Today's attempt to recheck the official page was blocked by Imperva. Do not claim a new independent official download.
- Israel Railways tender site: Cloudflare access denied on 30 October. Latest official construction price list not obtained or verified.
- Defense supplier portal: Imperva access denied on 30 October. Latest official construction price list not obtained or verified.
- mahod2: prior Codex chat identifies the legacy office document/email/project system. It found `plugin/MahodAI.Civil3D.Plugin/assets/civil-delivery/profiles/6422/judgment2-golden.xlsx`, a completed roads/traffic comparison example (46 rows, 8 columns). Opened read-only: catalog code, description, unit, quantity, unit price and amount columns are present. This is NOT verified as the requested mahod2 infrastructure example. Its unexplained 0.9 factor must not be copied into pricing.
- Facebook reference resolved to https://www.facebook.com/reel/913784554999060. Public caption and visible frames describe sewer BOQ by diameter/depth, reorganizing lines and manholes by material/diameter/depth, and mapping layers to price items. This goes beyond the current grouped line-length implementation. Automatic extraction of diameter/depth and manhole quantities is NOT verified or implemented by this patch. Login overlay later interrupted viewing.

## Acceptance gate status (latest retest)

- PASS: representative 12,000-polyline drawing with independent expected quantities.
- PASS: updated integrated plugin loaded in live Civil 3D 2027.
- PASS: manual scan, specification review, NTI item search/approval and export.
- PASS: reopened DWG and Excel reconcile counts, units, lengths, code, description, price and totals.
- PARTIAL: recorded export demonstration; not a recording of every acceptance gate.
- PARTIAL: Civil Delivery sections/estimate tabs and MahodAI drawing-read smoke checks; full road-project regression remains pending.
- PASS: replacement Excel price-book selection; approval-withdrawal manual test still pending.
- BLOCKED: actual Igor comparison requires original loaded DLL.
- BLOCKED: official priced Railways/Defense books and exact mahod2 infrastructure example.

The initial failed run below is retained as history. The successful retest supersedes INF-005/006 failures. No claim of complete original-scope acceptance.

## Live verification receipts, 30 October 2026

- Both integrated C# targets built with zero errors. Logs: `C:/WORK/infra-boq/build-plugin-2027.log` and `build-plugin-2026.log`.
- Integrated Core tests: 2,918 passed; filtered Plugin tests: 1,606 passed; infrastructure tests: 11 passed.
- Temporary side-by-side build loaded into Civil 3D 2027; MAHOD_INFO reports 2.1.16 (15e6c30). Original installed bundle manifest was restored after launch. No release published.
- Representative drawing: `C:/WORK/infra-boq/live-20261030/Infrastructure-12000.dwg`. Six utility layers, each 2,000 four-vertex polylines, each 205m. Expected total 2,460,000m.
- Manually opened infrastructure tab and accepted metre units. Live scan reports all six categories, each 2,000 lines / 410,000m, zero scan findings. Saved clearly marked TEST ONLY water specification and planned status.
- Igor button executed: `unavailable`, original MAHOD CalcPolylines assembly not loaded. This is a blocker, not a passed Igor comparison.
- Manual Excel/DWG export succeeded at `C:/Users/vadim/Desktop/כתב כמויות תשתיות-20261030-120334-4cafe0`. Includes XLSX, DWG, 12,000 source records, review JSON and explicit unavailable Igor receipt.
- Opened XLSX in actual Excel: three sheets, six quantities of 410,000m; unapproved item codes/rates/totals blank; partial total 0. Screenshot `C:/WORK/infra-boq/live-20261030/excel-draft.png`. This is an UNPRICED DRAFT, not a completed BOQ.
- Exported DWG copy opened in Civil 3D as `Exported-lines.dwg`. Initial view is empty/off-network; Zoom Extents displays the full grid. A fresh infrastructure scan then reports INSUNITS=Feet (0.3048): rejected this incorrect interpretation. Independent remeasurement is blocked by INF-005. Exporter's original internal readback checked numeric lengths but did not check unit metadata.
- Nested catalog picker opens, but desktop automation input did not populate search. No priced item approval was completed. Input investigation found that text injection uses clipboard paste (AutoCAD interprets this as PASTECLIP outside a focused text edit); individual physical key presses work for native commands. No product search failure is established.
- `draft-live-demo.mp4` is a 45-second diagnostic recording of the attempted reopen. It is NOT an accepted demonstration of the complete workflow.
- Existing Civil Delivery sections/estimate and MahodAI chat live regressions remain pending. Automated test passes do not replace them.

Sunday acceptance remains at risk until official Railways/Defense books, the exact example, Igor runtime and the remaining manual gates are resolved.

## Additional access attempts

- At the owner's request, fetched the Defense portal through the existing Mahod server. Official page `https://www.online.mod.gov.il/Online2016/pages/general/SapakInfo/Binuy.aspx` was accessible and marked updated 26/10/2026 02:56:36. Downloaded its linked `Zip/Binuy.zip` (2,229,253 bytes) to `C:/WORK/infra-boq/live-20261030/mod-binuy-20261026.zip`. The inspected chapter files are UTF-8 HTML tables with `.xls` names; columns are code, description, unit and update date. No unit-price column was present in the inspected chapter. This is an official item catalogue, not verified pricing. Railways remained HTTP 403 from the server.
- At the owner's subsequent request, enabled the already installed Cloudflare WARP VPN. CLI reported Connected and Cloudflare trace confirmed `warp=on` (UA/KBP). Retried Railways tender page, NTI price-list page and Defense construction-items page: all HTTP 403. Saved responses as `vpn-rail.html`, `vpn-nti.html`, `vpn-mod.html` under the live evidence directory.
- Local Chrome fallback could not run: desktop input returned Access Denied. No browser challenge was completed or bypassed.
- Restored WARP to its original disconnected state and verified the status. Existing Tailscale had no available exit nodes; no routing configuration was changed there.

## Successful unit-fix retest and priced export

- Fresh Civil 3D 2027 process loaded the updated test DLL. Original installed manifest restored after loading; installed release remains unchanged.
- Independent source verifier: `live-20261030/Infrastructure-12000.dwg.verification.csv` PASS.
- Manual specification review and catalogue search succeeded. Approved one explicitly synthetic TEST ONLY electrical duct group against NTI item `08.10.0906`, flexible double-wall plastic duct diameter 200 mm, unit metre, rate 45 NIS. This is fixture validation, not engineering approval for real works.
- UI export succeeded to `C:/Users/vadim/Desktop/כתב כמויות תשתיות-20261030-130130-bb11d0/`.
- Reopened output copy `live-20261030/Corrected-lines.dwg` in Civil 3D. Network visible with category colours without zoom commands. Independent LISP verifier PASS: INSUNITS=6, MEASUREMENT=1, six layers with 2,000 polylines and 410,000m each; total 12,000 / 2,460,000m. Receipt `Corrected-lines.dwg.verification.csv`.
- Reopened infrastructure workflow reports Meters and factor 1. Rescan reproduces six categories / 410,000m each. INF-005 and INF-006 closed on live evidence.
- Independent XLSX validation compared item code, Hebrew description, metre unit and rate against the bundled official XLS using xlrd/openpyxl. Six quantities 410,000; H10=F10*G10 and cached amount 18,450,000 NIS; H13 partial total 18,450,000; source trace 12,000 records / 2,460,000m. Receipt `live-20261030/priced-verification.json`.
- Opened `live-20261030/Verified-test-BOQ.xlsx` in actual Excel. Visually confirmed item 08.10.0906, quantity 410,000, rate 45 and amount 18,450,000. Five unapproved categories remain unpriced and marked for review; workbook remains a draft.
- `live-20261030/priced-export-demo.mp4` records 45 seconds of the reviewed group and export action. Sampled frame reviewed for readability. This clip does not prove missing catalogue/Igor/full regression gates.
- Existing Civil Delivery sections and estimate tabs open correctly against the fixture. Sections correctly show no axes/surfaces. This is a smoke check, not full section-generation validation on a road project.
- Hebrew plugin help and Hebrew/English/Russian web help now explain the infrastructure command, unit review, catalogue selection, approval, export and Igor/depth/manhole limitations.

- MahodAI live smoke: chat opened and a synthetic fixture query returned 12,000 drawing entities and zero Civil 3D pipe-network objects, correctly distinguishing polylines from networks. This validates a read path only. Desktop clipboard injection submitted the earlier TEST ONLY fixture description rather than the intended generic summary prompt; the actual visible response, not intended input, is the evidence.
- Final geometry code builds: 2026 `build-export-polish-2026.log`, 2027 `build-export-polish-2027.log`, zero errors. After help update, 2027 filtered Plugin tests rebuilt and passed 1,606/1,606 (`plugin-final-tests-2027.log`). Core 2,918 and focused infrastructure 11 previously passed for unchanged pure logic.

- Catalogue import live check passed using `BOQ-IMPORT-TEST.xlsx`, explicitly synthetic. Confirmation showed one parsed item; active identity/count changed correctly. Closed this test session without exporting against the synthetic book. Screenshot `live-20261030/catalogue-import-live.png`.
- Restored FILEDIA=1 after script checks. Left the verified workbook and Civil 3D open. Evidence screenshot `live-20261030/excel-priced-live.png`. No production release, push or parent-pointer change.
