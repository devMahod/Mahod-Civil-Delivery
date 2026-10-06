using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Utilities;
using CivilDb = Autodesk.Civil.DatabaseServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcGe = Autodesk.AutoCAD.Geometry;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Imports a COMPLETE, pre-built named Assembly (with all its subassemblies + styles)
    /// from a library .dwg into the active drawing, using the first-class Civil 3D .NET API
    /// <c>AssemblyCollection.ImportAssembly(name, sourceDatabase, sourceAssemblyName, location)</c>.
    ///
    /// This is the "clone-from-library" upgrade the owner preferred over the empty-shell +
    /// guided-manual path: once the firm's engineers author the standard-assembly library
    /// (one .dwg per Civil 3D version, four named assemblies — see <see cref="AssemblyLibraryLocator"/>),
    /// Stage 3 can build the cross-section automatically with no manual Tool Palette step.
    ///
    /// Failure is first-class and non-destructive: if the library file or the named assembly
    /// is missing, or the import yields no subassemblies, the tool returns <c>ToolResult.Fail</c>
    /// (the executor aborts the transaction, leaving the drawing untouched) so the agent can
    /// fall back to the empty-shell + Hebrew guide path. The pure path/name logic lives in the
    /// AutoCAD-free <see cref="AssemblyLibraryLocator"/> so it is unit-testable.
    ///
    /// Threading/locking: the <see cref="ToolExecutor"/> already runs this on AutoCAD's main
    /// thread, inside <c>doc.LockDocument()</c>, with the supplied <paramref name="tr"/> open on
    /// the destination database (committed only on success). So ImportAssembly runs inside that
    /// ambient transaction and we never start/commit our own destination transaction or take a
    /// second document lock.
    /// </summary>
    public class CloneAssemblyFromLibraryTool : DrawingToolBase
    {
        public override string Name => "clone_assembly_from_library";
        public override string Description =>
            "Imports a complete pre-built assembly (lanes/shoulders/curbs/median/daylight) for a " +
            "road_type (urban_2lane | rural_2lane | divided_highway | collector_local) from the " +
            "MahodAI assembly-library drawing into the active drawing, ready for create_corridor. " +
            "Fails cleanly (drawing untouched) if the library or the named assembly is unavailable, " +
            "so the caller can fall back to empty-shell + guided manual add.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

        /// <summary>Returned error code when the library file cannot be found/read (clean-fallback signal).</summary>
        public const string LibraryUnavailable = "ASSEMBLY_LIBRARY_UNAVAILABLE";

        /// <summary>Returned error code when the named source assembly is absent from the library.</summary>
        public const string SourceAssemblyMissing = "LIBRARY_ASSEMBLY_NOT_FOUND";

        /// <summary>Returned error code when the import produced an assembly with no subassemblies.</summary>
        public const string ImportedAssemblyEmpty = "IMPORTED_ASSEMBLY_EMPTY";

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""road_type"": {
                    ""type"": ""string"",
                    ""enum"": [""urban_2lane"", ""rural_2lane"", ""divided_highway"", ""collector_local""],
                    ""description"": ""Israeli road type whose pre-built assembly to import. The source assembly is named MahodAI_<road_type> in the library.""
                },
                ""source_assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Explicit source assembly name in the library .dwg. Overrides the name derived from road_type.""
                },
                ""assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Destination name for the imported assembly. Defaults to the source assembly name.""
                },
                ""library_path"": {
                    ""type"": ""string"",
                    ""description"": ""Optional override of the library .dwg path (a file or a directory containing the per-version files). Falls back to MAHOD_ASSEMBLY_LIBRARY_PATH, then the bundled Contents/Assemblies/ file.""
                },
                ""use_stock_only"": {
                    ""type"": ""boolean"",
                    ""description"": ""When true, SKIP the preloaded MahodAI library (override/env/bundle) and clone the matching Civil 3D STOCK assembly directly. Default false (custom library wins when present).""
                }
            },
            ""required"": [""road_type""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document"));

            var roadType = GetStringParam(parameters, "road_type");
            var explicitSource = GetStringParam(parameters, "source_assembly_name");
            string sourceName = !string.IsNullOrWhiteSpace(explicitSource)
                ? explicitSource!.Trim()
                : AssemblyLibraryLocator.SourceAssemblyName(roadType);
            string destName = GetStringParam(parameters, "assembly_name")?.Trim() is { Length: > 0 } an
                ? an
                : sourceName;

            // ── Resolve the library file (override param → env var → bundled per-version file) ──
            int versionMajor = GetCivil3dVersionMajor();
            int year = AssemblyLibraryLocator.YearForVersionMajor(versionMajor);
            string? overridePath = GetStringParam(parameters, "library_path");
            if (string.IsNullOrWhiteSpace(overridePath))
                overridePath = Environment.GetEnvironmentVariable(AssemblyLibraryLocator.LibraryPathEnvVar);

            string? bundleDir = null;
            try
            {
                var loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                    bundleDir = Path.GetDirectoryName(loc);
            }
            catch { /* Location can be empty/throw in single-file or some hosts */ }
            // Fallback to the app base directory when Location is unavailable, so the
            // zero-config bundled path (Contents/Assemblies/) still resolves in the
            // deployed bundle (mirrors NewChatControl.xaml.cs's resolution pattern).
            if (string.IsNullOrWhiteSpace(bundleDir))
            {
                try { bundleDir = AppDomain.CurrentDomain.BaseDirectory; }
                catch { /* last resort: bundle probe skipped; override path still works */ }
            }

            // When use_stock_only is set, skip the preloaded MahodAI library (override/env/bundle)
            // entirely and go straight to the Civil 3D stock-assembly fallback below.
            bool useStockOnly = GetBoolParam(parameters, "use_stock_only", defaultValue: false);
            string? libraryPath = useStockOnly
                ? null
                : AssemblyLibraryLocator.ResolveLibraryPath(overridePath, bundleDir, year, File.Exists);

            // Zero-authoring fallback: when no firm-authored MahodAI library exists, clone a Civil 3D
            // STOCK assembly (shipped under %ProgramData%\Autodesk\C3D <year>\<lang>\Assemblies\Metric\).
            // The custom library is resolved FIRST above, so it always wins when present ("stock now,
            // custom later"). We import the stock assembly by ITS own name into our MahodAI_<type> name.
            bool usingStock = false;
            if (string.IsNullOrEmpty(libraryPath))
            {
                var (stockFile, stockAsmName) = AssemblyLibraryLocator.StockAssemblyFor(roadType);
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string c3dBase = Path.Combine(programData, "Autodesk", $"C3D {year}");
                var stockDirs = new List<string> { Path.Combine(c3dBase, "enu", "Assemblies", "Metric") };
                try
                {
                    if (Directory.Exists(c3dBase))
                        foreach (var lang in Directory.GetDirectories(c3dBase))
                            stockDirs.Add(Path.Combine(lang, "Assemblies", "Metric"));
                }
                catch { /* language-folder scan is best-effort; enu default already queued */ }
                foreach (var dir in stockDirs)
                {
                    var candidate = Path.Combine(dir, stockFile);
                    if (File.Exists(candidate))
                    {
                        libraryPath = candidate;
                        sourceName = stockAsmName;   // import the stock assembly by its name…
                        usingStock = true;           // …into destName = MahodAI_<road_type>
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(libraryPath))
            {
                return Task.FromResult(ToolResult.Fail(
                    LibraryUnavailable,
                    $"לא נמצא קובץ ספריית האסמבלי ({AssemblyLibraryLocator.LibraryFileName(year)}) " +
                    "ולא נמצאה אסמבלי סטנדרטית של Civil 3D. מומלץ להמשיך בבניית Assembly ידנית.",
                    $"Searched override='{overridePath}', bundleDir='{bundleDir}', year={year}; stock fallback also missing."));
            }

            // ── Idempotent: if the destination already has this assembly, reuse it ──
            try
            {
                foreach (ObjectId existingId in civilDoc.AssemblyCollection)
                {
                    if (tr.GetObject(existingId, OpenMode.ForRead) is CivilDb.Assembly existing &&
                        existing.Name.Equals(destName, StringComparison.OrdinalIgnoreCase))
                    {
                        var names = CollectSubassemblyNames(tr, existing);
                        return Task.FromResult(ToolResult.Ok(BuildResult(
                            destName, sourceName, roadType, libraryPath!, year,
                            names.Count, names, alreadyExisted: true)));
                    }
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to scan existing assemblies: {ex.Message}", ex.ToString()));
            }

            // ── Read the library as a side database (read-only share) ──
            using var sideDb = new Database(false, true);
            try
            {
                // (path, fileSharing, allowCPConversion, password) — long-standing overload
                // present in 2026/2027; FileShare.Read avoids contention on a shared file.
                sideDb.ReadDwgFile(libraryPath!, FileShare.Read, true, "");
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    LibraryUnavailable,
                    $"לא ניתן לקרוא את ספריית האסמבלי '{Path.GetFileName(libraryPath)}'. " +
                    "ייתכן שהקובץ נשמר בגרסה חדשה יותר מהמותקנת — המשך בבנייה ידנית.",
                    $"ReadDwgFile('{libraryPath}') failed: {ex.Message}"));
            }

            // ── Pre-flight: discover the source assembly name in the side DB. ──
            // A normally-authored MahodAI library exposes its assemblies via the Civil
            // AssemblyCollection. Civil 3D STOCK content drawings (the Assemblies tool-palette source
            // files) do NOT surface them that way through a passive side DB — the assembly lives in
            // MODEL SPACE instead. So we look in BOTH places. For a stock file we adopt whatever
            // assembly we actually find (its real, possibly-localized name), and if discovery still
            // comes up empty we DON'T bail — we let ImportAssembly try the mapped name (it scans the
            // source database itself). An authored library with a genuinely missing name still fails
            // cleanly so the caller can fall back to the empty-shell path.
            try
            {
                bool found = false;
                string? discovered = null;

                // (1) Civil AssemblyCollection — authored libraries.
                try
                {
                    CivilDocument srcCivil = CivilDocument.GetCivilDocument(sideDb);
                    using var srcTr = sideDb.TransactionManager.StartTransaction();
                    foreach (ObjectId aid in srcCivil.AssemblyCollection)
                    {
                        if (srcTr.GetObject(aid, OpenMode.ForRead) is CivilDb.Assembly a)
                        {
                            discovered ??= a.Name;
                            if (a.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                        }
                    }
                    srcTr.Commit();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] clone pre-flight (collection) skipped: {ex.Message}");
                }

                // (2) Model-space scan — Civil 3D stock content drawings.
                if (!found)
                {
                    try
                    {
                        using var srcTr = sideDb.TransactionManager.StartTransaction();
                        var bt = (BlockTable)srcTr.GetObject(sideDb.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)srcTr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            if (srcTr.GetObject(id, OpenMode.ForRead) is CivilDb.Assembly a)
                            {
                                discovered ??= a.Name;
                                if (a.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                            }
                        }
                        srcTr.Commit();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] clone pre-flight (modelspace) skipped: {ex.Message}");
                    }
                }

                // Adopt the assembly actually present (handles localized/renamed stock names).
                if (!found && discovered != null)
                {
                    sourceName = discovered;
                    found = true;
                }

                // Authored library + genuinely missing name → clean fallback. Stock → let ImportAssembly try.
                if (!found && !usingStock)
                {
                    return Task.FromResult(ToolResult.Fail(
                        SourceAssemblyMissing,
                        $"האסמבלי '{sourceName}' לא נמצא בספרייה '{Path.GetFileName(libraryPath)}'. " +
                        "המשך בבניית Assembly ידנית.",
                        $"Assembly '{sourceName}' not present in {libraryPath}."));
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: proceed to ImportAssembly, which throws if the name is missing.
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] clone_assembly_from_library pre-flight skipped: {ex.Message}");
            }

            // ── The import. Runs inside the executor's destination transaction `tr`; brings the
            //    assembly AND its subassembly/style graph across and registers it in the
            //    AssemblyCollection. Location is cosmetic (the assembly is name-addressable). ──
            // Place the assembly marker NEAR the drawing with a margin instead of at the WCS
            // origin (which for an Israeli ITM survey sits ~200 km off the drawing). Mirrors
            // the profile-view placement: anchor on a real alignment, scan model-space extents
            // with outlier defense, and pick a clear spot below/right of the geometry.
            var insertionPoint = ComputeAssemblyInsertion(tr, civilDoc);
            ObjectId newAsmId;
            try
            {
                newAsmId = civilDoc.AssemblyCollection.ImportAssembly(
                    destName, sideDb, sourceName, insertionPoint);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"ImportAssembly נכשל עבור '{sourceName}'. המשך בבנייה ידנית.",
                    $"ImportAssembly('{destName}', side, '{sourceName}') failed: {ex.Message}"));
            }

            if (newAsmId.IsNull)
            {
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    "ImportAssembly החזיר מזהה ריק.", "ImportAssembly returned ObjectId.Null."));
            }

            // ── Verify subassemblies actually came across (an empty result = corridor would fail) ──
            List<string> subNames;
            try
            {
                var imported = tr.GetObject(newAsmId, OpenMode.ForRead) as CivilDb.Assembly;
                subNames = imported != null ? CollectSubassemblyNames(tr, imported) : new List<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] clone_assembly_from_library verify failed: {ex.Message}");
                subNames = new List<string>();
            }

            if (subNames.Count == 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    ImportedAssemblyEmpty,
                    $"האסמבלי '{destName}' יובא אך ללא Subassemblies — לא ניתן לבנות Corridor. " +
                    "המשך בבנייה ידנית.",
                    $"Imported assembly '{destName}' has 0 subassemblies."));
            }

            cache.RemoveByPattern("get_drawing_summary:");
            return Task.FromResult(ToolResult.Ok(BuildResult(
                destName, sourceName, roadType, libraryPath!, year,
                subNames.Count, subNames, alreadyExisted: false, fromStock: usingStock)));
        }

        /// <summary>Assembles the success-result dictionary.</summary>
        private static Dictionary<string, object> BuildResult(
            string assemblyName, string sourceName, string? roadType, string libraryPath,
            int year, int subassemblyCount, IReadOnlyList<string> subassemblyNames, bool alreadyExisted,
            bool fromStock = false)
        {
            string verb = alreadyExisted
                ? "כבר קיים"
                : (fromStock ? "יובא מאסמבלי סטנדרטית של Civil 3D" : "יובא מהספרייה");
            string stockNote = fromStock
                ? " (חתך סטנדרטי — ניתן לכוונן רוחבי נתיב/שוליים לערכי הת\"ע)"
                : string.Empty;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["assembly_name"] = assemblyName,
                ["source_assembly_name"] = sourceName,
                ["road_type"] = roadType ?? string.Empty,
                ["already_existed"] = alreadyExisted,
                ["from_stock"] = fromStock,
                ["source"] = fromStock ? "civil3d_stock" : "mahod_library",
                ["subassembly_count"] = subassemblyCount,
                ["subassembly_names"] = subassemblyNames,
                ["library_file"] = Path.GetFileName(libraryPath),
                ["civil3d_year"] = year,
                ["message"] =
                    $"Assembly '{assemblyName}' {verb} עם {subassemblyCount} Subassemblies{stockNote} — מוכן ליצירת Corridor.",
            };
        }

        /// <summary>
        /// Collects the names of an assembly's subassemblies via the version-tolerant
        /// reflection traversal (Assembly.GetSubassemblyIds(), then Groups → per-group ids).
        /// Returns an empty list when none are found.
        /// </summary>
        private static List<string> CollectSubassemblyNames(Transaction tr, CivilDb.Assembly assembly)
        {
            var names = new List<string>();
            foreach (var subId in GetSubassemblyIds(assembly))
            {
                if (subId == ObjectId.Null) continue;
                try
                {
                    var sub = tr.GetObject(subId, OpenMode.ForRead);
                    var name = sub?.GetType().GetProperty("Name")?.GetValue(sub)?.ToString();
                    names.Add(string.IsNullOrEmpty(name) ? "Subassembly" : name!);
                }
                catch { /* skip unreadable subassembly */ }
            }
            return names;
        }

        /// <summary>
        /// Version-tolerant subassembly-id traversal (mirrors VerifyAssemblyTool's three paths):
        /// (1) the assembly's GetSubassemblyIds() method; else iterate Groups and per group try
        /// (2) group.GetSubassemblyIds() and (3) the group's Subassemblies property (reading each
        /// item's ObjectId). All three swallow per-path errors, so an empty result means the import
        /// truly has no subassemblies OR the API shape changed — the caller logs that case before
        /// rolling back, so a real import is never silently discarded without a diagnostic.
        /// </summary>
        private static List<ObjectId> GetSubassemblyIds(CivilDb.Assembly assembly)
        {
            var ids = new List<ObjectId>();

            try
            {
                var method = assembly.GetType().GetMethod("GetSubassemblyIds", Type.EmptyTypes);
                if (method?.Invoke(assembly, null) is IEnumerable direct)
                {
                    foreach (var item in direct)
                        if (item is ObjectId oid) ids.Add(oid);
                    if (ids.Count > 0) return ids;
                }
            }
            catch { }

            try
            {
                if (assembly.GetType().GetProperty("Groups")?.GetValue(assembly) is IEnumerable groups)
                {
                    foreach (var group in groups)
                    {
                        // (2) group.GetSubassemblyIds()
                        try
                        {
                            var getSubs = group.GetType().GetMethod("GetSubassemblyIds", Type.EmptyTypes);
                            if (getSubs?.Invoke(group, null) is IEnumerable subIds)
                                foreach (var item in subIds)
                                    if (item is ObjectId oid) ids.Add(oid);
                        }
                        catch { }

                        // (3) group.Subassemblies → each item's ObjectId (older API shape)
                        try
                        {
                            if (group.GetType().GetProperty("Subassemblies")?.GetValue(group)
                                is IEnumerable subs)
                            {
                                foreach (var sub in subs)
                                {
                                    if (sub?.GetType().GetProperty("ObjectId")?.GetValue(sub)
                                        is ObjectId oid && oid != ObjectId.Null && !ids.Contains(oid))
                                        ids.Add(oid);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (ids.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] clone_assembly_from_library: subassembly traversal found 0 ids " +
                    $"for assembly '{SafeName(assembly)}' — treating import as empty (will roll back).");
            }
            return ids;
        }

        private static string SafeName(CivilDb.Assembly assembly)
        {
            try { return assembly.Name; } catch { return "?"; }
        }

        /// <summary>
        /// Chooses a model-space insertion point for the imported assembly that sits NEAR the
        /// drawing with a clear margin and does not overlap existing objects — the same strategy
        /// the profile view uses. Anchors on a real alignment's bounding box and discards far
        /// outliers (e.g. a block left at the WCS origin while the survey is in Israeli ITM
        /// coordinates ~200 km away) so the marker can never be flung off the drawing, then
        /// defers to <see cref="ProfileViewPlacement.Choose"/> for a collision-free spot. Always
        /// returns a real location near the geometry — never (0,0) unless the drawing is empty.
        /// </summary>
        private static AcGe.Point3d ComputeAssemblyInsertion(Transaction tr, CivilDocument civilDoc)
        {
            try
            {
                var db = HostApplicationServices.WorkingDatabase;

                // Anchor on the first readable alignment so origin outliers can't dominate.
                double aMinX = 0, aMinY = 0, aMaxX = 0, aMaxY = 0;
                bool haveAnchor = false;
                try
                {
                    foreach (ObjectId aid in CivilObjectFinder.GetAllAlignmentIds(tr, civilDoc))
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is not CivilDb.Alignment al) continue;
                        try
                        {
                            var ext = al.GeometricExtents;
                            aMinX = ext.MinPoint.X; aMinY = ext.MinPoint.Y;
                            aMaxX = ext.MaxPoint.X; aMaxY = ext.MaxPoint.Y;
                            haveAnchor = true;
                            break;
                        }
                        catch { /* alignment without extents — try the next */ }
                    }
                }
                catch { /* finder unavailable — fall back to drawing extents below */ }

                if (!haveAnchor)
                {
                    try
                    {
                        var min = db.TileMode ? db.Extmin : db.Pextmin;
                        var max = db.TileMode ? db.Extmax : db.Pextmax;
                        aMinX = min.X; aMinY = min.Y; aMaxX = max.X; aMaxY = max.Y;
                        haveAnchor = aMaxX > aMinX || aMaxY > aMinY;
                    }
                    catch { /* keep haveAnchor=false */ }
                }
                if (!haveAnchor)
                    return new AcGe.Point3d(0, 0, 0); // genuinely empty drawing

                double alignSpan = Math.Max(aMaxX - aMinX, aMaxY - aMinY);
                double keepBuffer = Math.Max(50000.0, alignSpan * 5.0);
                var keepRegion = new ProfileViewPlacement.Rect(
                    aMinX - keepBuffer, aMinY - keepBuffer, aMaxX + keepBuffer, aMaxY + keepBuffer);

                var allExtents = new List<ProfileViewPlacement.Rect>();
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var ms = tr.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId entId in ms!)
                {
                    try
                    {
                        if (tr.GetObject(entId, OpenMode.ForRead) is not Entity ent) continue;
                        var ex = ent.GeometricExtents;
                        var rect = new ProfileViewPlacement.Rect(
                            ex.MinPoint.X, ex.MinPoint.Y, ex.MaxPoint.X, ex.MaxPoint.Y);
                        if (!rect.Intersects(keepRegion)) continue; // discard far outliers
                        allExtents.Add(rect);
                    }
                    catch { /* entities without valid extents are skipped */ }
                }

                // Small footprint — an assembly marker is a cross-section template, not a grid.
                // Engineer request (2026-06-18): place the assembly to the RIGHT of the
                // drawing (beside the profile view), not stacked beneath it.
                var placement = ProfileViewPlacement.Choose(
                    allExtents,
                    new List<ProfileViewPlacement.Rect>(),
                    anchorX: aMinX,
                    anchorY: aMinY,
                    estimatedWidth: 40.0,
                    estimatedHeight: 15.0,
                    marginM: 50.0,
                    preferRight: true);
                return new AcGe.Point3d(placement.OriginX, placement.OriginY, 0);
            }
            catch
            {
                // Defensive last resort — below the drawing extents, still never the WCS origin.
                try
                {
                    var db = HostApplicationServices.WorkingDatabase;
                    var min = db.TileMode ? db.Extmin : db.Pextmin;
                    return new AcGe.Point3d(min.X, min.Y - 80.0, 0);
                }
                catch { return new AcGe.Point3d(0, 0, 0); }
            }
        }

        /// <summary>
        /// The running Civil 3D / AutoCAD release major version (25 for 2026, 26 for 2027).
        /// Defaults to 26 (2027, the newer install) when the version can't be read.
        /// </summary>
        private static int GetCivil3dVersionMajor()
        {
            try
            {
                var v = AcadApp.Version;
                if (v != null && v.Major > 0) return v.Major;
            }
            catch { }
            return 26;
        }
    }
}
