using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates a Profile View for an alignment — visual grid showing EG and FG profile lines.
    ///
    /// Civil 3D API:
    ///   ProfileView.Create(alignmentId, insertionPoint, name, bandSetId, styleId)
    ///   Uses reflection to handle signature variations across Civil 3D versions.
    ///
    /// The Profile View automatically displays all profiles associated with the alignment.
    /// </summary>
    public class CreateProfileViewTool : DrawingToolBase
    {
        public override string Name => "create_profile_view";
        public override string Description =>
            "Creates a Profile View (visual grid) for an alignment showing EG and FG profile lines. " +
            "The view is placed in the drawing at the specified location or automatically below the alignment.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment to create profile view for""
                },
                ""insert_x"": {
                    ""type"": ""number"",
                    ""description"": ""X coordinate for insertion point (default: alignment start X)""
                },
                ""insert_y"": {
                    ""type"": ""number"",
                    ""description"": ""Y coordinate for insertion point (default: 200m below alignment start Y)""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Profile View name (default: 'PV - <alignment>')""
                },
                ""band_set_style"": {
                    ""type"": ""string"",
                    ""description"": ""Name (or substring) of the ProfileView band-set style to apply (the footer strip). Defaults to a clean preferred style; if absent, the first available is used. Available names are returned in 'available_band_set_styles'.""
                },
                ""profile_view_style"": {
                    ""type"": ""string"",
                    ""description"": ""Name (or substring) of the ProfileView style (the grid). Defaults to a preferred style, else the first available. Available names are returned in 'available_profile_view_styles'.""
                },
                ""force_new"": {
                    ""type"": ""boolean"",
                    ""description"": ""Create a NEW profile view even when the alignment already has one (default: false — an existing view for the alignment is reused and returned with reused=true).""
                }
            },
            ""required"": [""alignment_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");
            var insertX = GetDoubleParam(parameters, "insert_x");
            var insertY = GetDoubleParam(parameters, "insert_y");
            var pvName = GetStringParam(parameters, "name") ?? $"PV - {alignmentName}";
            var bandSetStyleName = GetStringParam(parameters, "band_set_style");
            var profileViewStyleName = GetStringParam(parameters, "profile_view_style");
            var forceNew = GetBoolParam(parameters, "force_new");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find alignment
            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return ToolResult.NotFound("Alignment", alignmentName);

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to read alignment");

            // ── Idempotency: reuse the alignment's existing Profile View ──────────
            // Civil 3D draws every profile of an alignment into ANY profile view of that
            // alignment, so a second view is pure duplication — the engineer ends up with
            // an empty-looking grid next to the one they already had. Unless the caller
            // explicitly asks for force_new, hand back the existing view.
            if (!forceNew)
            {
                var existingPv = FindProfileViewForAlignment(tr, alignmentId.Value, pvName);
                if (existingPv != null)
                {
                    int existingProfileCount = 0;
                    foreach (ObjectId _ in alignment.GetProfileIds()) existingProfileCount++;

                    double exX = 0, exY = 0;
                    try
                    {
                        var ext = ((Autodesk.AutoCAD.DatabaseServices.Entity)existingPv).GeometricExtents;
                        exX = ext.MinPoint.X;
                        exY = ext.MinPoint.Y;
                    }
                    catch { /* extents are informational only */ }

                    return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["reused"] = true,
                        ["already_existed"] = true,
                        ["profile_view_name"] = existingPv.Name,
                        ["creation_method"] = "reused_existing",
                        ["alignment_name"] = alignmentName,
                        ["profiles_displayed"] = existingProfileCount,
                        ["insertion_point"] = new { x = Math.Round(exX, 2), y = Math.Round(exY, 2) },
                        ["placement"] = BuildPlacementPayload("reused_existing", 0, 1, new List<string>()),
                        ["collision_warnings"] = new List<string>(),
                        ["station_range"] = new
                        {
                            start = Math.Round(alignment.StartingStation, 2),
                            end = Math.Round(alignment.EndingStation, 2)
                        },
                        ["message"] = $"Profile View '{existingPv.Name}' already exists for alignment " +
                            $"'{alignmentName}' — reused it (showing {existingProfileCount} profiles). " +
                            "Pass force_new=true to create an additional view."
                    }));
                }
            }

            // Determine insertion point
            double x, y;
            string placementMethod;
            var placementWarnings = new List<string>();
            int scannedEntityCount = 0;
            int pvCount = 0;

            // Anchor: alignment start point (used as the preferred X column and as the
            // last-resort fallback origin — never a hardcoded coordinate).
            double refX = 0, refY = 0;
            try { alignment.PointLocation(alignment.StartingStation, 0, ref refX, ref refY); }
            catch (Exception ex)
            {
                placementWarnings.Add($"Alignment start point lookup failed: {ex.Message}");
            }

            if (insertX.HasValue && insertY.HasValue)
            {
                x = insertX.Value;
                y = insertY.Value;
                placementMethod = "explicit";
            }
            else
            {
                // Default: scan extents of ALL modelspace entities (alignments, tables,
                // blocks, section views, text — not just ProfileViews) and choose a free
                // rectangle below the whole drawing. Existing-PV stacking is honoured via
                // the gap computation in ProfileViewPlacement.Choose.
                try
                {
                    var db = HostApplicationServices.WorkingDatabase;
                    var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .DocumentManager.MdiActiveDocument?.Editor;

                    var allExtents = new List<ProfileViewPlacement.Rect>();
                    var pvExtents = new List<ProfileViewPlacement.Rect>();

                    // Robustness window around the alignment. A stray entity far from the road
                    // (classically a block left at the WCS origin while the survey sits in Israeli
                    // ITM coordinates ~200 km away) would otherwise dominate the measured drawing
                    // extents and fling the new PV tens of km off the drawing. We keep only entities
                    // within a generous buffer of the alignment's own bounding box, so placement
                    // reflects the survey AROUND the road, not a distant outlier. The buffer is far
                    // larger than any real survey (≥50 km, or 5× the alignment length) yet far
                    // smaller than an origin outlier's distance, so genuine geometry is never dropped.
                    double aMinX = refX, aMinY = refY, aMaxX = refX, aMaxY = refY;
                    try
                    {
                        var aext = alignment.GeometricExtents;
                        aMinX = aext.MinPoint.X; aMinY = aext.MinPoint.Y;
                        aMaxX = aext.MaxPoint.X; aMaxY = aext.MaxPoint.Y;
                    }
                    catch { /* fall back to the start point as a degenerate bbox */ }
                    double alignSpan = Math.Max(aMaxX - aMinX, aMaxY - aMinY);
                    double keepBuffer = Math.Max(50000.0, alignSpan * 5.0);
                    var keepRegion = new ProfileViewPlacement.Rect(
                        aMinX - keepBuffer, aMinY - keepBuffer, aMaxX + keepBuffer, aMaxY + keepBuffer);
                    int outlierCount = 0;

                    var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = tr.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                    foreach (ObjectId entId in ms!)
                    {
                        try
                        {
                            var ent = tr.GetObject(entId, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                            if (ent == null) continue;
                            var ext = ent.GeometricExtents;
                            var rect = new ProfileViewPlacement.Rect(
                                ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y);
                            // Discard far-away outliers so they cannot pollute the measured extents.
                            if (!rect.Intersects(keepRegion)) { outlierCount++; continue; }
                            allExtents.Add(rect);
                            scannedEntityCount++;
                            if (ent is CivilDb.ProfileView)
                            {
                                pvCount++;
                                pvExtents.Add(rect);
                            }
                        }
                        catch
                        {
                            // Entities without valid extents (e.g. empty blocks) are skipped.
                        }
                    }
                    if (outlierCount > 0)
                        placementWarnings.Add(
                            $"Ignored {outlierCount} entity(ies) far outside the alignment region " +
                            "when measuring drawing extents (kept the PV near the road).");

                    // Estimate the new PV footprint: width ≈ station range + label margin;
                    // height ≈ tallest existing PV (or a 200 m default grid).
                    double stationRange = Math.Max(
                        1.0, alignment.EndingStation - alignment.StartingStation);
                    double estWidth = stationRange + 100.0;
                    double estHeight = 200.0;
                    foreach (var pvRect in pvExtents)
                        if (pvRect.Height > estHeight) estHeight = pvRect.Height;

                    // Engineer request (2026-06-18): place the longitudinal profile view to
                    // the RIGHT of the plan with a generous margin, not stacked beneath it.
                    var placement = ProfileViewPlacement.Choose(
                        allExtents, pvExtents, refX, refY, estWidth, estHeight,
                        marginM: 50.0, preferRight: true);
                    x = placement.OriginX;
                    y = placement.OriginY;
                    placementMethod = placement.Method;
                    placementWarnings.AddRange(placement.Warnings);

                    ed?.WriteMessage(
                        $"\n[MahodAI] ProfileView placement: scanned {scannedEntityCount} entities " +
                        $"({pvCount} PVs), origin=({x:F0},{y:F0}), method={placementMethod}\n");
                }
                catch (Exception ex)
                {
                    // Fallback computed from real geometry — never a hardcoded coordinate.
                    placementWarnings.Add($"Placement scan failed: {ex.Message}; used extents fallback.");
                    placementMethod = "fallback_extents";
                    try
                    {
                        var aext = alignment.GeometricExtents;
                        x = aext.MinPoint.X;
                        y = aext.MinPoint.Y - 500.0;
                    }
                    catch
                    {
                        try
                        {
                            var db = HostApplicationServices.WorkingDatabase;
                            var ext = db.TileMode ? db.Extmin : db.Pextmin;
                            x = ext.X;
                            y = ext.Y - 500.0;
                        }
                        catch
                        {
                            // Last resort: anchor at the alignment start point.
                            x = refX;
                            y = refY - 500.0;
                        }
                    }
                }
            }

            var insertionPoint = new Point3d(x, y, 0);

            // Ensure a TrueType text style exists for Hebrew support
            // SHX fonts show ??? for Hebrew characters
            EnsureTrueTypeTextStyle(tr, HostApplicationServices.WorkingDatabase);

            // Get styles — prefer a named/clean style; collect ALL available names so the
            // engineer can tell us which band-set matches their template (the footer-clutter
            // fix, #3: today we blindly took the first band-set, which is the cluttered one).
            var availablePvStyles = new List<string>();
            var availableBandSets = new List<string>();
            ObjectId pvStyleId = ResolveStyleByName(
                tr, civilDoc.Styles.ProfileViewStyles, profileViewStyleName,
                _preferredPvStyleHints, availablePvStyles, out string chosenPvStyle,
                out string pvStyleMatch);
            ObjectId bandSetId = ResolveStyleByName(
                tr, civilDoc.Styles.ProfileViewBandSetStyles, bandSetStyleName,
                _preferredBandSetHints, availableBandSets, out string chosenBandSet,
                out string bandSetMatch);
            try
            {
                Autodesk.AutoCAD.ApplicationServices.Core.Application
                    .DocumentManager.MdiActiveDocument?.Editor?.WriteMessage(
                    $"\n[MahodAI] ProfileView styles — view='{chosenPvStyle}' ({pvStyleMatch}), " +
                    $"band-set='{chosenBandSet}' ({bandSetMatch}).\n" +
                    $"[MahodAI] Available band-set styles: {string.Join(" | ", availableBandSets)}\n");
            }
            catch { /* editor logging is best-effort */ }

            // Create Profile View via reflection
            ObjectId pvId = ObjectId.Null;
            string creationMethod = "unknown";

            try
            {
                pvId = CreateProfileViewViaApi(
                    tr, civilDoc, alignmentId.Value, insertionPoint, pvName, bandSetId, pvStyleId);
                creationMethod = "API_ProfileView.Create";
                // Only nudge the grid on when we could NOT resolve a real template style and had
                // to take whatever style came first — see ApplyPvStyleAndBands.
                bool forceGrid = pvStyleMatch == "fallback_first";
                // The Create overload that actually runs may DROP the bandSetId/styleId args
                // (3- or 4-param simple overloads), leaving the drawing's default (cluttered,
                // ????? ) band set. Re-apply the resolved style + band set explicitly so the
                // firm's clean "Mahod Profile View Band Set" actually sticks.
                ApplyPvStyleAndBands(tr, civilDoc, pvId, pvStyleId, bandSetId, forceGrid);
            }
            catch (Exception apiEx)
            {
                // Fallback: LISP command
                try
                {
                    bool queued = CreateProfileViewViaLisp(alignmentName, pvName);
                    if (queued)
                    {
                        creationMethod = "LISP_CreateProfileView";
                        cache.RemoveByPattern("get_drawing_summary:");

                        return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["profile_view_name"] = pvName,
                            ["creation_method"] = creationMethod,
                            ["command_queued"] = true,
                            ["insertion_point"] = new { x = Math.Round(x, 2), y = Math.Round(y, 2) },
                            ["placement"] = BuildPlacementPayload(placementMethod, scannedEntityCount, pvCount, placementWarnings),
                            // Belt-and-suspenders: also surface collision warnings at the TOP
                            // level so the agent reads them even if it ignores the nested
                            // placement object (the nested PROTOCOL.md contract stays intact).
                            ["collision_warnings"] = placementWarnings,
                            ["message"] = $"Profile View '{pvName}' creation queued via LISP. " +
                                "A dialog may appear — confirm with OK/Enter."
                        }));
                    }
                }
                catch (Exception lispEx)
                {
                    return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                        $"Failed to create Profile View. API: {apiEx.Message}. LISP: {lispEx.Message}");
                }
            }

            // Read back profile view data
            if (pvId != ObjectId.Null)
            {
                try
                {
                    var pv = tr.GetObject(pvId, OpenMode.ForRead) as CivilDb.ProfileView;
                    if (pv != null)
                    {
                        // Count profiles shown
                        int profileCount = 0;
                        foreach (ObjectId profId in alignment.GetProfileIds())
                            profileCount++;

                        cache.RemoveByPattern("get_drawing_summary:");

                        return await Task.FromResult(ToolResult.Ok(new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["profile_view_name"] = pv.Name,
                            ["creation_method"] = creationMethod,
                            ["alignment_name"] = alignmentName,
                            ["profiles_displayed"] = profileCount,
                            ["insertion_point"] = new { x = Math.Round(x, 2), y = Math.Round(y, 2) },
                            ["placement"] = BuildPlacementPayload(placementMethod, scannedEntityCount, pvCount, placementWarnings),
                            // Belt-and-suspenders: also surface collision warnings at the TOP
                            // level so the agent reads them even if it ignores the nested
                            // placement object (the nested PROTOCOL.md contract stays intact).
                            ["collision_warnings"] = placementWarnings,
                            ["station_range"] = new
                            {
                                start = Math.Round(alignment.StartingStation, 2),
                                end = Math.Round(alignment.EndingStation, 2)
                            },
                            // Surface the band-set/PV style names so the engineer can pin the
                            // clean template band-set (the footer-clutter fix, #3).
                            ["band_set_style"] = chosenBandSet,
                            ["profile_view_style"] = chosenPvStyle,
                            // How each style was resolved: "explicit" | "hint" | "fallback_first".
                            // "fallback_first" means the drawing carries none of the template
                            // styles — the view will look bare, so surface it rather than hide it.
                            ["band_set_style_match"] = bandSetMatch,
                            ["profile_view_style_match"] = pvStyleMatch,
                            ["available_band_set_styles"] = availableBandSets,
                            ["available_profile_view_styles"] = availablePvStyles,
                            ["message"] = $"Profile View '{pv.Name}' created successfully. " +
                                $"Showing {profileCount} profiles for alignment '{alignmentName}'."
                        }));
                    }
                }
                catch { }
            }

            return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                "Profile View creation completed but could not be verified");
        }

        /// <summary>
        /// Finds an existing ProfileView that belongs to <paramref name="alignmentId"/>.
        /// Primary match is the view's parent-alignment id (read by reflection so the
        /// property name/shape can vary across Civil 3D releases without breaking the
        /// build); the expected view name is the fallback for older drawings where the
        /// property is unreadable. Returns null when the alignment has no view yet.
        /// </summary>
        private static CivilDb.ProfileView? FindProfileViewForAlignment(
            Transaction tr, ObjectId alignmentId, string expectedName)
        {
            CivilDb.ProfileView? byName = null;
            try
            {
                var db = HostApplicationServices.WorkingDatabase;
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var ms = tr.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId entId in ms!)
                {
                    CivilDb.ProfileView? pv;
                    try { pv = tr.GetObject(entId, OpenMode.ForRead) as CivilDb.ProfileView; }
                    catch { continue; }
                    if (pv == null) continue;

                    if (GetParentAlignmentId(pv) == alignmentId) return pv;

                    if (byName == null && !string.IsNullOrEmpty(expectedName) &&
                        pv.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                        byName = pv;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] existing-ProfileView scan failed: {ex.Message}");
            }
            return byName;
        }

        /// <summary>Reads ProfileView's parent-alignment ObjectId by reflection (null id when unavailable).</summary>
        private static ObjectId GetParentAlignmentId(CivilDb.ProfileView pv)
        {
            foreach (var propName in new[] { "AlignmentId", "ParentAlignmentId" })
            {
                try
                {
                    var prop = pv.GetType().GetProperty(propName);
                    if (prop?.GetValue(pv) is ObjectId id && !id.IsNull) return id;
                }
                catch { /* try the next candidate */ }
            }
            return ObjectId.Null;
        }

        private static Dictionary<string, object> BuildPlacementPayload(
            string method, int scannedEntities, int existingProfileViews, List<string> warnings)
        {
            return new Dictionary<string, object>
            {
                ["method"] = method,
                ["scanned_entities"] = scannedEntities,
                ["existing_profile_views"] = existingProfileViews,
                ["collision_warnings"] = warnings,
            };
        }

        /// <summary>
        /// Re-applies the Profile View style and band-set style AFTER creation. Civil 3D's
        /// ProfileView.Create overloads vary, and the one that runs may ignore the band-set /
        /// style arguments, leaving the drawing default. Applying them explicitly here is what
        /// actually swaps in the firm's clean band set. Best-effort: each step is guarded so a
        /// missing API member never aborts the (successful) creation.
        /// </summary>
        /// <param name="forceGrid">
        /// Turn the style's major grid on. Only true when style resolution fell back to an
        /// arbitrary (first-available) style: a real template style already carries the firm's
        /// grid setup, and overriding it is what produced the cluttered white grid the engineer
        /// flagged (2026-07-27).
        /// </param>
        private static void ApplyPvStyleAndBands(
            Transaction tr, CivilDocument civilDoc, ObjectId pvId, ObjectId pvStyleId, ObjectId bandSetId,
            bool forceGrid)
        {
            if (pvId.IsNull) return;
            try
            {
                if (tr.GetObject(pvId, OpenMode.ForWrite) is not CivilDb.ProfileView pv) return;

                if (!pvStyleId.IsNull)
                {
                    try { pv.StyleId = pvStyleId; }
                    catch (Exception ex)
                    { System.Diagnostics.Debug.WriteLine($"[MahodAI] set ProfileView.StyleId failed: {ex.Message}"); }

                    // The engineer wants the grey graph-paper grid behind every profile view
                    // (request 2026-07-28) — the firm's own PV styles ship with it switched off,
                    // so this runs for template styles too, not just the fallback. Only the
                    // horizontal/vertical major+minor grid is touched; axes, annotation and the
                    // template's other colours are left exactly as authored.
                    EnsureProfileViewGrid(tr, pvStyleId);
                }

                if (!bandSetId.IsNull)
                {
                    // The firm band styles ship with TextStyle "Standard" (= simplex.shx, no
                    // Hebrew), so band titles render as ?????. The band-title annotation captures
                    // its text style WHEN THE BAND IS GENERATED, and changing it afterwards (+
                    // REGEN) does NOT refresh it (verified via MAHOD_HEBTEST: arial/MHEB render
                    // Hebrew, Standard does not). So repoint every profile-view band style to a
                    // Hebrew TrueType style BEFORE importing the band set, so the generated titles
                    // use a Hebrew font.
                    try
                    {
                        var db = HostApplicationServices.WorkingDatabase;
                        string? heb = ResolveHebrewTextStyle(tr, db);
                        if (heb != null) SetProfileBandStylesFont(tr, civilDoc, heb);
                    }
                    catch (Exception ex)
                    { System.Diagnostics.Debug.WriteLine($"[MahodAI] band-style Hebrew font pre-set failed: {ex.Message}"); }

                    try { pv.Bands.ImportBandSetStyle(bandSetId); }
                    catch (Exception ex)
                    { System.Diagnostics.Debug.WriteLine($"[MahodAI] Bands.ImportBandSetStyle failed: {ex.Message}"); }

                    // Imported band items inherit the band-set style's stored Weeding (the firm
                    // sets carry 100–500 m — labels closer than that are suppressed, which on a
                    // normal screen span reads as a completely EMPTY table) and every band
                    // auto-binds to the FIRST profile. Zero the weeding and bind EG/FG right
                    // here, so the view is correct even if configure_profile_bands never runs.
                    try
                    {
                        ObjectId egId = ObjectId.Null, fgId = ObjectId.Null;
                        if (tr.GetObject(pv.AlignmentId, OpenMode.ForRead) is CivilDb.Alignment pvAl)
                        {
                            foreach (ObjectId pid in pvAl.GetProfileIds())
                            {
                                if (tr.GetObject(pid, OpenMode.ForRead) is not CivilDb.Profile prf) continue;
                                if (prf.Name.IndexOf("EG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    prf.Name.IndexOf("Exist", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (egId.IsNull) egId = pid;
                                }
                                else if (prf.Name.IndexOf("FG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         prf.Name.IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (fgId.IsNull) fgId = pid;
                                }
                            }
                        }
                        var items = pv.Bands.GetBottomBandItems();
                        foreach (CivilDb.ProfileViewBandItem item in items)
                        {
                            string bandStyleName = "";
                            try
                            {
                                if (tr.GetObject(item.BandStyleId, OpenMode.ForRead)
                                    is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb) bandStyleName = sb.Name;
                            }
                            catch { }
                            bool isExist = bandStyleName.IndexOf("Exist", StringComparison.OrdinalIgnoreCase) >= 0;
                            ObjectId p1 = isExist ? egId : (!fgId.IsNull ? fgId : egId);
                            try { if (!p1.IsNull) item.Profile1Id = p1; }
                            catch { /* geometry bands reject a profile — they don't need one */ }
                            try { if (!egId.IsNull) item.Profile2Id = egId; } catch { }
                            try { item.Weeding = 0.0; } catch { }
                            try { item.LabelAtStartStation = true; item.LabelAtEndStation = true; } catch { }
                        }
                        pv.Bands.SetBottomBandItems(items);
                        Utilities.MahodLogger.Info(
                            $"[bands] '{pv.Name}': weeding zeroed + profiles bound at creation " +
                            $"(eg_found={!egId.IsNull} fg_found={!fgId.IsNull})");
                    }
                    catch (Exception ex)
                    {
                        Utilities.MahodLogger.Error("[bands] creation-time band bind failed", ex);
                        System.Diagnostics.Debug.WriteLine(
                            $"[MahodAI] creation-time band bind failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyPvStyleAndBands failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Turns on the MAJOR grid (horizontal + vertical) of a ProfileViewStyle so the profile
        /// view shows a background grid. The grid components are reached via the style's
        /// GetDisplayStyle(enum) getter (same one the MAHOD_DUMPPV diagnostic reads), which
        /// returns live display objects when the style is open for write. Best-effort and
        /// idempotent; minor grids are left off to avoid clutter.
        ///
        /// Called ONLY as a last resort (no template style matched). The style's own colours are
        /// kept — the old ACI-8 grey recolour overwrote the firm's grid colour.
        /// </summary>
        private static void EnsureProfileViewGrid(Transaction tr, ObjectId pvStyleId)
        {
            if (pvStyleId.IsNull) return;
            try
            {
                var styleObj = tr.GetObject(pvStyleId, OpenMode.ForWrite);
                if (styleObj == null) return;

                var getter = styleObj.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name.StartsWith("GetDisplayStyle", StringComparison.Ordinal)
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType.IsEnum);
                if (getter == null) return;

                var enumType = getter.GetParameters()[0].ParameterType;

                foreach (var ev in Enum.GetValues(enumType))
                {
                    string name = ev.ToString() ?? "";
                    if (name.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool axisGrid =
                        name.IndexOf("Horizontal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Vertical", StringComparison.OrdinalIgnoreCase) >= 0;
                    // GridAtHGP / GridAtSampleLineStations draw one vertical per geometry point or
                    // per sample line — on a road with 100 sample lines that is a picket fence,
                    // not a background grid. The engineer asked for the graph-paper grid only.
                    if (!axisGrid) continue;

                    bool minor = name.IndexOf("Minor", StringComparison.OrdinalIgnoreCase) >= 0;
                    try
                    {
                        var disp = getter.Invoke(styleObj, new object[] { ev });
                        if (disp == null) continue;
                        var dt = disp.GetType();
                        dt.GetProperty("Visible")?.SetValue(disp, true);
                        // Grey graph paper: mid grey for the major lines, light grey for the minor
                        // ones, so the grid reads as background behind the EG/FG profiles
                        // (engineer request 2026-07-28).
                        dt.GetProperty("Color")?.SetValue(disp,
                            Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, minor ? (short)253 : (short)8));
                    }
                    catch (Exception ex)
                    { System.Diagnostics.Debug.WriteLine($"[MahodAI] grid component '{name}' enable failed: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] EnsureProfileViewGrid failed: {ex.Message}"); }
        }

        // Name-based fallbacks, used ONLY when no style declares the Hebrew character set.
        private static readonly string[] _hebrewTextStylePrefs = { "MHEB", "David", "arial" };

        /// <summary>Resolves a Hebrew-capable TrueType text style name (arial/MHEB/David, else first TTF). Null if none.</summary>
        /// <summary>
        /// A text style that actually renders Hebrew band titles. The CHARACTER SET decides, not
        /// the name: this drawing's style literally called "arial" is arial.ttf with charset 0
        /// (ANSI), and every band title using it drew as "??????" while "MHEB" (David, charset
        /// 177) rendered perfectly — measured in the live drawing 2026-07-28. The old preference
        /// list put "arial" first purely by name, which is why the firm's Hebrew band titles kept
        /// coming out as question marks. Order now: charset 177 + TrueType → known Hebrew names →
        /// any TrueType.
        /// </summary>
        private static string? ResolveHebrewTextStyle(Transaction tr, Database db)
        {
            try
            {
                if (tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) is TextStyleTable tst)
                {
                    foreach (ObjectId id in tst)
                        if (tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord ts &&
                            !string.IsNullOrEmpty(ts.Name))
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(ts.Font.TypeFace) && ts.Font.CharacterSet == 177)
                                    return ts.Name;
                            }
                            catch { }
                        }

                    foreach (var want in _hebrewTextStylePrefs)
                        if (tst.Has(want)) return want;

                    foreach (ObjectId id in tst)
                        if (tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord ts)
                        {
                            string tf = ""; try { tf = ts.Font.TypeFace ?? ""; } catch { }
                            if (!string.IsNullOrEmpty(tf)) return ts.Name;
                        }
                }
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveHebrewTextStyle failed: {ex.Message}"); }
            return null;
        }

        /// <summary>
        /// Repoints every profile-view band style's title TextStyle to a Hebrew TrueType style.
        /// Must run BEFORE ImportBandSetStyle: the band-title annotation captures its text style
        /// when the band is generated, so setting it afterwards (+ REGEN) does not refresh it.
        /// </summary>
        private static void SetProfileBandStylesFont(Transaction tr, CivilDocument civilDoc, string textStyleName)
        {
            try
            {
                var bandStyles = civilDoc.Styles.BandStyles;
                SetBandCollectionFont(tr, bandStyles.ProfileViewProfileDataBandStyles, textStyleName);
                SetBandCollectionFont(tr, bandStyles.ProfileViewVerticalGeometryBandStyles, textStyleName);
                SetBandCollectionFont(tr, bandStyles.ProfileViewHorizontalGeometryBandStyles, textStyleName);
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] SetProfileBandStylesFont failed: {ex.Message}"); }
        }

        // The inline MTEXT font run that actually renders Hebrew band titles: Arial with the
        // Hebrew character set (c177). The c177 charset is what decodes the \U+05xx escapes as
        // Hebrew — the firm's untouched bands render with exactly this; stripping it (or using
        // c0) yields ?????.
        private const string HebrewFontRun = @"\fArial|b0|i0|c177|p34;";

        /// <summary>
        /// For every band style in the collection: point the title TextStyle at a Hebrew TrueType
        /// style AND ensure the title MTEXT carries the Arial + Hebrew-charset (c177) inline font
        /// run. That run is what renders the Hebrew (verified: the firm's untouched bands render
        /// with it; stripping it or using c0 produces ?????). If a font run is already present its
        /// codepage is normalised to c177; if absent it is inserted after the leading brace.
        /// </summary>
        private static void SetBandCollectionFont(
            Transaction tr, System.Collections.IEnumerable coll, string textStyleName)
        {
            if (coll == null) return;
            foreach (ObjectId id in coll)
            {
                try
                {
                    var bs = tr.GetObject(id, OpenMode.ForWrite);
                    if (bs == null) continue;
                    var t = bs.GetType();

                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;
                        if (p.PropertyType == typeof(string) &&
                            p.Name.IndexOf("TextStyle", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { p.SetValue(bs, textStyleName); } catch { }
                        }
                    }

                    var pText = t.GetProperty("Text");
                    if (pText != null && pText.CanWrite && pText.PropertyType == typeof(string)
                        && pText.GetValue(bs) is string cur && cur.Length > 0)
                    {
                        try
                        {
                            string updated;
                            if (cur.IndexOf("\\f", StringComparison.Ordinal) >= 0)
                                // Replace any existing font run with the Hebrew (Arial/c177) one.
                                updated = System.Text.RegularExpressions.Regex.Replace(
                                    cur, @"\\f[^;]*;", HebrewFontRun);
                            else if (cur.StartsWith("{", StringComparison.Ordinal))
                                // Stripped earlier — re-insert the font run after the opening brace.
                                updated = "{" + HebrewFontRun + cur.Substring(1);
                            else
                                updated = "{" + HebrewFontRun + cur + "}";

                            if (updated != cur) pText.SetValue(bs, updated);
                        }
                        catch (Exception ex)
                        { System.Diagnostics.Debug.WriteLine($"[MahodAI] band Text font-run set failed: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                { System.Diagnostics.Debug.WriteLine($"[MahodAI] SetBandCollectionFont item failed: {ex.Message}"); }
            }
        }

        private static ObjectId CreateProfileViewViaApi(
            Transaction tr,
            CivilDocument civilDoc,
            ObjectId alignmentId,
            Point3d insertionPoint,
            string pvName,
            ObjectId bandSetId,
            ObjectId pvStyleId)
        {
            var ed = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument?.Editor;

            // Civil 3D 2026: ProfileView.Create requires *CreationOptions objects.
            // Strategy: discover all static Create overloads, try matching by parameter types.
            var methods = typeof(CivilDb.ProfileView)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Create")
                .ToList();

            ed?.WriteMessage($"\n[MahodAI] ProfileView.Create: found {methods.Count} overloads\n");
            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                ed?.WriteMessage($"[MahodAI]   overload({ps.Length}): {string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name))}\n");
            }

            ObjectId pvId = ObjectId.Null;

            // === Approach 1: Direct call with simple parameters ===
            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                // Skip overloads that require *CreationOptions types
                bool hasCreationOptions = parms.Any(p =>
                    p.ParameterType.Name.Contains("CreationOptions") ||
                    p.ParameterType.Name.Contains("Stacked") ||
                    p.ParameterType.Name.Contains("Split") ||
                    p.ParameterType.Name.Contains("Multiple"));
                if (hasCreationOptions) continue;

                try
                {
                    object[] args = parms.Length switch
                    {
                        5 => new object[] { alignmentId, insertionPoint, pvName, bandSetId, pvStyleId },
                        4 => new object[] { alignmentId, insertionPoint, pvName, pvStyleId },
                        3 => new object[] { alignmentId, insertionPoint, pvName },
                        _ => null!
                    };
                    if (args == null) continue;

                    pvId = (ObjectId)method.Invoke(null, args)!;
                    ed?.WriteMessage($"[MahodAI] ProfileView.Create succeeded via {parms.Length}-param simple overload\n");
                    return pvId;
                }
                catch (Exception ex)
                {
                    ed?.WriteMessage($"[MahodAI] Simple {parms.Length}-param failed: {ex.InnerException?.Message ?? ex.Message}\n");
                }
            }

            // === Approach 2: Use MultipleProfileViewsCreationOptions (for single PV) ===
            try
            {
                var optionsType = typeof(CivilDb.ProfileView).Assembly
                    .GetTypes()
                    .FirstOrDefault(t => t.Name == "MultipleProfileViewsCreationOptions");

                if (optionsType != null)
                {
                    // Find Create overload that accepts this options type
                    var method = methods.FirstOrDefault(m =>
                    {
                        var ps = m.GetParameters();
                        return ps.Any(p => p.ParameterType == optionsType);
                    });

                    if (method != null)
                    {
                        // Create options instance — constructor typically takes (alignmentId, insertionPoint)
                        object? options = null;
                        foreach (var ctor in optionsType.GetConstructors())
                        {
                            var ctorParams = ctor.GetParameters();
                            try
                            {
                                if (ctorParams.Length == 2 &&
                                    ctorParams[0].ParameterType == typeof(ObjectId) &&
                                    ctorParams[1].ParameterType == typeof(Point3d))
                                {
                                    options = ctor.Invoke(new object[] { alignmentId, insertionPoint });
                                    break;
                                }
                                else if (ctorParams.Length == 3)
                                {
                                    options = ctor.Invoke(new object[] { alignmentId, insertionPoint, pvName });
                                    break;
                                }
                                else if (ctorParams.Length == 0)
                                {
                                    options = ctor.Invoke(Array.Empty<object>());
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (options == null)
                            options = Activator.CreateInstance(optionsType);

                        if (options != null)
                        {
                            // Set properties on options object
                            SetPropertySafe(options, "AlignmentId", alignmentId);
                            SetPropertySafe(options, "InsertionPoint", insertionPoint);
                            if (pvStyleId != ObjectId.Null)
                                SetPropertySafe(options, "ProfileViewStyleId", pvStyleId);
                            if (bandSetId != ObjectId.Null)
                                SetPropertySafe(options, "BandSetStyleId", bandSetId);

                            var methodParams = method.GetParameters();
                            object[] args = methodParams.Length == 1
                                ? new[] { options }
                                : new[] { alignmentId, options };

                            var result = method.Invoke(null, args);
                            if (result is ObjectId id && id != ObjectId.Null)
                            {
                                ed?.WriteMessage($"[MahodAI] ProfileView.Create succeeded via MultipleProfileViewsCreationOptions\n");
                                return id;
                            }
                            // result might be ObjectIdCollection for multiple views
                            if (result is ObjectIdCollection coll && coll.Count > 0)
                            {
                                ed?.WriteMessage($"[MahodAI] ProfileView.Create succeeded via MultipleProfileViewsCreationOptions (collection of {coll.Count})\n");
                                return coll[0];
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"[MahodAI] MultipleProfileViewsCreationOptions approach failed: {ex.InnerException?.Message ?? ex.Message}\n");
            }

            // === Approach 3: Brute-force all overloads with matching arg types ===
            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                try
                {
                    object?[] args = new object?[parms.Length];
                    bool canBuild = true;
                    for (int i = 0; i < parms.Length; i++)
                    {
                        var pt = parms[i].ParameterType;
                        if (pt == typeof(ObjectId)) args[i] = alignmentId;
                        else if (pt == typeof(Point3d)) args[i] = insertionPoint;
                        else if (pt == typeof(string)) args[i] = pvName;
                        else
                        {
                            // Try to create default instance of any options type
                            try { args[i] = Activator.CreateInstance(pt); }
                            catch { canBuild = false; break; }
                        }
                    }
                    if (!canBuild) continue;

                    var result = method.Invoke(null, args);
                    if (result is ObjectId rid && rid != ObjectId.Null)
                    {
                        ed?.WriteMessage($"[MahodAI] ProfileView.Create succeeded via brute-force {parms.Length}-param\n");
                        return rid;
                    }
                    if (result is ObjectIdCollection coll2 && coll2.Count > 0)
                    {
                        ed?.WriteMessage($"[MahodAI] ProfileView.Create succeeded via brute-force (collection of {coll2.Count})\n");
                        return coll2[0];
                    }
                }
                catch (Exception ex)
                {
                    ed?.WriteMessage($"[MahodAI] Brute-force {parms.Length}-param failed: {ex.InnerException?.Message ?? ex.Message}\n");
                }
            }

            throw new InvalidOperationException(
                $"No compatible ProfileView.Create overload found. " +
                $"Discovered {methods.Count} candidate(s).");
        }

        private static void SetPropertySafe(object obj, string propName, object value)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(obj, value);
            }
            catch { }
        }

        private static bool CreateProfileViewViaLisp(string alignmentName, string pvName)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            string lispCmd = $"(progn " +
                $"(setvar \"CMDDIA\" 0) " +
                $"(command \"_CREATEPROFILEVIEW\" \"{alignmentName}\" \"\" \"\") " +
                $"(setvar \"CMDDIA\" 1))";

            doc.SendStringToExecute(lispCmd, true, false, false);
            return true;
        }

        // Preference hints when no explicit style name is requested. Resolved against whatever
        // styles the OPEN DRAWING carries (they come from the firm template); the full list is
        // returned to the agent so an exact name can be pinned later.
        //
        // The firm drawings carry the "IRoads" content-pack styles — verified against the live
        // style inventory (MAHOD_DUMPSTYLES → style_dump.txt): the profile-view grid styles are
        // "IRoads Design Profile View [1:1] / [1:10] / [Presentation]" and the band sets are
        // "IRoads Design Profile Band Set [No Ditch|Right and Left Ditch][Index|Distance]".
        // The older "IL Left & Bottom Axis" / "Mahod Profile View Band Set" names do NOT exist
        // in these drawings, so the hints never matched and resolution fell through to the
        // first-available style — "Land Desktop Full Band Set" plus the bare "Major Grids"
        // grid, i.e. the styleless, textless, cluttered view the engineer flagged.
        // Keep the legacy names as later fallbacks for drawings that still carry them.
        private static readonly string[] _preferredBandSetHints =
        {
            "IRoads Design Profile Band Set [No Ditch][Index]",
            "IRoads Design Profile Band Set",
            "Mahod", "מהוד", "מתוכנן",
        };
        // NOTE: never hint a bare "Major Grid" here — it substring-matches the Autodesk
        // "Major Grids" style (grid only, no axis annotation), which is exactly the bad view.
        private static readonly string[] _preferredPvStyleHints =
        {
            "IRoads Design Profile View [1:10]",
            "IRoads Design Profile View [1:1]",
            "IRoads Design Profile View",
            "IL Left & Bottom Axis", "Mahod", "מהוד",
        };

        /// <summary>
        /// Resolves a Civil 3D style ObjectId from a style collection by: (1) an explicit
        /// requested name (exact, then substring); (2) a list of preferred-name hints; then
        /// (3) the first available as a last resort. Collects every style name into
        /// <paramref name="available"/> and reports the <paramref name="chosen"/> name so the
        /// engineer can see and pin the right template style. <paramref name="matchKind"/> says
        /// HOW it was found ("explicit" | "hint" | "fallback_first" | "none") — the caller uses
        /// it to decide whether the style is a real template style (leave it alone) or an
        /// arbitrary last resort (safe to patch its grid). Never throws.
        /// </summary>
        private static ObjectId ResolveStyleByName(
            Transaction tr,
            System.Collections.IEnumerable styleCollection,
            string? requestedName,
            string[] preferredHints,
            List<string> available,
            out string chosen,
            out string matchKind)
        {
            chosen = string.Empty;
            matchKind = "none";
            var names = new List<string>();
            var ids = new List<ObjectId>();
            try
            {
                foreach (ObjectId id in styleCollection)
                {
                    // Typed Name read — reflection on the style's Name getter throws
                    // "Property Get method was not found" on Civil 3D style objects, which
                    // previously made every name fall back to the ObjectId handle (so no
                    // preferred-name match ever fired and the cluttered first style won).
                    string nm = id.ToString();
                    if (tr.GetObject(id, OpenMode.ForRead)
                            is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb)
                        nm = sb.Name;
                    names.Add(nm);
                    ids.Add(id);
                    available.Add(nm);
                }
            }
            catch { /* enumeration failure → fall through to Null */ }

            // (1) explicit request — exact, then substring.
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                for (int i = 0; i < names.Count; i++)
                    if (string.Equals(names[i], requestedName, StringComparison.OrdinalIgnoreCase))
                    { chosen = names[i]; matchKind = "explicit"; return ids[i]; }
                for (int i = 0; i < names.Count; i++)
                    if (names[i].IndexOf(requestedName!, StringComparison.OrdinalIgnoreCase) >= 0)
                    { chosen = names[i]; matchKind = "explicit"; return ids[i]; }
            }
            // (2) preferred-name hints.
            foreach (var hint in preferredHints)
                for (int i = 0; i < names.Count; i++)
                    if (names[i].IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    { chosen = names[i]; matchKind = "hint"; return ids[i]; }
            // (3) first available.
            if (ids.Count > 0) { chosen = names[0]; matchKind = "fallback_first"; return ids[0]; }
            return ObjectId.Null;
        }

        /// <summary>
        /// Ensure a TrueType text style exists so Hebrew labels don't show as ???
        /// SHX fonts (like simplex.shx) don't support Hebrew characters.
        /// Creates "MahodAI" text style with Arial font if it doesn't exist,
        /// and sets it as current so new labels use it.
        /// </summary>
        private static void EnsureTrueTypeTextStyle(Transaction tr, Database db)
        {
            const string STYLE_NAME = "MahodAI";
            const string FONT_NAME = "Arial";

            try
            {
                var textStyleTable = tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) as TextStyleTable;
                if (textStyleTable == null) return;

                if (textStyleTable.Has(STYLE_NAME))
                {
                    // Style exists — make it current
                    db.Textstyle = textStyleTable[STYLE_NAME];
                    return;
                }

                // Create new TrueType text style
                var style = new TextStyleTableRecord
                {
                    Name = STYLE_NAME,
                    FileName = FONT_NAME, // TrueType font name
                    BigFontFileName = "", // No big font (SHX)
                    TextSize = 0, // 0 = use default
                };

                var textStyleTableWrite = tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite) as TextStyleTable;
                if (textStyleTableWrite != null)
                {
                    textStyleTableWrite.Add(style);
                    tr.AddNewlyCreatedDBObject(style, true);
                    db.Textstyle = style.ObjectId;

                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Created TrueType text style '{STYLE_NAME}' with font '{FONT_NAME}'");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] EnsureTrueTypeTextStyle failed (non-critical): {ex.Message}");
            }
        }
    }
}
