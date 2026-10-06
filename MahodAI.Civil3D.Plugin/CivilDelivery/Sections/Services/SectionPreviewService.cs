using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;
using AcadPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AcadMText = Autodesk.AutoCAD.DatabaseServices.MText;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
using AcadDocumentLock = Autodesk.AutoCAD.ApplicationServices.DocumentLock;
using static MahodAI.CivilDelivery.Shared.SectionPreviewGeometry;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>Evidence returned to the UI for one real, transient section preview.</summary>
    public sealed class SectionPreviewDisplay
    {
        public const string GeometryOnlyNotice =
            "בדיקת גאומטריה אבחונית בלבד — אינה אישור ואינה תצוגת התוצר הסופי; " +
            "רצועות, שיפועים ובלוקים משרדיים נוצרים ונבדקים רק ב'החל' וב'אמת'";

        public required string RecordId { get; init; }
        public required Extents3d Extents { get; init; }
        public int DrawableCount { get; init; }
        public int ExistingGroundSamples { get; init; }
        public int DesignSamples { get; init; }
        public int ProjectedUtilityCount { get; init; }
        public bool FinalAppearanceRendered => false;
        public string ScopeNotice => GeometryOnlyNotice;
    }

    /// <summary>
    /// A preview computed from Civil read-back but not yet shown.  Holding this value
    /// has no screen or DWG side effect.  The owner must either publish it after the
    /// caller-owned read transaction has closed successfully, or dispose it.
    /// </summary>
    public sealed class PreparedSectionPreview : IDisposable
    {
        private List<Drawable>? _drawables;

        internal PreparedSectionPreview(
            SectionPreviewDisplay display,
            List<Drawable> drawables,
            Database ownerDatabase)
        {
            Display = display;
            _drawables = drawables;
            OwnerDatabase = ownerDatabase;
        }

        public SectionPreviewDisplay Display { get; }
        internal Database OwnerDatabase { get; }

        internal IReadOnlyList<Drawable> Drawables => _drawables
            ?? throw new InvalidOperationException("Prepared preview was already consumed.");

        internal void ReleaseOwnership() => _drawables = null;

        public void Dispose()
        {
            var drawables = _drawables;
            _drawables = null;
            if (drawables == null) return;
            foreach (var drawable in drawables)
            {
                try { drawable.Dispose(); }
                catch (Exception ex)
                {
                    MahodLogger.Warning($"Disposing unpublished section preview failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// A real cross-section PREVIEW, drawn only through AutoCAD transient graphics.
    /// Exact PLAN-selected surfaces are sampled along the actual CL and plotted at the
    /// planned sheet position. No database object is created or changed here.
    /// </summary>
    public sealed class SectionPreviewService : IDisposable
    {
        private readonly List<Drawable> _transients = new();
        private Database? _ownerDatabase;
        private long? _ownerDatabaseToken;

        public bool HasActivePreview => _transients.Count > 0;

        private const short ColorExistingGround = 3; // green
        private const short ColorDesign = 1;         // red
        private const short ColorFrame = 8;          // plot-friendly grey
        private const short ColorText = 7;           // black on white / white on black

        public PreparedSectionPreview PreparePreview(
            Database db,
            Transaction tr,
            ProjectProfile profile,
            SectionPlan plan,
            string? selectedRecordId = null)
        {
            if (SectionInputIntegrityService.HasPlanningIntegrityBlocker(plan))
                throw new InvalidOperationException(
                    "PREVIEW refused: PLAN contains unresolved source-integrity findings.");
            var integrityReason = SectionInputIntegrityService.StaleReason(
                db, plan, plan.SourceDatabaseRevision, "PREVIEW", profile);
            if (integrityReason != null)
                throw new InvalidOperationException(integrityReason);

            var record = SelectRecord(plan, selectedRecordId);
            ValidateRecord(plan, record);

            var a = new WcsPoint(record.Cl.WcsEndpoints[0], record.Cl.WcsEndpoints[1]);
            var b = new WcsPoint(record.Cl.WcsEndpoints[2], record.Cl.WcsEndpoints[3]);
            var crossing = new WcsPoint(
                record.SelectedCrossing!.Point[0], record.SelectedCrossing.Point[1]);
            var samplingPath = BuildSamplingPath(
                a, b, crossing, record.SelectedCrossing.TangentDeg,
                maximumSpacingM: 1.0, maximumSamples: 81);
            if (samplingPath.Count < 3)
                throw new InvalidOperationException(
                    "לא ניתן להציג חתך: גיאומטריית ה-CL אינה מגדירה שני צדדים אמינים סביב הציר.");

            var reviewedPair = SectionSurfacePairPlanLogic.RequireExplicit(record, profile,
                db.FingerprintGuid);
            var selected = reviewedPair != null ? new SectionSourceSelectionLogic.Selection(
                new(SectionSourceSelectionLogic.ChoiceState.Selected, reviewedPair.ExistingName, Array.Empty<string>()),
                new(SectionSourceSelectionLogic.ChoiceState.Selected, reviewedPair.DesignName, Array.Empty<string>()))
                : SectionSourceSelectionLogic.Select(
                record.PlannedSources
                    .Where(s => string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.SourceName),
                record.SelectedAlignment,
                profile.Sections.Projection.ExistingSurfacePatterns);
            if (!selected.IsReady)
                throw new InvalidOperationException(
                    "לא ניתן להציג חתך אמיתי: PLAN לא זיהה באופן חד-משמעי משטח קרקע קיימת ומשטח תכנון.");

            var eg = SampleExactSurface(db, tr, record, selected.ExistingGround.Name!, samplingPath, "קרקע קיימת");
            var design = SampleExactSurface(db, tr, record, selected.Design.Name!, samplingPath, "תכנון");
            if (!eg.HasRenderableSegment || !design.HasRenderableSegment)
                throw new InvalidOperationException(
                    "לא ניתן להציג חתך אמיתי: לפחות אחד מהמשטחים שנבחרו לא סיפק שני גבהים רציפים לאורך ה-CL. " +
                    "לא הוצגה סכימה מומצאת.");

            if (!TryCreateElevationWindow(
                    new IReadOnlyList<SurfacePoint>[] { eg.AllPoints, design.AllPoints }, out var window) ||
                window == null)
                throw new InvalidOperationException(
                    "לא ניתן להציג חתך אמיתי: טווח הגבהים שנמדד מהמשטחים אינו תקין.");
            if (!eg.TryElevationAtOffset(0.0, out var existingGroundAtAxis))
                throw new InvalidOperationException(
                    "לא ניתן להציג חתך אמיתי: משטח הקרקע הקיימת אינו מכסה את הציר, " +
                    "ולכן אין נקודת רום ייחוס אמיתית. לא הוצג ערך משוער.");

            var origin = new Point3d(
                record.PlannedLayoutPosition![0], record.PlannedLayoutPosition[1], 0);
            var offsetMin = samplingPath.Min(p => p.Offset);
            var offsetMax = samplingPath.Max(p => p.Offset);
            var frameBottom = origin.Y;
            var frameTop = frameBottom + 2.0 + window.Height * window.VerticalScale;
            var drawables = new List<Drawable>();

            // One frame and ONE stated existing-ground elevation at the axis. There
            // is deliberately no repeated elevation stack (Nataly's requirement).
            drawables.Add(MakeFrame(origin.X + offsetMin, frameBottom,
                origin.X + offsetMax, frameTop, ColorFrame));
            drawables.Add(MakeLine(
                new Point3d(origin.X, frameBottom, 0),
                new Point3d(origin.X, frameTop, 0), ColorFrame, LineWeight.LineWeight025));
            var axisGroundPlot = Map(origin.X, frameBottom,
                new SurfacePoint(0.0, existingGroundAtAxis), window);
            drawables.Add(new Circle(
                new Point3d(axisGroundPlot.X, axisGroundPlot.Y, 0), Vector3d.ZAxis, 0.28)
            {
                ColorIndex = ColorExistingGround,
            });
            drawables.Add(MakeText(
                new Point3d(axisGroundPlot.X + 0.45, axisGroundPlot.Y + 0.4, 0),
                MahodAI.CivilDelivery.Shared.SectionDrawingTextLogic.DatumText(existingGroundAtAxis),
                0.65, ColorText));

            var sectionName = string.IsNullOrWhiteSpace(record.SectionId)
                ? record.RecordId
                : record.SectionId!;
            var station = record.Station is { } st ? FormatStation(st) : "?";
            drawables.Add(MakeText(
                new Point3d(origin.X + offsetMin, frameTop + 2.4, 0),
                $"חתך {sectionName}  תחנה {station}", 1.0, ColorText));
            drawables.Add(MakeText(
                new Point3d(origin.X + offsetMin, frameTop + 1.1, 0),
                $"קרקע קיימת {eg.Name}", 0.72, ColorExistingGround));
            drawables.Add(MakeText(
                new Point3d(origin.X + offsetMin + Math.Min(14.0, (offsetMax - offsetMin) * 0.45),
                    frameTop + 1.1, 0),
                $"תכנון {design.Name}  הגבהה אנכית ×{window.VerticalScale:F1}", 0.72, ColorDesign));
            // PREVIEW has no native SampleLine/SectionView yet and its transaction is
            // deliberately aborted.  It therefore cannot truthfully render the final
            // SectionView transform, persisted office BlockReferences, strip/width row,
            // or registered grounded-slope labels.  State that scope inside the graphic
            // itself so a screenshot can never be mistaken for Nataly's deliverable.
            drawables.Add(MakeText(
                new Point3d(origin.X + offsetMin, frameTop + 3.7, 0),
                SectionPreviewDisplay.GeometryOnlyNotice, 0.62, ColorText));

            AddSurface(drawables, eg, origin.X, frameBottom, window,
                ColorExistingGround, LineWeight.LineWeight025);
            AddSurface(drawables, design, origin.X, frameBottom, window,
                ColorDesign, LineWeight.LineWeight050);

            var utilityCount = AddProjectedUtilities(
                drawables, record, profile, origin.X, frameBottom,
                offsetMin, offsetMax, window);

            // Compute extents from the known preview footprint rather than asking
            // unowned transient DBObjects for database-dependent GeometricExtents.
            var labelDepth = utilityCount == 0 ? 2.0 : 4.2;
            var extents = new Extents3d(
                new Point3d(origin.X + offsetMin - 2.0, frameBottom - labelDepth, 0),
                new Point3d(origin.X + offsetMax + 2.0, frameTop + 5.2, 0));

            var display = new SectionPreviewDisplay
            {
                RecordId = record.RecordId,
                Extents = extents,
                DrawableCount = drawables.Count,
                ExistingGroundSamples = eg.AllPoints.Count,
                DesignSamples = design.AllPoints.Count,
                ProjectedUtilityCount = utilityCount,
            };
            return new PreparedSectionPreview(display, drawables, db);
        }

        /// <summary>
        /// Publishes a fully prepared preview.  This method must be called only after
        /// the Civil read transaction that produced <paramref name="prepared"/> has
        /// aborted and disposed successfully.  The old preview remains visible until
        /// every new drawable has been accepted.  A failed add removes only the new
        /// drawables and leaves the old handles intact.
        /// </summary>
        public SectionPreviewDisplay PublishPreview(PreparedSectionPreview prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));

            var nextOwner = prepared.OwnerDatabase;
            var nextOwnerToken = nextOwner.UnmanagedObject.ToInt64();
            var active = AcadApp.DocumentManager.MdiActiveDocument;
            SectionPreviewCleanup.RequireActiveOwner(nextOwnerToken,
                active?.Database.UnmanagedObject.ToInt64(), ownerIsOpen: active != null);
            // Never replace handles belonging to another drawing with graphics from
            // the active one. Return to their owner and clean them before publication.
            if (HasActivePreview && _ownerDatabaseToken != nextOwnerToken)
                SectionPreviewCleanup.RequireActiveOwner(
                    _ownerDatabaseToken, nextOwnerToken, _ownerDatabase != null);
            _ownerDatabase = nextOwner;
            _ownerDatabaseToken = nextOwnerToken;
            var tm = TransientManager.CurrentTransientManager;
            var next = prepared.Drawables.ToList();
            var addedNext = new List<Drawable>();
            try
            {
                foreach (var drawable in next)
                {
                    // Treat every attempted add as potentially visible. Native APIs
                    // are allowed to throw after performing part of an operation; if
                    // that happens, cleanup must still try this exact handle rather
                    // than dispose it and manufacture an untracked ghost.
                    addedNext.Add(drawable);
                    tm.AddTransient(drawable, TransientDrawingMode.DirectShortTerm, 128,
                        new IntegerCollection());
                }
            }
            catch (Exception addFailure)
            {
                var cleanupFailures = new List<Exception>();
                var stillVisible = EraseWithoutLosingHandles(
                    tm, addedNext.AsEnumerable().Reverse(), cleanupFailures,
                    "rollback new preview after AddTransient failure");
                DisposeExcept(next, stillVisible);
                foreach (var drawable in stillVisible)
                    if (!_transients.Contains(drawable)) _transients.Add(drawable);
                prepared.ReleaseOwnership();
                throw PreviewPublicationFailure(addFailure, cleanupFailures);
            }

            // All new graphics are now displayable.  Only at this point may the old
            // preview be removed.  Keep the old objects alive until every erase has
            // succeeded, so a partial erase can be rolled back by re-adding them.
            var previous = _transients.ToList();
            var erasedPrevious = new List<Drawable>();
            try
            {
                foreach (var drawable in previous)
                {
                    // As with AddTransient, a native failure can be reported after a
                    // partial erase. Treat the attempted handle as needing restore.
                    erasedPrevious.Add(drawable);
                    tm.EraseTransient(drawable, new IntegerCollection());
                }
            }
            catch (Exception replaceFailure)
            {
                var cleanupFailures = new List<Exception>();
                foreach (var drawable in erasedPrevious)
                {
                    try
                    {
                        tm.AddTransient(drawable, TransientDrawingMode.DirectShortTerm, 128,
                            new IntegerCollection());
                    }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            "Failed to restore a previous section-preview drawable.", ex));
                    }
                }

                var stillVisibleNew = EraseWithoutLosingHandles(
                    tm, addedNext.AsEnumerable().Reverse(), cleanupFailures,
                    "rollback new preview after old-preview replacement failure");
                DisposeExcept(next, stillVisibleNew);

                // Never lose a handle merely because host cleanup failed.  Every old
                // drawable and every new drawable that could not be erased remains
                // reachable by a later explicit ClearPreview retry.
                _transients.Clear();
                _transients.AddRange(previous);
                foreach (var drawable in stillVisibleNew)
                    if (!_transients.Contains(drawable)) _transients.Add(drawable);
                prepared.ReleaseOwnership();
                throw PreviewPublicationFailure(replaceFailure, cleanupFailures);
            }

            _transients.Clear();
            _transients.AddRange(next);
            prepared.ReleaseOwnership();
            foreach (var drawable in previous)
            {
                try { drawable.Dispose(); }
                catch (Exception ex)
                {
                    // It is already erased and cannot become a ghost.  Report the
                    // resource cleanup problem without turning a correct display into
                    // a false red result that still has visible new graphics.
                    MahodLogger.Warning($"Disposing replaced section preview failed: {ex.Message}");
                }
            }
            return prepared.Display;
        }

        public void ClearPreview()
        {
            SectionPreviewCleanup.Clear(
                _transients, EnterOwnerCleanupContext,
                (context, drawable) => context.Manager.EraseTransient(drawable, new IntegerCollection()),
                drawable => drawable.Dispose(),
                ex => MahodLogger.Warning($"Disposing cleared section preview failed: {ex.Message}"));
            _ownerDatabase = null;
            _ownerDatabaseToken = null;
        }

        public bool OwnsPreview(Database database) => HasActivePreview &&
            _ownerDatabaseToken == database.UnmanagedObject.ToInt64();

        public void ClearPreviewForDocument(AcadDocument document)
        {
            if (!HasActivePreview) return;
            if (OwnsPreview(document.Database)) ClearPreview();
        }

        private PreviewCleanupContext EnterOwnerCleanupContext()
        {
            AcadDocument? owner = null;
            try
            {
                if (_ownerDatabase != null)
                    owner = AcadApp.DocumentManager.GetDocument(_ownerDatabase);
            }
            catch { /* A closed owner must retain its handles, never target a new DWG. */ }
            var active = AcadApp.DocumentManager.MdiActiveDocument;
            SectionPreviewCleanup.RequireActiveOwner(_ownerDatabaseToken,
                active?.Database.UnmanagedObject.ToInt64(), owner != null);
            var documentLock = owner!.LockDocument();
            try
            {
                return new PreviewCleanupContext(
                    documentLock, TransientManager.CurrentTransientManager);
            }
            catch
            {
                documentLock.Dispose();
                throw;
            }
        }

        private sealed class PreviewCleanupContext : IDisposable
        {
            private readonly AcadDocumentLock _documentLock;
            public TransientManager Manager { get; }
            public PreviewCleanupContext(AcadDocumentLock documentLock, TransientManager manager)
            {
                _documentLock = documentLock;
                Manager = manager;
            }
            public void Dispose() => _documentLock.Dispose();
        }

        public void Dispose() => ClearPreview();

        private static SectionPlanRecord SelectRecord(SectionPlan plan, string? selectedRecordId)
        {
            if (!string.IsNullOrWhiteSpace(selectedRecordId))
            {
                return plan.Records.FirstOrDefault(r =>
                           string.Equals(r.RecordId, selectedRecordId, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException(
                           "הרשומה שנבחרה אינה קיימת עוד בתוכנית. יש להריץ תכנון מחדש.");
            }

            return plan.Records
                       .Where(record => CanPreview(plan, record))
                       .OrderBy(r => r.Station ?? double.MaxValue)
                       .ThenBy(r => r.RecordId, StringComparer.Ordinal)
                       .FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       "אין בתוכנית חתך עם גאומטריה מוכחת שניתן להציג בבדיקה אבחונית.");
        }

        private static void ValidateRecord(SectionPlan plan, SectionPlanRecord record)
        {
            var reason = PreviewBlockReason(plan, record);
            if (reason != null)
                throw new InvalidOperationException(reason);
        }

        internal static bool CanPreview(SectionPlan plan, SectionPlanRecord record) =>
            SectionDiagnosticPreviewPolicy.CanPreview(plan, record);

        internal static string? PreviewBlockReason(
            SectionPlan? plan,
            SectionPlanRecord? record) =>
            SectionDiagnosticPreviewPolicy.PreviewBlockReason(plan, record);

        private static SampledSurface SampleExactSurface(
            Database db,
            Transaction tr,
            SectionPlanRecord record,
            string selectedName,
            IReadOnlyList<PathSample> path,
            string role)
        {
            var planned = record.PlannedSources.SingleOrDefault(s =>
                string.Equals(s.SourceType, "surface", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.SourceName, selectedName, StringComparison.OrdinalIgnoreCase));
            if (planned == null || string.IsNullOrWhiteSpace(planned.SourceHandle))
                throw new InvalidOperationException(
                    $"לא ניתן להציג חתך אמיתי: ל-{role} '{selectedName}' אין handle מתוכנן ומוכח.");

            if (!long.TryParse(planned.SourceHandle, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var handleValue) ||
                !db.TryGetObjectId(new Handle(handleValue), out var surfaceId))
                throw new InvalidOperationException(
                    $"לא ניתן להציג חתך אמיתי: משטח {role} '{selectedName}' אינו קיים עוד בשרטוט.");

            CivilDb.Surface surface;
            try
            {
                surface = tr.GetObject(surfaceId, OpenMode.ForRead) as CivilDb.Surface
                    ?? throw new InvalidOperationException(
                        $"המקור '{selectedName}' אינו משטח Civil שניתן לדגום.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"לא ניתן לפתוח את משטח {role} '{selectedName}' לקריאה.", ex);
            }

            if (!string.Equals(surface.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"משטח {role} השתנה מאז PLAN: צופה '{selectedName}', נמצא '{surface.Name}'. " +
                    "יש להריץ תכנון מחדש.");

            var segments = new List<List<SurfacePoint>>();
            var current = new List<SurfacePoint>();
            foreach (var point in path)
            {
                try
                {
                    var elevation = surface.FindElevationAtXY(point.X, point.Y);
                    if (!double.IsFinite(elevation)) throw new InvalidOperationException("non-finite elevation");
                    current.Add(new SurfacePoint(point.Offset, elevation));
                }
                catch
                {
                    FinishSegment();
                }
            }
            FinishSegment();

            var renderable = segments.Where(HasRenderableSegment).ToList();
            return new SampledSurface(selectedName, renderable);

            void FinishSegment()
            {
                if (current.Count > 0)
                {
                    segments.Add(current);
                    current = new List<SurfacePoint>();
                }
            }
        }

        private static void AddSurface(
            ICollection<Drawable> target,
            SampledSurface surface,
            double axisX,
            double frameBottom,
            ElevationWindow window,
            short color,
            LineWeight weight)
        {
            foreach (var segment in surface.Segments)
            {
                var polyline = new AcadPolyline { ColorIndex = color, LineWeight = weight };
                foreach (var sample in segment)
                {
                    var point = Map(axisX, frameBottom, sample, window);
                    polyline.AddVertexAt(polyline.NumberOfVertices,
                        new Point2d(point.X, point.Y), 0, 0, 0);
                }
                target.Add(polyline);
            }
        }

        private static int AddProjectedUtilities(
            ICollection<Drawable> target,
            SectionPlanRecord record,
            ProjectProfile profile,
            double axisX,
            double frameBottom,
            double offsetMin,
            double offsetMax,
            ElevationWindow window)
        {
            var rules = profile.Sections.Projection.UtilityRules.Select(r =>
                new SectionProjectionLogic.ProjectionRuleConfig(
                    r.LayerPattern, r.XrefPattern, r.Label, r.Kind, r.ColorIndex)).ToList();
            var frame = SectionCutGeometry.RequireFrame(record);
            var utilities = record.ProjectedEntities
                .Where(p => p.IntersectionWcs is { Length: >= 2 } &&
                            p.IntersectionWcs.Take(2).All(double.IsFinite))
                .Select(p => new
                {
                    Plan = p,
                    Offset = frame.OffsetOf(new SectionProjectionLogic.P2(
                        p.IntersectionWcs[0], p.IntersectionWcs[1])),
                })
                .Where(x => x.Offset >= offsetMin - 0.01 && x.Offset <= offsetMax + 0.01)
                .OrderBy(x => x.Offset)
                .ToList();

            for (var i = 0; i < utilities.Count; i++)
            {
                var item = utilities[i];
                var match = SectionProjectionLogic.Classify(
                    item.Plan.SourceLayer, item.Plan.SourceXref, rules);
                var color = match?.ColorIndex ?? (short)6;
                var markerX = axisX + item.Offset;

                // Current PLAN evidence intentionally makes no depth claim for flat
                // utility drafting. If a future plan carries a measured Z as element
                // 3, plot it; otherwise use the labelled no-depth evidence band.
                var hasRealZ = item.Plan.IntersectionWcs.Length >= 3 &&
                               double.IsFinite(item.Plan.IntersectionWcs[2]) &&
                               Math.Abs(item.Plan.IntersectionWcs[2]) >
                               SectionProjectionLogic.FlatZToleranceM;
                double markerY;
                string suffix;
                if (hasRealZ && item.Plan.IntersectionWcs[2] >= window.Datum &&
                    item.Plan.IntersectionWcs[2] <= window.Top)
                {
                    markerY = Map(axisX, frameBottom,
                        new SurfacePoint(item.Offset, item.Plan.IntersectionWcs[2]), window).Y;
                    suffix = $" {item.Plan.IntersectionWcs[2]:F2}";
                }
                else
                {
                    markerY = frameBottom + 0.75;
                    suffix = " (ללא עומק)";
                }

                target.Add(MakeLine(
                    new Point3d(markerX, frameBottom, 0),
                    new Point3d(markerX, markerY, 0), color, LineWeight.LineWeight025));
                target.Add(new Circle(new Point3d(markerX, markerY, 0), Vector3d.ZAxis, 0.35)
                {
                    ColorIndex = color,
                });

                var label = string.IsNullOrWhiteSpace(item.Plan.SystemLabel)
                    ? item.Plan.SourceLayer
                    : item.Plan.SystemLabel;
                var labelY = frameBottom - 1.5 - (i % 3) * 0.8;
                target.Add(MakeText(new Point3d(markerX + 0.25, labelY, 0),
                    label + suffix, 0.55, ColorText));
            }
            return utilities.Count;
        }

        private static List<Drawable> EraseWithoutLosingHandles(
            TransientManager tm,
            IEnumerable<Drawable> drawables,
            ICollection<Exception> failures,
            string context)
        {
            var stillVisible = new List<Drawable>();
            foreach (var drawable in drawables)
            {
                try { tm.EraseTransient(drawable, new IntegerCollection()); }
                catch (Exception ex)
                {
                    stillVisible.Add(drawable);
                    failures.Add(new InvalidOperationException(context, ex));
                }
            }
            return stillVisible;
        }

        private static void DisposeExcept(
            IEnumerable<Drawable> drawables,
            IReadOnlyCollection<Drawable> keepAlive)
        {
            foreach (var drawable in drawables)
            {
                if (keepAlive.Contains(drawable)) continue;
                try { drawable.Dispose(); }
                catch (Exception ex)
                {
                    MahodLogger.Warning($"Disposing rolled-back section preview failed: {ex.Message}");
                }
            }
        }

        private static Exception PreviewPublicationFailure(
            Exception primary,
            IReadOnlyCollection<Exception> cleanupFailures)
        {
            if (cleanupFailures.Count == 0)
                return new InvalidOperationException(
                    "Section preview display failed; the previous preview was preserved.", primary);

            var all = new List<Exception> { primary };
            all.AddRange(cleanupFailures);
            return new AggregateException(
                "Section preview display failed and one or more transient handles require a cleanup retry.",
                all);
        }

        private static Drawable MakeFrame(
            double minX, double minY, double maxX, double maxY, short color)
        {
            var frame = new AcadPolyline { Closed = true, ColorIndex = color };
            frame.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            frame.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            frame.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            frame.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
            return frame;
        }

        private static Drawable MakeLine(
            Point3d a, Point3d b, short color, LineWeight lineWeight) =>
            new Line(a, b) { ColorIndex = color, LineWeight = lineWeight };

        private static Drawable MakeText(Point3d position, string text, double height, short color)
        {
            // A transient DBText inherits the drawing's default text style.  In the
            // office drawings that style is commonly SHX, which rendered every Hebrew
            // glyph as '?' in the live preview.  An inline TrueType override makes the
            // transient self-contained without creating a TextStyleTableRecord (PREVIEW
            // must remain read-only and leave no database residue).
            var safe = (text ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("{", "\\{")
                .Replace("}", "\\}");
            return new AcadMText
            {
                Location = position,
                Contents = $@"{{\fArial|b0|i0|c0|p34;{safe}}}",
                TextHeight = height,
                ColorIndex = color,
                Attachment = AttachmentPoint.BottomLeft,
            };
        }

        private static string FormatStation(double station)
        {
            var whole = Math.Floor(station / 1000.0);
            var remainder = station - whole * 1000.0;
            return $"{whole:0}+{remainder:000.00}";
        }

        private sealed class SampledSurface
        {
            public SampledSurface(string name, List<List<SurfacePoint>> segments)
            {
                Name = name;
                Segments = segments;
                AllPoints = segments.SelectMany(s => s).OrderBy(p => p.Offset).ToList();
            }

            public string Name { get; }
            public List<List<SurfacePoint>> Segments { get; }
            public List<SurfacePoint> AllPoints { get; }
            public bool HasRenderableSegment => Segments.Any(SectionPreviewGeometry.HasRenderableSegment);

            public bool TryElevationAtOffset(double offset, out double elevation)
            {
                foreach (var segment in Segments)
                {
                    if (SectionPreviewGeometry.TryElevationAtOffset(segment, offset, out elevation))
                        return true;
                }
                elevation = 0;
                return false;
            }
        }
    }
}
