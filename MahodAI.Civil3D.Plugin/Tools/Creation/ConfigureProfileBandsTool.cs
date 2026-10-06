using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Configures bottom bands on a Profile View with standard data rows.
    ///
    /// Based on Igor's ProfileView band access patterns (CAlignment.cs, ProjectBlocksOnGeomBand.cs).
    ///
    /// Civil 3D Profile View bands show data below the profile graph:
    /// - EG Elevation band — existing ground elevations at stations
    /// - FG Elevation band — design elevations at stations
    /// - Cut/Fill band — difference between FG and EG
    /// - Grade/Slope band — longitudinal grades between PVIs
    /// - Horizontal Geometry band — curves, tangents, spirals
    /// - Station/Distance band — cumulative distance
    ///
    /// This tool ensures bands reference the correct profiles (EG + FG)
    /// and uses the drawing's available band set styles.
    /// </summary>
    public class ConfigureProfileBandsTool : DrawingToolBase
    {
        public override string Name => "configure_profile_bands";
        public override string Description =>
            "Configures bottom bands on a Profile View with standard Israeli data rows: " +
            "FG elevation, EG elevation, cut/fill, grade, horizontal geometry, cumulative distance.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""profile_view_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the Profile View to configure""
                },
                ""alignment_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the alignment (used to find profiles)""
                },
                ""fg_profile_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name of the FG (design) profile (default: auto-detect)""
                }
            },
            ""required"": [""profile_view_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var pvName = GetRequiredStringParam(parameters, "profile_view_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var fgProfileName = GetStringParam(parameters, "fg_profile_name");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            // Find Profile View by name
            CivilDb.ProfileView? profileView = null;
            ObjectId pvId = ObjectId.Null;

            // Search through all profile views in the drawing
            var db = HostApplicationServices.WorkingDatabase;
            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Cannot access block table");

            var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
            if (ms == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Cannot access model space");

            foreach (ObjectId entId in ms)
            {
                var ent = tr.GetObject(entId, OpenMode.ForRead);
                if (ent is CivilDb.ProfileView pv)
                {
                    if (pv.Name.Equals(pvName, StringComparison.OrdinalIgnoreCase))
                    {
                        profileView = pv;
                        pvId = entId;
                        break;
                    }
                }
            }

            if (profileView == null)
                return ToolResult.NotFound("ProfileView", pvName);

            // Open for write
            profileView = tr.GetObject(pvId, OpenMode.ForWrite) as CivilDb.ProfileView;
            if (profileView == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Cannot open ProfileView for write");

            // Find alignment
            ObjectId alignmentId = profileView.AlignmentId;
            CivilDb.Alignment? alignment = null;
            if (alignmentId != ObjectId.Null)
            {
                alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment;
            }

            if (alignment == null && !string.IsNullOrEmpty(alignmentName))
            {
                alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName) ?? ObjectId.Null;
                if (alignmentId != ObjectId.Null)
                    alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as CivilDb.Alignment;
            }

            // Find EG and FG profiles
            ObjectId egProfileId = ObjectId.Null;
            ObjectId fgProfileId = ObjectId.Null;

            if (alignment != null)
            {
                foreach (ObjectId profId in alignment.GetProfileIds())
                {
                    var prof = tr.GetObject(profId, OpenMode.ForRead) as CivilDb.Profile;
                    if (prof == null) continue;

                    if (!string.IsNullOrEmpty(fgProfileName) &&
                        prof.Name.Equals(fgProfileName, StringComparison.OrdinalIgnoreCase))
                    {
                        fgProfileId = profId;
                        continue;
                    }

                    // Auto-detect EG vs FG by name patterns
                    if (prof.Name.Contains("EG", StringComparison.OrdinalIgnoreCase) ||
                        prof.Name.Contains("Existing", StringComparison.OrdinalIgnoreCase))
                    {
                        egProfileId = profId;
                    }
                    else if (prof.Name.Contains("FG", StringComparison.OrdinalIgnoreCase) ||
                             prof.Name.Contains("Design", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fgProfileId == ObjectId.Null)
                            fgProfileId = profId;
                    }
                }
            }

            // Bind EG/FG profiles to the bands using the TYPED API. GetBottomBandItems() returns a
            // COPY of the collection, so changes persist ONLY if SetBottomBandItems() is called —
            // the old reflection path mutated the copy and dropped it, so every band stayed bound
            // to EG (prof1=prof2=EG) and the design-elevation rows were blank. Assign per purpose:
            //   • a band whose style name says "Exist" shows EXISTING ground → Profile1 = EG
            //   • any other data/geometry band shows the DESIGN → Profile1 = FG (fallback EG)
            //   • Profile2 = EG so two-profile / cut-fill difference bands have both surfaces.
            var bandsInfo = new List<Dictionary<string, object>>();
            var bandsVerified = new List<Dictionary<string, object>>();
            var verifyWarnings = new List<string>();
            int bandsConfigured = 0;

            try
            {
                var bands = profileView.Bands;
                var bottom = bands.GetBottomBandItems();
                foreach (CivilDb.ProfileViewBandItem item in bottom)
                {
                    bandsConfigured++;

                    string styleName = "";
                    try
                    {
                        if (tr.GetObject(item.BandStyleId, OpenMode.ForRead)
                            is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb2) styleName = sb2.Name;
                    }
                    catch { }
                    string btype = "?";
                    try { btype = item.BandType.ToString(); } catch { }

                    bool isExisting = styleName.IndexOf("Exist", StringComparison.OrdinalIgnoreCase) >= 0;
                    ObjectId p1 = isExisting
                        ? egProfileId
                        : (fgProfileId != ObjectId.Null ? fgProfileId : egProfileId);
                    ObjectId p2 = egProfileId != ObjectId.Null ? egProfileId : p1;

                    try { if (p1 != ObjectId.Null) item.Profile1Id = p1; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Profile1Id failed: {ex.Message}"); }
                    try { if (p2 != ObjectId.Null) item.Profile2Id = p2; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Profile2Id failed: {ex.Message}"); }

                    bool labelled = ApplyLabelSettings(item);
                    string diag = DescribeBandAnnotation(tr, item.BandStyleId);
                    Utilities.MahodLogger.Info(
                        $"[bands] '{pvName}' {btype} style='{styleName}' labelsForced={labelled} {diag}");

                    bandsInfo.Add(new Dictionary<string, object>
                    {
                        ["type"] = btype,
                        ["style"] = styleName,
                        ["profile1"] = isExisting ? "EG" : (fgProfileId != ObjectId.Null ? "FG" : "EG"),
                        ["profile2"] = "EG",
                        ["labels_forced"] = labelled,
                        ["annotation"] = diag,
                    });
                }

                // Persist the modified band items — without this the assignments are lost.
                bands.SetBottomBandItems(bottom);

                // Fix the ????? Hebrew band labels. Each firm band style stores its title as MTEXT
                // with an inline \fArial|...|c177 (Hebrew charset) override but TextStyle "Standard"
                // (= simplex.shx, no Hebrew). The title renders with the TextStyle font (simplex),
                // ignoring the inline Arial → ?????. Repoint each band style's TextStyle to a
                // Hebrew TrueType style (arial / MHEB / David) so the labels render.
                var (hebrewStyle, hebrewStyleId) = ResolveHebrewTextStyle(tr, db);
                if (hebrewStyle != null)
                {
                    var fixedStyles = new HashSet<string>();
                    foreach (CivilDb.ProfileViewBandItem item in bottom)
                        if (!item.BandStyleId.IsNull && fixedStyles.Add(item.BandStyleId.Handle.ToString()))
                            EnsureBandTitleFont(tr, item.BandStyleId, hebrewStyle, hebrewStyleId);
                }

                // VERIFY by re-reading. This bind path has failed silently before (Debug-only
                // catch), and a "successful" run with blank band cells costs an engineer
                // round-trip to diagnose. The fresh read-back goes into the tool result and the
                // plugin log, so the run proves — or disproves — its own success.
                var reread = bands.GetBottomBandItems();
                foreach (CivilDb.ProfileViewBandItem it in reread)
                {
                    string vStyle = "";
                    try
                    {
                        if (tr.GetObject(it.BandStyleId, OpenMode.ForRead)
                            is Autodesk.Civil.DatabaseServices.Styles.StyleBase vb) vStyle = vb.Name;
                    }
                    catch { }
                    string p1n = "", p2n = "";
                    try { if (!it.Profile1Id.IsNull && tr.GetObject(it.Profile1Id, OpenMode.ForRead) is CivilDb.Profile vp1) p1n = vp1.Name; } catch { }
                    try { if (!it.Profile2Id.IsNull && tr.GetObject(it.Profile2Id, OpenMode.ForRead) is CivilDb.Profile vp2) p2n = vp2.Name; } catch { }
                    double vw = -1; try { vw = it.Weeding; } catch { }
                    string vtype = "?"; try { vtype = it.BandType.ToString(); } catch { }

                    bool isDataBand = vtype.IndexOf("ProfileData", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isDataBand && string.IsNullOrEmpty(p1n))
                        verifyWarnings.Add(
                            $"band '{vStyle}' has no bound profile — its cells will render EMPTY");
                    if (vw > 0.0)
                        verifyWarnings.Add(
                            $"band '{vStyle}' kept weeding {vw:F0} m — labels closer than that are suppressed");

                    bandsVerified.Add(new Dictionary<string, object>
                    {
                        ["style"] = vStyle,
                        ["type"] = vtype,
                        ["profile1"] = p1n,
                        ["profile2"] = p2n,
                        ["weeding"] = vw,
                    });
                    Utilities.MahodLogger.Info(
                        $"[bands-verify] '{pvName}' {vtype} style='{vStyle}' p1='{p1n}' p2='{p2n}' weeding={vw:F0}");
                }
            }
            catch (Exception ex)
            {
                Utilities.MahodLogger.Error("[bands] ConfigureProfileBands typed bind failed", ex);
                verifyWarnings.Add($"band binding failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] ConfigureProfileBands typed bind failed: {ex.Message}");
            }

            // If no bands found, try to apply a band set style
            if (bandsConfigured == 0)
            {
                try
                {
                    // Get first available band set style and apply it
                    foreach (ObjectId bsId in civilDoc.Styles.ProfileViewBandSetStyles)
                    {
                        var bandSetProp = profileView.GetType().GetProperty("BandSetId")
                                        ?? profileView.GetType().GetProperty("BandSetStyleId");
                        if (bandSetProp != null && bandSetProp.CanWrite)
                        {
                            bandSetProp.SetValue(profileView, bsId);
                            bandsConfigured = 1;
                            bandsInfo.Add(new Dictionary<string, object>
                            {
                                ["type"] = "BandSetApplied",
                                ["message"] = "Applied default band set style"
                            });
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] ConfigureProfileBands apply band set: {ex.Message}");
                }
            }

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["profile_view_name"] = pvName,
                ["bands_found"] = bandsConfigured,
                ["bands"] = bandsInfo,
                ["bands_verified"] = bandsVerified,
                ["warnings"] = verifyWarnings,
                ["eg_profile_found"] = egProfileId != ObjectId.Null,
                ["fg_profile_found"] = fgProfileId != ObjectId.Null,
                ["message"] = bandsConfigured > 0
                    ? $"Profile View '{pvName}' bands configured: {bandsConfigured} bands found. " +
                      $"EG profile: {(egProfileId != ObjectId.Null ? "set" : "not found")}. " +
                      $"FG profile: {(fgProfileId != ObjectId.Null ? "set" : "not found")}."
                    : $"Profile View '{pvName}' — no band set applied. " +
                      "Configure bands manually in Civil 3D Profile View Properties → Bands tab."
            };

            return await Task.FromResult(ToolResult.Ok(result));
        }

        /// <summary>
        /// One-line description of everything that decides whether a band prints TEXT, written to
        /// the plugin log on every run: the style's text height, its assigned text style, and how
        /// many of its label-style slots are actually filled. A band with ticks but no numbers is
        /// either text-height-vs-scale or empty label slots, and this line says which — so the
        /// next run diagnoses itself instead of costing another round-trip.
        /// </summary>
        private static string DescribeBandAnnotation(Transaction tr, ObjectId bandStyleId)
        {
            if (bandStyleId.IsNull) return "style=<null>";
            try
            {
                var bs = tr.GetObject(bandStyleId, OpenMode.ForRead);
                if (bs == null) return "style=<unreadable>";
                var t = bs.GetType();

                string textHeight = "?", textStyle = "?", bandHeight = "?";
                try { textHeight = Convert.ToString(t.GetProperty("TextHeight")?.GetValue(bs)) ?? "?"; } catch { }
                try { textStyle = Convert.ToString(t.GetProperty("TextStyle")?.GetValue(bs)) ?? "?"; } catch { }
                try { bandHeight = Convert.ToString(t.GetProperty("BandHeight")?.GetValue(bs)) ?? "?"; } catch { }

                int slots = 0, filled = 0;
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (p.PropertyType != typeof(ObjectId)) continue;
                    if (p.Name.IndexOf("LabelStyle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    slots++;
                    try { if (p.GetValue(bs) is ObjectId id && !id.IsNull) filled++; } catch { }
                }

                return $"textHeight={textHeight} bandHeight={bandHeight} textStyle='{textStyle}' " +
                       $"labelStyles={filled}/{slots}";
            }
            catch (Exception ex)
            {
                return $"diag_failed={ex.Message}";
            }
        }

        /// <summary>
        /// Turns a band item's annotation ON as far as the band ITEM can control it.
        ///
        /// <c>ProfileViewBandItem</c> exposes no label station window (verified against
        /// AeccDbMgd 2027 — there is no <c>LabelStartStation</c>/<c>Increment</c>; the increments
        /// and label styles live on the band STYLE). What the item does own:
        ///   • <c>Weeding</c> — Civil 3D drops labels closer together than this. A non-zero
        ///     weeding on a long profile silently blanks the whole band, so force it to 0.
        ///   • <c>LabelAtStartStation</c> / <c>LabelAtEndStation</c> — the end labels.
        /// This is a necessary condition for band text, not proof of one: if a band style
        /// carries no label styles, nothing here can make it speak.
        /// </summary>
        private static bool ApplyLabelSettings(CivilDb.ProfileViewBandItem item)
        {
            bool any = false;
            try { item.Weeding = 0.0; any = true; }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] set Weeding failed: {ex.Message}"); }
            try { item.LabelAtStartStation = true; any = true; }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] set LabelAtStartStation failed: {ex.Message}"); }
            try { item.LabelAtEndStation = true; any = true; }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] set LabelAtEndStation failed: {ex.Message}"); }
            return any;
        }

        /// <summary>
        /// Resolves a Hebrew-capable TrueType text style (name + ObjectId), preferring the firm's
        /// "arial" / "MHEB" (David), else the first text style with any TrueType typeface.
        /// Returns (null, Null) if the table can't be read or no TrueType style exists.
        /// </summary>
        private static (string? name, ObjectId id) ResolveHebrewTextStyle(Transaction tr, Database db)
        {
            string[] prefer = { "arial", "MHEB", "David" };
            try
            {
                if (tr.GetObject(db.TextStyleTableId, OpenMode.ForRead) is TextStyleTable tst)
                {
                    foreach (var want in prefer)
                        if (tst.Has(want)) return (want, tst[want]);
                    // Fallback: first text style with a TrueType typeface (renders Hebrew).
                    foreach (ObjectId id in tst)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord ts)
                        {
                            string tf = ""; try { tf = ts.Font.TypeFace ?? ""; } catch { }
                            if (!string.IsNullOrEmpty(tf)) return (ts.Name, id);
                        }
                    }
                }
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveHebrewTextStyle failed: {ex.Message}"); }
            return (null, ObjectId.Null);
        }

        /// <summary>
        /// Repoints a band style's title font to a Hebrew TrueType style (the ????? lever). The
        /// firm band styles store the title as MTEXT with an inline \fArial|...|c177 run but
        /// TextStyle "Standard" (= simplex.shx, no Hebrew). We don't know which the title honours,
        /// so set EVERY writable *TextStyle* property (string=name, ObjectId=id) AND normalise the
        /// MTEXT codepage (c177 -> c0) so an honoured inline run renders the Unicode Hebrew with a
        /// TrueType face. Each step is logged so a follow-up MAHOD_DUMPPV shows what actually stuck.
        /// </summary>
        private static void EnsureBandTitleFont(
            Transaction tr, ObjectId bandStyleId, string textStyleName, ObjectId textStyleId)
        {
            try
            {
                var bs = tr.GetObject(bandStyleId, OpenMode.ForWrite);
                if (bs == null) return;
                var t = bs.GetType();

                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || !p.CanWrite) continue;
                    if (p.Name.IndexOf("TextStyle", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    try
                    {
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(bs, textStyleName);
                            System.Diagnostics.Debug.WriteLine($"[MahodAI] band {t.Name}.{p.Name}(string)='{textStyleName}'");
                        }
                        else if (p.PropertyType == typeof(ObjectId) && !textStyleId.IsNull)
                        {
                            p.SetValue(bs, textStyleId);
                            System.Diagnostics.Debug.WriteLine($"[MahodAI] band {t.Name}.{p.Name}(ObjectId) set");
                        }
                    }
                    catch (Exception ex)
                    { System.Diagnostics.Debug.WriteLine($"[MahodAI] band {p.Name} set failed: {ex.Message}"); }
                }

                // NOTE: the band-title font is fixed at PV-creation time (CreateProfileViewTool
                // ensures the Arial + Hebrew-charset \fArial|...|c177 run before ImportBandSetStyle).
                // Do NOT touch the title MTEXT codepage here — c177 (Hebrew charset) is what renders
                // the Hebrew; rewriting it to c0 produced ?????.
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[MahodAI] EnsureBandTitleFont failed: {ex.Message}"); }
        }
    }
}
