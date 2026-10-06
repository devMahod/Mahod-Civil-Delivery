using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDb = Autodesk.Civil.DatabaseServices;
using AcadColor = Autodesk.AutoCAD.Colors.Color;
using AcadRegion = Autodesk.AutoCAD.DatabaseServices.Region;
using SectionGeometry = MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.SectionGeometryCollector;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using MahodAI.Civil3D.Plugin.Services.SheetQA;
using MahodAI.Civil3D.Plugin.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Collects mahod-evidence/1 recognition evidence during the quantity scan. Autodesk reads only: objects
    /// are opened ForRead through the scan's own transaction, no file is opened and nothing is written.
    /// All interpretation and serialization lives in Core (MahodAI.CivilDelivery.Estimate.Evidence).
    /// Every field is read in its own try/catch; a failure becomes <c>&lt;key&gt;_status=unavailable:&lt;ExceptionType&gt;</c>
    /// and never stops the scan. There is no per-record finding: one coverage summary is returned by
    /// <see cref="Complete"/>. Evidence never changes grouping, rule keys, quantities, mappings or prices.
    /// Phase 1 (traversal): <see cref="CaptureText"/> indexes text, <see cref="Read"/> reads per-entity fields,
    /// <see cref="Bind"/> remembers emitted records. Phase 2 (<see cref="Complete"/>): nearby-text queries.
    /// </summary>
    internal sealed class CivilEvidenceCollector
    {
        internal const int SamplesPerGroup = GeometrySampleBudget.DefaultPerGroup;
        internal const int MaxAttributes = 16;
        internal const int MaxProperties = 16;
        internal const int MaxValueChars = 80;
        // Native GetPointAtParameter calls per spline/ellipse/other curve. Lines, arcs, circles and polylines keep
        // every vertex instead (arcs split to the section tool's chord tolerance).
        internal const int CurveQuerySamplePoints = 256;
        // Scan-wide cap on query points kept in memory until phase 2 (16 bytes each): beyond it a record answers
        // unavailable:query-geometry-budget instead of a coarser, wrong distance.
        internal const long MaxStoredShapePoints = 8_000_000;
        /// <summary>
        /// Wall-clock budget for reading query geometry over one scan. Past it, the remaining records report
        /// unavailable:scan-time-budget instead of slowing the scan; the measurements themselves are untouched.
        /// </summary>
        internal static readonly TimeSpan ShapeTimeBudget = TimeSpan.FromSeconds(30);

        private readonly DrawingUnitPolicy.Scale _units;
        private readonly int? _hostUnitCode;
        private readonly NearbyTextIndex _texts;
        private readonly Dictionary<ObjectId, EffectiveColorPolicy.ColorValue?> _layerColors = new();
        private readonly List<(NeutralQuantityRecord Record, EntityEvidence Evidence)> _bound = new();
        private readonly GeometrySampleBudget _samples = new(SamplesPerGroup);
        // Block definition → whether the INSERT currently being traversed into it is (or lies inside) an INSERT on
        // one of our own annotation layers. The traversal refuses definition cycles, so while a definition's members
        // are read, its last recorded INSERT is the one being traversed.
        private readonly Dictionary<ObjectId, bool> _ownInsertDefinitions = new();
        private long _storedShapePoints;
        private readonly System.Diagnostics.Stopwatch _shapeClock = new();
        private int _captureFailures;
        // Legend rows of the host drawing's sheets (ReadLegends); a failure makes every record's ev_legend_row unavailable.
        private readonly List<LegendEntry> _legend = new();
        private string? _legendFailure = "legend-not-read";
        private bool _legendReadComplete = true;
        private int _legendLayouts, _legendBlocksByName, _legendBlocksGuessed;
        internal const int MaxLegendLayouts = 80;
        internal static readonly TimeSpan LegendTimeBudget = TimeSpan.FromSeconds(10);
        private int _skippedUnsupportedUnits;
        // ev_pset_component (AEC PropertySets). Holds no AEC type itself, so a host without AecPropDataMgd still loads this class.
        private readonly CivilPropertySetReader _propertySets = new();
        private bool _completed;

        /// <param name="host">The host drawing database the scan runs in (its frame is <see cref="Frame.Host"/>).</param>
        /// <param name="units">Host drawing units (host coordinates × LinearToMetres = metres).</param>
        /// <param name="hostPhysicalUnits">The resolved host unit (an approved Unitless→metres declaration counts as metres).</param>
        internal CivilEvidenceCollector(Database host, DrawingUnitPolicy.Scale units,
            PhysicalDrawingUnitPolicy.Resolution? hostPhysicalUnits,
            double radiusMetres = NearbyTextIndex.DefaultRadiusMetres, int maxTexts = NearbyTextIndex.DefaultMaxTexts)
        {
            ArgumentNullException.ThrowIfNull(host);
            _units = units ?? throw new ArgumentNullException(nameof(units));
            _hostUnitCode = hostPhysicalUnits is { IsSupported: true } ? hostPhysicalUnits.EffectiveUnitCode : null;
            _texts = new NearbyTextIndex(radiusMetres, maxTexts);
        }

        /// <summary>A record's per-entity evidence, shared by the (one or two) records emitted from that entity.</summary>
        internal sealed class EntityEvidence
        {
            internal EntityEvidence(Dictionary<string, string> fields, EvidenceShape shape, string handlePath)
            {
                Fields = fields;
                Shape = shape;
                HandlePath = handlePath;
            }

            internal IReadOnlyDictionary<string, string> Fields { get; }
            /// <summary>
            /// Host metres at full resolution (every vertex, every hatch ring), kept in memory for the phase-2 query;
            /// the preview sample (at most 64 points) is derived from it only within the sample budget. A stand-in
            /// outline is never kept: the shape then carries its failure.
            /// </summary>
            internal EvidenceShape Shape { get; }
            internal string HandlePath { get; }
            internal EvidenceValue? Nearby { get; set; }
        }

        /// <summary>One INSERT above the entity (block or XREF), innermost first.</summary>
        internal sealed record ColorNode(EffectiveColorPolicy.Subject Subject, ColorNode? Outer);

        /// <summary>
        /// The traversal state of the space an entity lives in: the XREF links crossed so far (each the
        /// row-major matrix from the XREF's space into its parent, ordinary INSERTs since the previous boundary
        /// multiplied in), the ordinary-INSERT product since the last XREF boundary, the INSERT ancestry for
        /// colour inheritance and the database whose objects this space holds.
        /// </summary>
        internal sealed class Frame
        {
            private IReadOnlyList<EffectiveColorPolicy.Subject>? _ancestorList;
            private double[]? _cachedComposed;
            private EvidenceValue _cachedTransform;

            private Frame(IReadOnlyList<(string Xref, double[] RowMajor16)> chain, Matrix3d sinceSource,
                ColorNode? ancestors, Database? sourceDatabase, string? failure, bool insideOwnInsert)
            {
                Chain = chain;
                SinceSource = sinceSource;
                Ancestors = ancestors;
                SourceDatabase = sourceDatabase;
                Failure = failure;
                InsideOwnInsert = insideOwnInsert;
            }

            internal IReadOnlyList<(string Xref, double[] RowMajor16)> Chain { get; }
            internal Matrix3d SinceSource { get; }
            internal ColorNode? Ancestors { get; }
            internal Database? SourceDatabase { get; }
            /// <summary>A transform that could not be read on the way down; transform evidence is then unavailable.</summary>
            internal string? Failure { get; }
            /// <summary>This space lies inside an INSERT (at any depth) placed on one of our own annotation layers.</summary>
            internal bool InsideOwnInsert { get; }

            internal static Frame Host(Database db) =>
                new(Array.Empty<(string Xref, double[] RowMajor16)>(), Matrix3d.Identity, null, db, null, false);

            /// <summary>Entering an ordinary INSERT's definition: its transform stays inside the current XREF link.</summary>
            internal Frame EnterInsert(BlockReference reference, CivilEvidenceCollector collector, Transaction tr)
            {
                var since = SinceSource;
                var failure = Failure;
                try { since = SinceSource * reference.BlockTransform; }
                catch (Exception ex) { failure ??= "insert-transform-" + ex.GetType().Name; }
                var subject = collector.InsertSubject(reference, tr);
                var frame = new Frame(Chain, since, new ColorNode(subject, Ancestors), SourceDatabase, failure,
                    InsideOwnInsert || NearbyTextIndex.IsOwnAnnotationLayer(subject.Layer));
                collector.NoteInsertDefinition(reference, frame.InsideOwnInsert);
                return frame;
            }

            /// <summary>Crossing an XREF boundary: one new chain link, and the XREF's own database from here down.</summary>
            internal Frame EnterXref(BlockReference reference, BlockTableRecord definition, Database? loaded,
                CivilEvidenceCollector collector, Transaction tr)
            {
                var failure = Failure;
                var chain = new List<(string Xref, double[] RowMajor16)>(Chain.Count + 1);
                chain.AddRange(Chain);
                try { chain.Add((definition.Name, RowMajor(SinceSource * reference.BlockTransform))); }
                catch (Exception ex) { failure ??= "xref-transform-" + ex.GetType().Name; }
                var subject = collector.InsertSubject(reference, tr);
                return new Frame(chain, Matrix3d.Identity, new ColorNode(subject, Ancestors), loaded, failure,
                    InsideOwnInsert || NearbyTextIndex.IsOwnAnnotationLayer(subject.Layer));
            }

            internal IReadOnlyList<EffectiveColorPolicy.Subject> AncestorSubjects()
            {
                if (_ancestorList != null) return _ancestorList;
                var list = new List<EffectiveColorPolicy.Subject>();
                for (var node = Ancestors; node != null && list.Count <= EffectiveColorPolicy.MaxDepth; node = node.Outer)
                    list.Add(node.Subject);
                return _ancestorList = list;
            }

            /// <summary>All entities of one space share one source→host matrix; build its evidence once.</summary>
            internal EvidenceValue TransformEvidence(double[] composed, Func<EvidenceValue> build)
            {
                if (_cachedComposed != null && SameMatrix(_cachedComposed, composed)) return _cachedTransform;
                var value = build();
                _cachedComposed = composed;
                _cachedTransform = value;
                return value;
            }

            private static bool SameMatrix(double[] left, double[] right)
            {
                if (left.Length != right.Length) return false;
                for (var i = 0; i < left.Length; i++)
                    if (!left[i].Equals(right[i])) return false;
                return true;
            }
        }

        /// <summary>Row-major 16 values through the explicit indexer M[row, column] (not ToArray, whose order is not asserted).</summary>
        internal static double[] RowMajor(Matrix3d matrix)
        {
            var values = new double[16];
            for (var row = 0; row < 4; row++)
                for (var column = 0; column < 4; column++)
                    values[row * 4 + column] = matrix[row, column];
            return values;
        }

        /// <summary>
        /// Indexes one DBText/MText (model space of the host or an XREF, or a member of an ordinary block) at its
        /// host position. Called before any exclusion so text on *LABEL* layers still counts; never throws and
        /// never changes scan counts. A member of an INSERT that is (or lies inside) an INSERT on one of our own
        /// annotation layers is ours whatever its own layer, and is not indexed.
        /// </summary>
        internal void CaptureText(Entity text, Matrix3d sourceToHost, string handlePath, string? xrefChain)
        {
            Point3d? anchor = null;
            try
            {
                if (!_units.IsSupported)
                {
                    _skippedUnsupportedUnits++;
                    return;
                }
                switch (text)
                {
                    // A constant attribute has no reference: its definition is the visible text of every instance.
                    case AttributeDefinition constant when constant.Constant && !constant.Invisible:
                        anchor = TextAnchor(constant);
                        Index(constant.TextString, anchor.Value, sourceToHost, handlePath, xrefChain, constant.Layer, "attribute",
                            InsideOwnInsert(constant));
                        return;
                    // Other definitions show through their references; references are read with their INSERT.
                    case AttributeDefinition:
                    case AttributeReference:
                        return;
                    case DBText single:
                        anchor = TextAnchor(single);
                        Index(single.TextString, anchor.Value, sourceToHost, handlePath, xrefChain, single.Layer, "text",
                            InsideOwnInsert(single));
                        return;
                    case MText multi:
                        anchor = multi.Location;
                        Index(multi.Text, anchor.Value, sourceToHost, handlePath, xrefChain, multi.Layer, "mtext",
                            InsideOwnInsert(multi));
                        return;
                }
            }
            catch (Exception)
            {
                Unread(anchor, sourceToHost, handlePath);
            }
        }

        /// <summary>
        /// Indexes the visible attributes of an ordinary INSERT — counted or not, at any nesting depth — at their host
        /// positions. Reading an INSERT's own <c>ev_block_attributes</c> never indexes: this is the one place, so no
        /// attribute is indexed twice and a nested implementation INSERT's labels still reach nearby records.
        /// Never throws and never changes scan counts or quantities.
        /// </summary>
        internal void CaptureAttributes(BlockReference reference, Transaction tr, Matrix3d sourceToHost, string handlePath,
            string? xrefChain, bool insideOwnInsert)
        {
            bool ownInsert;
            try { ownInsert = insideOwnInsert || NearbyTextIndex.IsOwnAnnotationLayer(reference.Layer); }
            catch (Exception) { ownInsert = insideOwnInsert; }
            List<ObjectId> ids;
            try
            {
                ids = new List<ObjectId>();
                foreach (ObjectId id in reference.AttributeCollection) ids.Add(id);
            }
            catch (Exception)
            {
                // Which attributes exist, and where, is unknown: no query may claim there is no text.
                Unread(null, sourceToHost, handlePath);
                return;
            }
            foreach (var id in ids)
            {
                Point3d? anchor = null;
                // The attribute's own leaf path when its handle is known, as for a readable attribute: its INSERT's
                // own query then skips it like any of its own labels. Unknown, it stays a neighbour of everything.
                var leafPath = handlePath;
                try { leafPath = CivilQuantityExtractionService.AppendReferenceHandlePath(handlePath, id.Handle.ToString()); }
                catch (Exception) { }
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference attribute || attribute.Invisible) continue;
                    anchor = TextAnchor(attribute);
                    CaptureAttribute(attribute, AttributeText(attribute), sourceToHost, handlePath, xrefChain, ownInsert);
                }
                catch (Exception)
                {
                    Unread(anchor, sourceToHost, leafPath);
                }
            }
        }

        /// <summary>A drawing text that could not be read: at its host position when known, otherwise everywhere.</summary>
        private void Unread(Point3d? anchor, Matrix3d sourceToHost, string handlePath)
        {
            _captureFailures++;
            try
            {
                if (anchor is { } source && _units.IsSupported)
                {
                    var host = source.TransformBy(sourceToHost);
                    _texts.ReportUnread(host.X * _units.LinearToMetres, host.Y * _units.LinearToMetres, handlePath);
                    return;
                }
            }
            catch (Exception) { }
            _texts.ReportUnread(null, null, handlePath);
        }

        /// <summary>Remembers, for the definition an INSERT is about to be traversed into, whether that INSERT is ours.</summary>
        internal void NoteInsertDefinition(BlockReference reference, bool insideOwnInsert)
        {
            // Unknown definition: its member text keeps the plain per-layer check, as before.
            try { _ownInsertDefinitions[reference.BlockTableRecord] = insideOwnInsert; }
            catch (Exception) { }
        }

        // Model-space text is owned by a model space, never by an INSERT definition, so it never matches here.
        private bool InsideOwnInsert(Entity text) =>
            _ownInsertDefinitions.TryGetValue(text.OwnerId, out var own) && own;

        private void Index(string? value, Point3d anchor, Matrix3d sourceToHost, string handlePath, string? xrefChain,
            string? layer, string kind, bool insideOwnInsert)
        {
            var host = anchor.TransformBy(sourceToHost);
            _texts.Add(value, host.X * _units.LinearToMetres, host.Y * _units.LinearToMetres, handlePath,
                string.IsNullOrWhiteSpace(xrefChain) ? "host" : xrefChain, layer, kind, insideOwnInsert);
        }

        // Left/base text anchors at Position; any other justification at its AlignmentPoint
        // (the same rule SectionAnnotationRegistry uses). GeometricExtents is avoided: it loads fonts.
        private static Point3d TextAnchor(DBText text) =>
            text.HorizontalMode == TextHorizontalMode.TextLeft && text.VerticalMode == TextVerticalMode.TextBase
                ? text.Position
                : text.AlignmentPoint;

        /// <summary>
        /// Reads the per-entity evidence of a measured entity (after its measurements were transformed).
        /// <paramref name="sourceToHost"/> is the composed matrix the measurement used.
        /// </summary>
        internal EntityEvidence Read(Entity ent, Transaction tr, Frame frame, Matrix3d sourceToHost,
            string handlePath, string? xrefChain)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            void Put(string key, Func<EvidenceValue> read)
            {
                EstimateScanTrace.Mark("evidence." + key + ".begin", handle: handlePath, type: ent.GetType().Name);
                EvidenceValue value;
                try { value = read(); }
                catch (Exception ex) { value = EvidenceValue.Unavailable(ex.GetType().Name); }
                EvidenceJson.Write(fields, key, value);
                EstimateScanTrace.Mark("evidence." + key + ".end", handle: handlePath, type: ent.GetType().Name);
            }

            EvidenceJson.Write(fields, EvidenceKeys.Schema, EvidenceValue.Read(EvidenceKeys.SchemaV1));
            Put(EvidenceKeys.XrefTransform, () => ReadTransform(ent, frame, sourceToHost));
            Put(EvidenceKeys.Hatch, () => ent is Hatch hatch ? ReadHatch(hatch) : EvidenceValue.Absent);
            Put(EvidenceKeys.BlockAttributes, () => ent is BlockReference reference
                ? ReadAttributes(reference, tr)
                : EvidenceValue.Absent);
            Put(EvidenceKeys.BlockProps, () => ent is BlockReference reference
                ? ReadDynamicProperties(reference)
                : EvidenceValue.Absent);
            // The object's own AEC PropertySets, read ForRead through this transaction (CivilPropertySetReader). A host
            // without the AEC PropertyData module, a set that cannot be read or an entity inside an INSERT/XREF (whose
            // instance data AEC may keep on the reference) is unavailable, never absent.
            Put(EvidenceKeys.PsetComponent, () => _propertySets.Read(ent, tr, insideReference: frame.Ancestors != null));
            // Matched against the sheets' legends once the traversal is complete (ReadLegends, then Complete).
            EvidenceJson.Write(fields, EvidenceKeys.LegendRow, EvidenceValue.Unavailable("not-completed"));
            Put(EvidenceKeys.ColorEffective, () => ReadColor(ent, tr, frame));
            Put(EvidenceKeys.Closed, () => ReadClosed(ent));
            // Filled later (Bind / Complete). A scan that never completes keeps these honest statuses.
            EvidenceJson.Write(fields, EvidenceKeys.NearbyText, EvidenceValue.Unavailable("not-completed"));
            EvidenceJson.Write(fields, EvidenceKeys.GeometrySample, EvidenceValue.Unavailable("sample-budget"));

            EvidenceShape shape;
            EstimateScanTrace.Mark("evidence.shape.begin", handle: handlePath, type: ent.GetType().Name);
            if (_shapeClock.Elapsed > ShapeTimeBudget)
                shape = EvidenceShape.Unavailable("scan-time-budget");
            else
            {
                _shapeClock.Start();
                try
                {
                    shape = _units.IsSupported
                        ? HostShape(ent, sourceToHost)
                        : EvidenceShape.Unavailable("host-units-unsupported");
                }
                catch (Exception ex)
                {
                    shape = EvidenceShape.Unavailable(ex.GetType().Name);
                }
                finally
                {
                    _shapeClock.Stop();
                }
            }
            EstimateScanTrace.Mark("evidence.shape.end", shape.PointCount, handlePath, ent.GetType().Name);
            return new EntityEvidence(fields, Reserve(shape), handlePath);
        }

        /// <summary>Keeps a shape for phase 2 only within the scan-wide point budget.</summary>
        private EvidenceShape Reserve(EvidenceShape shape)
        {
            if (shape.PointCount == 0) return shape;
            if (_storedShapePoints + shape.PointCount > MaxStoredShapePoints)
                return EvidenceShape.Unavailable("query-geometry-budget");
            _storedShapePoints += shape.PointCount;
            return shape;
        }

        /// <summary>Copies the entity's evidence into one final (already transformed) measurement.</summary>
        internal static void Append(QuantityMeasurement measurement, EntityEvidence evidence)
        {
            foreach (var field in evidence.Fields)
                measurement.Parameters[field.Key] = field.Value;
        }

        /// <summary>
        /// Remembers an emitted record for phase 2 and writes its host geometry preview while its recognition group
        /// (source, layer leaf, kind, unit, method class, block — <see cref="GeometrySampleBudget.GroupKey"/>) has
        /// fewer than <see cref="SamplesPerGroup"/> usable samples. Other records keep <c>unavailable:sample-budget</c>.
        /// </summary>
        internal void Bind(NeutralQuantityRecord record, EntityEvidence evidence)
        {
            if (_completed) throw new InvalidOperationException("Evidence was already completed for this scan.");
            _bound.Add((record, evidence));
            _samples.Offer(record, () => EvidenceJson.GeometrySample(evidence.Shape));
        }

        /// <summary>
        /// Reads the legend printed on every paper-space layout of the host drawing with the existing SheetQA reader (a
        /// block named like a legend — מקרא, mikra, legend — else the most legend-like block, captions paired to sample
        /// lines by row). Read-only inside the scan transaction, bounded by <see cref="MaxLegendLayouts"/> and
        /// <see cref="LegendTimeBudget"/>. Never throws. Partial rows remain available for diagnostics,
        /// but any incomplete read makes ev_legend_row unavailable rather than absent or a complete read.
        /// </summary>
        internal void ReadLegends(Transaction tr, Database db)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Fail(string reason)
            {
                _legend.Clear();
                _legendFailure = reason;
            }
            try
            {
                // Evidence mode: every legend found by name, unread XREF legends and hidden members are handled for
                // evidence; SheetQA's own reads are unchanged.
                var reader = new LegendBlockReader(tr, new SheetStyleResolver(tr, db)) { ReadForEvidence = true };
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry item in layouts)
                {
                    if (tr.GetObject(item.Value, OpenMode.ForRead) is not Layout layout || layout.ModelType) continue;
                    if (++_legendLayouts > MaxLegendLayouts) { Fail("legend-layout-cap"); return; }
                    if (clock.Elapsed > LegendTimeBudget) { Fail("legend-time-budget"); return; }
                    var read = reader.Read(layout);
                    // Capture completeness before either no-block/no-caption early continue.
                    _legendReadComplete &= read.IsComplete;
                    if (read.BlockName == null) continue;
                    var captioned = read.Rows.Where(row => !string.IsNullOrWhiteSpace(row.Label)).ToList();
                    if (captioned.Count == 0) continue;
                    if (read.IdentifiedByName) _legendBlocksByName++;
                    else _legendBlocksGuessed++;
                    foreach (var row in captioned)
                        _legend.Add(new LegendEntry(row.Label, row.ColorIndex, row.Linetype, layout.LayoutName, read.BlockName,
                            read.IdentifiedByName));
                }
                _legendFailure = null;
            }
            catch (Exception ex)
            {
                Fail("legend-" + ex.GetType().Name);
            }
        }

        private EvidenceValue LegendFor(NeutralQuantityRecord record)
        {
            try
            {
                return LegendEvidence.ApplyReadCompleteness(
                    LegendEvidence.ForRecord(_legend, _legendFailure, record.Measurement.Parameters),
                    _legendReadComplete);
            }
            catch (Exception ex) { return EvidenceValue.Unavailable(ex.GetType().Name); }
        }

        /// <summary>
        /// Phase 2, once after the traversal: answers every bound record's nearby-text query from the one index and
        /// returns the bounded coverage summary.
        /// </summary>
        internal EvidenceCoverage Complete()
        {
            if (_completed) throw new InvalidOperationException("Evidence was already completed for this scan.");
            _completed = true;
            var coverage = new EvidenceCoverage { GeometrySampleBudgetPerGroup = SamplesPerGroup };
            foreach (var (record, evidence) in _bound)
            {
                evidence.Nearby ??= QueryNearby(evidence);
                EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.NearbyText, evidence.Nearby.Value);
                EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.LegendRow, LegendFor(record));
                // A PropertySet read stopped part-way says so on every record: which records were read before it stopped
                // depends on traversal order and timing, and an approval must not turn stale between two equal scans.
                if (_propertySets.ScanFailure is { } psetStopped)
                    EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.PsetComponent, EvidenceValue.Unavailable(psetStopped));
                coverage.Tally(record.Measurement.Parameters);
            }
            coverage.GeometrySamplesEmitted = _samples.Emitted;
            coverage.TextIndex = new TextIndexCoverage(_texts.RadiusMetres, _texts.MaxTexts, _texts.Count,
                _texts.ExcludedOwnLayers, _texts.RejectedEmptyOrInvalid, _texts.DroppedByCap, _texts.IsTruncated,
                _captureFailures, _skippedUnsupportedUnits, _texts.UnreadLocated, _texts.UnreadUnlocated);
            coverage.Notes.Add("ev_nearby_text is a spatial candidate within " +
                _texts.RadiusMetres.ToString("R", CultureInfo.InvariantCulture) +
                " host metres of the record's full geometry (every vertex; arcs split to " +
                EvidenceShape.ChordToleranceMetres.ToString("R", CultureInfo.InvariantCulture) +
                " m; every hatch ring with its fill style, holes excluded) in model space, ordinary-block text and " +
                "visible attributes included; never a verified fact. Text of our own annotation layers or inside an INSERT " +
                "on one is excluded.");
            coverage.Notes.Add("ev_nearby_text and ev_geometry_sample report unavailable:approximate-geometry[:detail] where " +
                "the geometry could only be stood in for (a region, an unreadable or open hatch loop): an extents box is " +
                "never used as a shape.");
            coverage.Notes.Add("ev_nearby_text reports unavailable:" + NearbyTextIndex.CaptureIncomplete + " where a drawing " +
                "text that could not be read may be within reach (everywhere when its position is unknown), and truncated " +
                "where readable hits were found next to an unread text: a read failure is never proof that no text is there.");
            coverage.Notes.Add("ev_legend_row: rows of the legends printed on the host drawing's layouts whose sample line has " +
                "the record's effective colour and line type — a candidate, never a verified fact. match=linetype only for " +
                "a distinctive line type in a legend found by name; color for a plain line type; legend-guess when the " +
                "legend block was chosen by shape. " + (_legendFailure != null
                    ? "Legends were not read (" + _legendFailure + "): every record reports unavailable."
                    : _legendLayouts.ToString(CultureInfo.InvariantCulture) + " layouts read; legends by name: " +
                      _legendBlocksByName.ToString(CultureInfo.InvariantCulture) + ", guessed: " +
                      _legendBlocksGuessed.ToString(CultureInfo.InvariantCulture) + ", captioned rows: " +
                      _legend.Count.ToString(CultureInfo.InvariantCulture) + "."));
            if (!_legendReadComplete)
                coverage.Notes.Add("ev_legend_row: unavailable:legend-read-incomplete. Readable captions were retained " +
                    "for diagnostics, but a candidate/member read failed; the partial legend cannot prove absence or completeness.");
            coverage.Notes.Add("ev_pset_component: the manual text, integer, real and yes/no values of the AEC PropertySets attached " +
                "directly to the object, as \"<set>" + PropertySetEvidence.NameSeparator + "<property>\" name/value pairs — the " +
                "object's own statement, never a verified fact. Automatic, formula, field, increment and linked (anchor, location, " +
                "project, graphic, classification) properties restate other data and are not read: a set that has one is " +
                "truncated (partial), never complete. An entity inside an INSERT or XREF is unavailable:pset-reference-context-unread " +
                "(instance data on the reference is not resolved). At most " +
                PropertySetEvidence.MaxSets.ToString(CultureInfo.InvariantCulture) + " sets, " +
                PropertySetEvidence.MaxPropertiesPerSet.ToString(CultureInfo.InvariantCulture) + " values per set and " +
                PropertySetEvidence.MaxValues.ToString(CultureInfo.InvariantCulture) + " values per record (truncated beyond, or " +
                "where a name or value was cut); a set or property that could not be read is unavailable:" +
                PropertySetEvidence.ReadIncomplete + ", never a partial read." + (_propertySets.ModuleFailure != null
                    ? " The AEC PropertyData module was not available (" + _propertySets.ModuleFailure + "): every record " +
                      "reports unavailable, never absent."
                    : string.Empty) + (_propertySets.ScanFailure != null
                    ? " The read stopped part-way (" + _propertySets.ScanFailure + ", at most " +
                      CivilPropertySetReader.MaxScanPropertiesRead.ToString(CultureInfo.InvariantCulture) + " properties or " +
                      CivilPropertySetReader.TimeBudget.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s per scan): " +
                      "every record reports unavailable."
                    : string.Empty));
            coverage.Notes.Add("ev_geometry_sample: host metres, at most 64 points, the first " +
                SamplesPerGroup.ToString(CultureInfo.InvariantCulture) +
                " usable samples per recognition group (source, layer, kind, unit, method class, block); " +
                "a preview, never a measurement. " +
                _samples.GroupsWithSample.ToString(CultureInfo.InvariantCulture) + " groups received a sample.");
            return coverage;
        }

        private EvidenceValue QueryNearby(EntityEvidence evidence)
        {
            try { return _texts.Query(evidence.Shape, evidence.HandlePath); }
            catch (Exception ex) { return EvidenceValue.Unavailable(ex.GetType().Name); }
        }

        private EvidenceValue ReadTransform(Entity ent, Frame frame, Matrix3d sourceToHost)
        {
            if (frame.Failure != null) return EvidenceValue.Unavailable(frame.Failure);
            if (frame.SourceDatabase is not { } sourceDatabase || !SameDatabase(ent.Database, sourceDatabase))
                return EvidenceValue.Unavailable("database-mismatch");
            var composed = RowMajor(sourceToHost);
            return frame.TransformEvidence(composed, () =>
            {
                var hostUnits = XrefTransformEvidence.UnitName(_hostUnitCode);
                // XREF units are the source drawing's own explicit INSUNITS: a host declaration never covers an XREF.
                var sourceUnits = frame.Chain.Count == 0
                    ? hostUnits
                    : XrefTransformEvidence.UnitName((int)sourceDatabase.Insunits);
                return XrefTransformEvidence.Build(frame.Chain, composed, sourceUnits, hostUnits);
            });
        }

        /// <summary>The same native database (the managed wrapper instance may differ between getters).</summary>
        private static bool SameDatabase(Database? left, Database right) =>
            left != null && (ReferenceEquals(left, right) || left.UnmanagedObject == right.UnmanagedObject);

        private static EvidenceValue ReadHatch(Hatch hatch)
        {
            var pattern = hatch.PatternName;
            var patternType = hatch.PatternType.ToString();
            var scale = hatch.PatternScale;
            var angle = hatch.PatternAngle;
            var solid = hatch.IsSolidFill;
            var gradient = hatch.IsGradient;
            var associative = hatch.Associative;
            return EvidenceValue.Read(EvidenceJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("pattern", EvidenceJson.Text(pattern, MaxValueChars) ?? string.Empty);
                writer.WriteString("pattern_type", patternType);
                EvidenceJson.Number(writer, "scale", scale);
                EvidenceJson.Number(writer, "angle", angle);
                writer.WriteString("angle_units", "rad");
                writer.WriteBoolean("solid", solid);
                writer.WriteBoolean("gradient", gradient);
                writer.WriteBoolean("associative", associative);
                // Scale and angle are in the entity's own drawing; ev_xref_transform maps them to host space.
                writer.WriteString("space", "source");
                writer.WriteEndObject();
            }));
        }

        // The record's own attributes. Visible attribute text is indexed for nearby records by CaptureAttributes,
        // which the traversal calls for every ordinary INSERT; reading here never indexes it a second time.
        private static EvidenceValue ReadAttributes(BlockReference reference, Transaction tr)
        {
            var items = new List<(string Tag, string Value, bool Invisible, bool Cut)>();
            var total = 0;
            foreach (ObjectId id in reference.AttributeCollection)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference attribute) continue;
                total++;
                var raw = AttributeText(attribute);
                var invisible = attribute.Invisible;
                if (items.Count >= MaxAttributes) continue;
                var clean = EvidenceReader.Clean(raw);
                items.Add((EvidenceJson.Text(attribute.Tag, MaxValueChars) ?? string.Empty,
                    EvidenceJson.Text(raw, MaxValueChars) ?? string.Empty, invisible,
                    clean != null && clean.Length > MaxValueChars));
            }
            if (total == 0) return EvidenceValue.Absent;
            var json = EvidenceJson.Build(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("tag", item.Tag);
                    writer.WriteString("value", item.Value);
                    writer.WriteBoolean("invisible", item.Invisible);
                    if (item.Cut) writer.WriteBoolean("value_cut", true);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
            return total > items.Count ? EvidenceValue.Truncated(json, total) : EvidenceValue.Read(json);
        }

        private static string AttributeText(AttributeReference attribute)
        {
            if (!attribute.IsMTextAttribute) return attribute.TextString;
            using var text = attribute.MTextAttribute;
            return text.Text;
        }

        private void CaptureAttribute(AttributeReference attribute, string? value, Matrix3d sourceToHost,
            string handlePath, string? xrefChain, bool insideOwnInsert)
        {
            if (!_units.IsSupported)
            {
                _skippedUnsupportedUnits++;
                return;
            }
            Index(value, TextAnchor(attribute), sourceToHost,
                CivilQuantityExtractionService.AppendReferenceHandlePath(handlePath, attribute.Handle.ToString()),
                xrefChain, attribute.Layer, "attribute", insideOwnInsert);
        }

        private static EvidenceValue ReadDynamicProperties(BlockReference reference)
        {
            if (!reference.IsDynamicBlock) return EvidenceValue.Absent;
            var items = new List<(string Name, string? Value, string? ValueStatus, string Units)>();
            var total = 0;
            foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
            {
                var name = property.PropertyName;
                // The insertion point is geometry, not a property of what the block is.
                if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                if (items.Count >= MaxProperties) continue;
                string? value = null;
                string? valueStatus = null;
                try { value = PropertyValue(property.Value); }
                catch (Exception ex) { valueStatus = EvidenceValue.UnavailablePrefix + EvidenceJson.Reason(ex.GetType().Name); }
                string units;
                try { units = property.UnitsType.ToString(); }
                catch (Exception) { units = "unknown"; }
                items.Add((EvidenceJson.Text(name, MaxValueChars) ?? string.Empty, value, valueStatus, units));
            }
            if (total == 0) return EvidenceValue.Absent;
            var json = EvidenceJson.Build(writer =>
            {
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", item.Name);
                    if (item.ValueStatus == null) writer.WriteString("value", item.Value ?? string.Empty);
                    else writer.WriteString("value_status", item.ValueStatus);
                    // Distances are in block (source) units.
                    writer.WriteString("units", item.Units);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            });
            return total > items.Count ? EvidenceValue.Truncated(json, total) : EvidenceValue.Read(json);
        }

        private static string PropertyValue(object? value) => value switch
        {
            null => string.Empty,
            double number => double.IsFinite(number) ? EvidenceJson.NumberText(number) : "non-finite",
            float number => double.IsFinite(number) ? EvidenceJson.NumberText(number) : "non-finite",
            IFormattable formattable => EvidenceJson.Text(formattable.ToString(null, CultureInfo.InvariantCulture), MaxValueChars) ?? string.Empty,
            _ => EvidenceJson.Text(value.ToString(), MaxValueChars) ?? string.Empty,
        };

        private EvidenceValue ReadColor(Entity ent, Transaction tr, Frame frame)
        {
            var subject = new EffectiveColorPolicy.Subject(ToColorValue(ent.Color), ent.Layer, LayerColor(ent.LayerId, tr));
            return EffectiveColorPolicy.Resolve(subject, frame.AncestorSubjects());
        }

        private static EvidenceValue ReadClosed(Entity ent) => ent switch
        {
            BlockReference => EvidenceValue.Absent,
            CivilDb.Structure => EvidenceValue.Absent,
            Hatch => EvidenceValue.Read("true"),
            AcadRegion => EvidenceValue.Read("true"),
            Curve curve => EvidenceValue.Read(curve.Closed ? "true" : "false"),
            _ => EvidenceValue.Absent,
        };

        /// <summary>The colour subject of an INSERT; unreadable parts become unavailable during resolution.</summary>
        internal EffectiveColorPolicy.Subject InsertSubject(BlockReference reference, Transaction tr)
        {
            EffectiveColorPolicy.ColorValue color;
            string? layer = null;
            EffectiveColorPolicy.ColorValue? layerColor = null;
            try { color = ToColorValue(reference.Color); }
            catch (Exception ex) { color = EffectiveColorPolicy.ColorValue.Unsupported("unreadable-" + ex.GetType().Name); }
            try
            {
                layer = reference.Layer;
                layerColor = LayerColor(reference.LayerId, tr);
            }
            catch (Exception) { layerColor = null; }
            return new EffectiveColorPolicy.Subject(color, layer, layerColor);
        }

        private EffectiveColorPolicy.ColorValue? LayerColor(ObjectId layerId, Transaction tr)
        {
            if (_layerColors.TryGetValue(layerId, out var cached)) return cached;
            EffectiveColorPolicy.ColorValue? value;
            try
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                value = ToColorValue(layer.Color);
            }
            catch (Exception) { value = null; }
            _layerColors[layerId] = value;
            return value;
        }

        private static EffectiveColorPolicy.ColorValue ToColorValue(AcadColor color) => color.ColorMethod switch
        {
            ColorMethod.ByLayer => EffectiveColorPolicy.ColorValue.ByLayer,
            ColorMethod.ByBlock => EffectiveColorPolicy.ColorValue.ByBlock,
            ColorMethod.ByAci => EffectiveColorPolicy.ColorValue.FromAci(color.ColorIndex),
            ColorMethod.ByColor => EffectiveColorPolicy.ColorValue.FromRgb((byte)color.Red, (byte)color.Green, (byte)color.Blue),
            _ => EffectiveColorPolicy.ColorValue.Unsupported(color.ColorMethod.ToString()),
        };

        /// <summary>
        /// The entity's host geometry in metres at full resolution, for its nearby-text query and its preview.
        /// Lines, arcs, circles and polylines (LW, 2D, 3D) keep every vertex, arcs split to the section tool's chord
        /// tolerance, through the same reader the section workflow uses (SectionGeometryCollector.TryExtractVertices).
        /// Other curves are sampled by parameter (fidelity "sampled"). A hatch keeps every ring with its fill style.
        /// A region's boundary is not read in this build and an extents box is never used in its place, so a region
        /// answers approximate-geometry. This is evidence geometry, never a measurement.
        /// </summary>
        private EvidenceShape HostShape(Entity ent, Matrix3d sourceToHost)
        {
            var factor = _units.LinearToMetres;
            (double X, double Y) Host(Point3d point)
            {
                var host = point.TransformBy(sourceToHost);
                return (host.X * factor, host.Y * factor);
            }

            switch (ent)
            {
                case BlockReference reference:
                    return EvidenceShape.Open(new[] { Host(reference.Position) });
                case CivilDb.Structure structure:
                    return EvidenceShape.Open(new[] { Host(structure.Location) });
                case CivilDb.Pipe pipe:
                    // A curved pipe is only its chord here.
                    return EvidenceShape.Open(new[] { Host(pipe.StartPoint), Host(pipe.EndPoint) }, EvidenceShape.Sampled);
                case Hatch hatch:
                    return HatchShape(hatch, sourceToHost);
                case AcadRegion:
                    return EvidenceShape.Approximate("region-boundary-not-read");
                // A curve- or spline-fit polyline stores control vertices, not its shape: sample the curve instead.
                case Polyline2d fitted2d when fitted2d.PolyType != Poly2dType.SimplePoly:
                    return SampledCurve(fitted2d);
                case Polyline3d fitted3d when fitted3d.PolyType != Poly3dType.SimplePoly:
                    return SampledCurve(fitted3d);
                // Too many vertices to keep: refused before any vertex is opened.
                case Polyline light when light.NumberOfVertices > EvidenceShape.MaxPoints:
                    return EvidenceShape.Unavailable("query-geometry-too-large");
                case Polyline2d or Polyline3d when VertexCountOver(ent, EvidenceShape.MaxPoints):
                    return EvidenceShape.Unavailable("query-geometry-too-large");
                case Line or Arc or Circle or Polyline or Polyline2d or Polyline3d:
                {
                    if (!SectionGeometry.TryExtractVertices(ent, sourceToHost, out var vertices, out var closedPath, out _))
                        return EvidenceShape.Unavailable("geometry-not-read");
                    if (vertices.Count > EvidenceShape.MaxPoints) return EvidenceShape.Unavailable("query-geometry-too-large");
                    var points = new List<(double X, double Y)>(vertices.Count);
                    foreach (var vertex in vertices) points.Add((vertex.X * factor, vertex.Y * factor));
                    return closedPath
                        ? EvidenceShape.Rings(new[] { (IReadOnlyList<(double X, double Y)>)points })
                        : EvidenceShape.Open(points);
                }
                case Curve curve:
                    return SampledCurve(curve);
                default:
                    return EvidenceShape.Unavailable("unsupported-sample-type");
            }

            EvidenceShape SampledCurve(Curve curve)
            {
                var points = new List<(double X, double Y)>(CurveQuerySamplePoints);
                foreach (var point in ParameterPoints(curve, CurveQuerySamplePoints)) points.Add(Host(point));
                return curve.Closed
                    ? EvidenceShape.Rings(new[] { (IReadOnlyList<(double X, double Y)>)points }, EvidenceFill.Normal, EvidenceShape.Sampled)
                    : EvidenceShape.Open(points, EvidenceShape.Sampled);
            }
        }

        /// <summary>Counts a 2D/3D polyline's vertex ids without opening them, stopping at the limit.</summary>
        private static bool VertexCountOver(Entity ent, int limit)
        {
            var count = 0;
            if (ent is not System.Collections.IEnumerable vertices) return false;
            foreach (var _ in vertices)
                if (++count > limit) return true;
            return false;
        }

        private static List<Point3d> ParameterPoints(Curve curve, int count)
        {
            var start = curve.StartParam;
            var end = curve.EndParam;
            var points = new List<Point3d>(count);
            for (var i = 0; i < count; i++)
                points.Add(curve.GetPointAtParameter(start + (end - start) * i / (count - 1)));
            return points;
        }

        /// <summary>
        /// Every loop of a hatch as a host ring, with the hatch's fill style (so a text in a hole is outside and a
        /// second separate area is inside). Text-box loops (the gap cut around a label drawn on the hatch) and
        /// duplicate loops are left out. Straight edges are exact, circular arcs are split to the chord tolerance,
        /// elliptical and spline edges are sampled. An open or unreadable loop makes the whole shape
        /// approximate-geometry: the outline without it would claim an interior that is not the hatch's.
        /// </summary>
        private EvidenceShape HatchShape(Hatch hatch, Matrix3d sourceToHost)
        {
            var fill = hatch.HatchStyle switch
            {
                HatchStyle.Normal => EvidenceFill.Normal,
                HatchStyle.Outer => EvidenceFill.Outer,
                HatchStyle.Ignore => EvidenceFill.Ignore,
                _ => EvidenceFill.None,
            };
            if (fill == EvidenceFill.None) return EvidenceShape.Approximate("hatch-style");
            var loops = hatch.NumberOfLoops;
            if (loops < 1) return EvidenceShape.Approximate("hatch-no-loop");
            var skippedTypes = HatchLoopTypes.Textbox | HatchLoopTypes.TextIsland | HatchLoopTypes.Duplicate;
            var kept = 0;
            for (var i = 0; i < loops && kept <= EvidenceShape.MaxPaths; i++)
                if ((hatch.LoopTypeAt(i) & skippedTypes) == 0) kept++;
            if (kept > EvidenceShape.MaxPaths) return EvidenceShape.Approximate("hatch-loops-over-cap");
            var factor = _units.LinearToMetres;
            var plane = Matrix3d.PlaneToWorld(hatch.Normal);
            var elevation = hatch.Elevation;
            // Metres per hatch-plane unit along the most stretched direction: bounds every arc's chord error.
            var metresPerUnit = SectionGeometry.MaxLinearScale(sourceToHost) * factor;
            var skipped = HatchLoopTypes.Textbox | HatchLoopTypes.TextIsland | HatchLoopTypes.Duplicate;
            var sampled = false;
            var total = 0;
            var rings = new List<IReadOnlyList<(double X, double Y)>>();
            for (var i = 0; i < loops; i++)
            {
                var loop = hatch.GetLoopAt(i);
                var type = loop.LoopType;
                if ((type & skipped) != 0) continue;
                if ((type & HatchLoopTypes.NotClosed) != 0) return EvidenceShape.Approximate("hatch-loop-not-closed");
                IReadOnlyList<(double X, double Y)>? ring;
                try { ring = loop.IsPolyline ? PolylineRing(loop) : CurveRing(loop); }
                catch (Exception) { ring = null; }
                if (ring == null) return EvidenceShape.Approximate("hatch-loop-unreadable");
                total += ring.Count;
                if (total > EvidenceShape.MaxPoints) return EvidenceShape.Unavailable("query-geometry-too-large");
                rings.Add(ring);
            }
            if (rings.Count == 0) return EvidenceShape.Approximate("hatch-no-loop");
            return EvidenceShape.Rings(rings, fill, sampled ? EvidenceShape.Sampled : EvidenceShape.Exact);

            (double X, double Y) Host(double x, double y)
            {
                var host = new Point3d(x, y, elevation).TransformBy(plane).TransformBy(sourceToHost);
                return (host.X * factor, host.Y * factor);
            }

            IReadOnlyList<(double X, double Y)>? PolylineRing(HatchLoop loop)
            {
                var raw = new List<SectionHatchBoundaryGeometry.BulgePoint>();
                foreach (BulgeVertex vertex in loop.Polyline)
                {
                    if (raw.Count >= EvidenceShape.MaxPoints) return null;
                    raw.Add(new SectionHatchBoundaryGeometry.BulgePoint(vertex.Vertex.X, vertex.Vertex.Y, vertex.Bulge));
                }
                // Bulges are tessellated in the hatch plane (OCS), where their sign is defined, then transformed.
                var boundary = SectionHatchBoundaryGeometry.TessellateClosedPolyline(raw, metresPerUnit,
                    EvidenceShape.ChordToleranceMetres, EvidenceShape.MaxPoints);
                var ring = new List<(double X, double Y)>(boundary.Points.Count);
                foreach (var point in boundary.Points) ring.Add(Host(point.X, point.Y));
                return ring;
            }

            IReadOnlyList<(double X, double Y)>? CurveRing(HatchLoop loop)
            {
                var edges = new List<IReadOnlyList<(double X, double Y)>>();
                var count = 0;
                foreach (Curve2d edge in loop.Curves)
                {
                    var samples = new List<(double X, double Y)>();
                    switch (edge)
                    {
                        case LineSegment2d line:
                            samples.Add(Host(line.StartPoint.X, line.StartPoint.Y));
                            samples.Add(Host(line.EndPoint.X, line.EndPoint.Y));
                            break;
                        case CircularArc2d arc:
                        {
                            var interval = arc.GetInterval();
                            var segments = EvidenceShape.ArcSegments(arc.Radius * metresPerUnit,
                                Math.Abs(arc.EndAngle - arc.StartAngle));
                            for (var s = 0; s <= segments; s++)
                            {
                                var point = s == 0 ? arc.StartPoint : s == segments ? arc.EndPoint
                                    : arc.EvaluatePoint(interval.LowerBound +
                                        (interval.UpperBound - interval.LowerBound) * s / segments);
                                samples.Add(Host(point.X, point.Y));
                            }
                            break;
                        }
                        default:
                        {
                            // Elliptical and spline edges: sampled by parameter, so distances carry that error.
                            sampled = true;
                            var interval = edge.GetInterval();
                            for (var s = 0; s <= EvidenceShape.CurveSamplesPerEdge; s++)
                            {
                                var point = edge.EvaluatePoint(interval.LowerBound +
                                    (interval.UpperBound - interval.LowerBound) * s / EvidenceShape.CurveSamplesPerEdge);
                                samples.Add(Host(point.X, point.Y));
                            }
                            break;
                        }
                    }
                    count += samples.Count;
                    if (count > EvidenceShape.MaxPoints * 2) return null;
                    edges.Add(samples);
                }
                return EvidenceShape.JoinRing(edges);
            }
        }
    }
}
