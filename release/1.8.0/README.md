# Mahod Civil Delivery 1.8.0

The simple bill-of-quantities workspace now offers **שייך אוטומטית**. One click assigns each eligible, visible, unmapped layer to its recommended item in the selected catalogue edition and category. The layer-name filter controls the batch scope. The item-search query belongs to the selected layer and is never applied to other layers. Existing assignments and excluded rows are preserved. Rows without a layer-topic recommendation, compatible quantity unit or positive quantity remain unmapped.

Manual selection uses the same ranked recommendation: one matching item receives a green background, bold text and a **מומלץ** badge beside its code. The badge remains readable when selection moves. Displaying or selecting a recommendation does not assign it; the manual or automatic assignment action does. A category or exact-code search alone cannot manufacture a layer recommendation for an unknown or unrelated layer. Auxiliary length layers are not automatically assigned through a category filter.

**בטל שיוך אוטומטי אחרון** reverses the last batch without removing subsequent manual decisions. Automatic origin survives saved-review restore and appears in exported Excel row status. Existing review files remain compatible. Catalogue prices and measurement rules are unchanged; no missing engineering specification or quantity is invented. No approver-name or confirmation form is added.

## Validation

Validation receipts, installer checks and native installation status will be recorded after the frozen-source build. Host-free tests cover recommendation/assignment consistency, chosen catalogue/category, scope, unit compatibility, preserving existing editions, planning without mutation, undo, old/new saved reviews and exported prices. WPF rendering checks cover the badge and readable green emphasis with and without selection.

Private catalogue snapshots, project archives and generated research output remain local and are not distributed or added to Git. The installer targets only the per-user application bundle and requires Civil 3D to be closed.
