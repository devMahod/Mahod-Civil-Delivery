using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>Where a point on the plan lands on the road: the alignment, station, axis point, tangent, the marker
    /// chord ±half-width through the axis point, and the verdict (<see cref="TypicalSectionPickLogic.Assess"/>).</summary>
    internal sealed record PickResolution(
        TypicalSectionPickLogic.PickState State, string? Reason, string? AlignmentName, double Station,
        double OffsetFromAxis, P2 Axis, double TangentDeg, P2 A, P2 B)
    {
        public bool IsReady => State == TypicalSectionPickLogic.PickState.Ready;
    }

    /// <summary>
    /// The typical-sections picker (2026-10): the candidate alignments held open for reading while the engineer moves
    /// the cursor, so the live preview and the click resolve through exactly the same code. Read objects live in an
    /// open/close transaction that is independent of the transaction stack, so committing a marker in an ordinary
    /// transaction meanwhile is safe. Dispose when the picking session ends.
    /// </summary>
    internal sealed partial class TypicalSectionPicker : IDisposable
    {
        /// <summary>Magenta, like the section ticks engineers draw by hand.</summary>
        private const short MarkerColorIndex = 6;

        private readonly Database _db;
        private readonly Transaction _read;
        private readonly List<CivilDb.Alignment> _alignments;
        private readonly Dictionary<ObjectId, List<double>> _corridorStations;
        private readonly List<PlanAxis> _planAxes;

        private TypicalSectionPicker(Database db, Transaction read, List<CivilDb.Alignment> alignments, bool corridorOnly,
            Dictionary<ObjectId, List<double>> corridorStations, List<PlanAxis>? planAxes = null)
        {
            _db = db;
            _read = read;
            _alignments = alignments;
            CorridorOnly = corridorOnly;
            _corridorStations = corridorStations;
            _planAxes = planAxes ?? new List<PlanAxis>();
        }

        /// <summary>True when the candidates are corridor baselines; false when no allowed alignment has a corridor.</summary>
        public bool CorridorOnly { get; }

        /// <summary>
        /// True on a drafted drawing: no Civil alignment at all, the candidates are the plan's axis polylines (host and
        /// XREFs) and the section will be read off the plan (<see cref="DraftedTypicalSectionService"/>).
        /// </summary>
        public bool PlanOnly => _alignments.Count == 0 && _planAxes.Count > 0;

        public IReadOnlyList<string> AlignmentNames =>
            _alignments.Count > 0 ? _alignments.Select(a => a.Name).ToList() : _planAxes.Select(a => a.Name).ToList();

        /// <summary>
        /// The alignments a point may resolve to: those the profile allows, narrowed to corridor baselines when any
        /// exist, because the firm's section program draws along a corridor. With no corridor at all every allowed
        /// alignment is offered so the engineer can still mark; the confirm step then says what is missing.
        /// Each corridor alignment also carries the stations its corridors computed, so a pick snaps to one of them
        /// (<see cref="TypicalSectionPickLogic.SnapToCorridorStation"/>).
        /// </summary>
        public static TypicalSectionPicker Open(Database db, CivilDocument civilDoc, IReadOnlyList<string> allowed, bool automatic = false)
        {
            var read = db.TransactionManager.StartOpenCloseTransaction();
            try
            {
                var corridorStations = new Dictionary<ObjectId, List<double>>();
                foreach (ObjectId corridorId in civilDoc.CorridorCollection)
                {
                    if (read.GetObject(corridorId, OpenMode.ForRead) is not CivilDb.Corridor corridor) continue;
                    for (var i = 0; i < corridor.Baselines.Count; i++)
                    {
                        var baseline = corridor.Baselines[i];
                        if (!corridorStations.TryGetValue(baseline.AlignmentId, out var stations))
                            corridorStations[baseline.AlignmentId] = stations = new List<double>();
                        try { stations.AddRange(baseline.SortedStations()); }
                        catch { /* a baseline Civil has not built has no stations to snap to */ }
                    }
                }
                var baselineAlignments = new HashSet<ObjectId>(corridorStations.Keys);
                var all = new List<CivilDb.Alignment>();
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    if (read.GetObject(alignmentId, OpenMode.ForRead) is not CivilDb.Alignment alignment) continue;
                    if (automatic && !(alignment.Length > 0)) continue;
                    if (allowed.Count > 0 && !allowed.Any(pattern => ClInstructionReader.WildcardMatch(alignment.Name, pattern)))
                        continue;
                    all.Add(alignment);
                }
                var withCorridor = all.Where(a => baselineAlignments.Contains(a.ObjectId)).ToList();
                if (withCorridor.Count > 0)
                    return new TypicalSectionPicker(db, read, automatic ? all : withCorridor, corridorOnly: true, corridorStations);
                // A drafted drawing (MAAKAF DD-500X, 2026-11-02): no alignment anywhere, the axes are plan polylines.
                var planAxes = all.Count == 0
                    ? DraftedTypicalSectionService.CollectPlanAxes(read, db, allowed, automatic)
                    : new List<PlanAxis>();
                return new TypicalSectionPicker(db, read, all, corridorOnly: false, corridorStations, planAxes);
            }
            catch
            {
                read.Dispose();
                throw;
            }
        }

        /// <summary>The verdict for a point: nearest alignment by perpendicular offset, its axis point and chord.</summary>
        public PickResolution Resolve(P2 point, double halfWidth, IReadOnlyList<P2> taken)
        {
            if (_alignments.Count == 0 && _planAxes.Count > 0) return ResolveOnPlan(point, halfWidth, taken);
            CivilDb.Alignment? best = null;
            double bestStation = double.NaN, bestOffset = double.PositiveInfinity;
            foreach (var alignment in _alignments)
            {
                double station = 0, offset = 0;
                try { alignment.StationOffset(point.X, point.Y, ref station, ref offset); }
                catch { continue; } // beyond the alignment's ends
                if (!double.IsFinite(station) || !double.IsFinite(offset)) continue;
                if (Math.Abs(offset) < Math.Abs(bestOffset)) { best = alignment; bestStation = station; bestOffset = offset; }
            }
            if (best == null)
            {
                var none = TypicalSectionPickLogic.Assess(null, double.NaN, double.NaN, halfWidth, false);
                return new PickResolution(none.State, none.Reason, null, double.NaN, double.NaN, point, 0, point, point);
            }
            return Build(best, bestStation, bestOffset, halfWidth, taken);
        }

        /// <summary>
        /// The verdict on a drafted drawing: the nearest plan axis by perpendicular offset, its point and direction at
        /// the click's station, the chord ±half-width. No corridor stations to snap to: the section is cut where clicked.
        /// </summary>
        private PickResolution ResolveOnPlan(P2 point, double halfWidth, IReadOnlyList<P2> taken)
        {
            PlanAxis? best = null;
            double bestStation = double.NaN, bestOffset = double.PositiveInfinity;
            foreach (var axis in _planAxes)
            {
                if (!axis.TryStationOffset(point, out var station, out var offset, out _)) continue;
                if (Math.Abs(offset) < Math.Abs(bestOffset)) { best = axis; bestStation = station; bestOffset = offset; }
            }
            if (best == null)
            {
                var none = TypicalSectionPickLogic.Assess(null, double.NaN, double.NaN, halfWidth, false);
                return new PickResolution(none.State, none.Reason, null, double.NaN, double.NaN, point, 0, point, point);
            }
            var axisPoint = best.PointAt(bestStation, 0);
            var tangent = best.TangentDegAt(bestStation);
            var verdict = TypicalSectionPickLogic.Assess(
                best.Name, bestStation, bestOffset, halfWidth, TypicalSectionPickLogic.TooClose(taken, axisPoint));
            var (a, b) = TypicalSectionPickLogic.ValidateHalfWidth(halfWidth) == null
                ? TypicalSectionPickLogic.Endpoints(axisPoint, tangent, halfWidth)
                : (axisPoint, axisPoint);
            return new PickResolution(verdict.State, verdict.Reason, best.Name, bestStation, bestOffset, axisPoint, tangent, a, b);
        }

        /// <summary>The resolution at a known station of a named alignment (the smoke test's "click" on the axis).</summary>
        public PickResolution? AtStation(string alignmentName, double station, double halfWidth, IReadOnlyList<P2> taken)
        {
            var alignment = _alignments.FirstOrDefault(a => string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase));
            return alignment == null ? null : Build(alignment, station, 0, halfWidth, taken);
        }

        /// <summary>A plan point at a station and signed offset (left positive), for simulated clicks.</summary>
        public P2? PointAt(string alignmentName, double station, double offset)
        {
            var alignment = _alignments.FirstOrDefault(a => string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase));
            if (alignment == null) return null;
            double x = 0, y = 0;
            alignment.PointLocation(station, offset, ref x, ref y);
            return new P2(x, y);
        }

        /// <summary>The corridor station a pick at this station snaps to; the station itself when no corridor computed one.</summary>
        public double SnapStation(string alignmentName, double station)
        {
            var alignment = _alignments.FirstOrDefault(a => string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase));
            return alignment == null ? station : Snap(alignment, station);
        }

        /// <summary>The corridor's computed stations within <paramref name="radius"/> of a station, for the smoke report.</summary>
        public IReadOnlyList<double> CorridorStationsNear(string alignmentName, double station, double radius)
        {
            var alignment = _alignments.FirstOrDefault(a => string.Equals(a.Name, alignmentName, StringComparison.OrdinalIgnoreCase));
            if (alignment == null || !_corridorStations.TryGetValue(alignment.ObjectId, out var stations)) return Array.Empty<double>();
            return stations.Where(s => Math.Abs(s - station) <= radius).Distinct().OrderBy(s => s).ToList();
        }

        private double Snap(CivilDb.Alignment alignment, double station) =>
            _corridorStations.TryGetValue(alignment.ObjectId, out var stations)
                ? TypicalSectionPickLogic.SnapToCorridorStation(station, stations) ?? station
                : station;

        private PickResolution Build(CivilDb.Alignment alignment, double station, double offset, double halfWidth, IReadOnlyList<P2> taken)
        {
            station = Snap(alignment, station);
            double ax = 0, ay = 0;
            alignment.PointLocation(station, 0, ref ax, ref ay);
            var axis = new P2(ax, ay);
            var tangent = AlignmentCandidateResolver.TangentAt(alignment, station);
            var verdict = TypicalSectionPickLogic.Assess(
                alignment.Name, station, offset, halfWidth, TypicalSectionPickLogic.TooClose(taken, axis));
            var (a, b) = TypicalSectionPickLogic.ValidateHalfWidth(halfWidth) == null
                ? TypicalSectionPickLogic.Endpoints(axis, tangent, halfWidth)
                : (axis, axis);
            return new PickResolution(verdict.State, verdict.Reason, alignment.Name, station, offset, axis, tangent, a, b);
        }

        /// <summary>
        /// Writes the marker and its station text on the marker layer, in one committed transaction. The marker is a
        /// two-vertex polyline with a real width (<see cref="TypicalSectionContracts.MarkerWidth"/>): a plain LINE was lost
        /// among the plan's linework.
        /// </summary>
        public (ObjectId Line, ObjectId Text, TypicalSectionPick Pick) Commit(PickResolution resolution, double halfWidth)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            var added = AddMarker(tr, resolution, halfWidth, PlanOnly);
            tr.Commit();
            return added;
        }

        private (ObjectId Line, ObjectId Text, TypicalSectionPick Pick) AddMarker(Transaction tr, PickResolution resolution, double halfWidth, bool fromPlan)
        {
            if (!resolution.IsReady || resolution.AlignmentName == null)
                throw new InvalidOperationException(resolution.Reason ?? "The pick is not ready.");
            var layerId = EnsureLayer(tr, _db, TypicalSectionContracts.MarkerLayer, MarkerColorIndex);
            var blockTable = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var width = TypicalSectionContracts.MarkerWidth(halfWidth);
            var line = new Polyline(2) { LayerId = layerId };
            line.AddVertexAt(0, new Point2d(resolution.A.X, resolution.A.Y), 0, width, width);
            line.AddVertexAt(1, new Point2d(resolution.B.X, resolution.B.Y), 0, width, width);
            var lineId = modelSpace.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            var rotationDeg = TypicalSectionPickLogic.ReadableTextRotationDeg(resolution.TangentDeg + 90.0);
            var anchor = LabelAnchor(resolution.Axis, rotationDeg, halfWidth);
            var text = new DBText
            {
                TextString = TypicalSectionContracts.MarkerLabel(resolution.Station),
                Height = TypicalSectionContracts.MarkerTextHeight(halfWidth),
                Rotation = rotationDeg * Math.PI / 180.0,
                LayerId = layerId,
                Position = anchor,
                Justify = AttachmentPoint.BottomCenter,
                AlignmentPoint = anchor,
            };
            var textId = modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            text.AdjustAlignment(_db);

            return (lineId, textId, new TypicalSectionPick(
                line.Handle.ToString(), text.Handle.ToString(), resolution.AlignmentName, resolution.Station, halfWidth,
                resolution.Axis.X, resolution.Axis.Y, resolution.TangentDeg,
                resolution.A.X, resolution.A.Y, resolution.B.X, resolution.B.Y) { FromPlan = fromPlan });
        }

        /// <summary>
        /// Moves a committed marker (its LINE and station text) to another station of its alignment and returns the pick
        /// as it now stands, so the line in the drawing is where the section is cut. The caller holds the document lock.
        /// </summary>
        internal static TypicalSectionPick MoveTo(Database db, CivilDocument civilDoc, TypicalSectionPick pick, double station)
        {
            using var tr = db.TransactionManager.StartTransaction();
            CivilDb.Alignment? alignment = null;
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
                if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment candidate &&
                    string.Equals(candidate.Name, pick.AlignmentName, StringComparison.OrdinalIgnoreCase))
                { alignment = candidate; break; }
            if (alignment == null) throw new InvalidOperationException($"התוואי {pick.AlignmentName} לא נמצא.");

            double ax = 0, ay = 0;
            alignment.PointLocation(station, 0, ref ax, ref ay);
            var axis = new P2(ax, ay);
            var tangent = AlignmentCandidateResolver.TangentAt(alignment, station);
            var (a, b) = TypicalSectionPickLogic.Endpoints(axis, tangent, pick.HalfWidthM);

            if (db.TryGetObjectId(new Handle(Convert.ToInt64(pick.LineHandle, 16)), out var lineId) && !lineId.IsErased)
            {
                switch (tr.GetObject(lineId, OpenMode.ForWrite))
                {
                    case Polyline band when band.NumberOfVertices >= 2:
                        band.SetPointAt(0, new Point2d(a.X, a.Y));
                        band.SetPointAt(band.NumberOfVertices - 1, new Point2d(b.X, b.Y));
                        break;
                    case Line line: // a marker drawn before markers had a width
                        line.StartPoint = new Point3d(a.X, a.Y, 0);
                        line.EndPoint = new Point3d(b.X, b.Y, 0);
                        break;
                }
            }
            if (db.TryGetObjectId(new Handle(Convert.ToInt64(pick.TextHandle, 16)), out var textId) && !textId.IsErased &&
                tr.GetObject(textId, OpenMode.ForWrite) is DBText text)
            {
                var rotationDeg = TypicalSectionPickLogic.ReadableTextRotationDeg(tangent + 90.0);
                var anchor = LabelAnchor(axis, rotationDeg, pick.HalfWidthM);
                text.TextString = TypicalSectionContracts.MarkerLabel(station);
                text.Rotation = rotationDeg * Math.PI / 180.0;
                text.Position = anchor;
                text.AlignmentPoint = anchor;
                text.AdjustAlignment(db);
            }
            tr.Commit();
            return pick with
            {
                Station = station, AxisX = ax, AxisY = ay, TangentDeg = tangent, AX = a.X, AY = a.Y, BX = b.X, BY = b.Y,
            };
        }

        /// <summary>The station label's anchor: beside the band at the axis point, on the text's upper side.</summary>
        private static Point3d LabelAnchor(P2 axis, double textRotationDeg, double halfWidth)
        {
            var up = (textRotationDeg + 90.0) * Math.PI / 180.0;
            var gap = TypicalSectionContracts.MarkerWidth(halfWidth) * 0.5 + TypicalSectionContracts.MarkerTextHeight(halfWidth) * 0.2;
            return new Point3d(axis.X + Math.Cos(up) * gap, axis.Y + Math.Sin(up) * gap, 0);
        }

        public void Erase(params ObjectId[] ids)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                if (id.IsNull || id.IsErased) continue;
                try { tr.GetObject(id, OpenMode.ForWrite).Erase(); }
                catch { /* already gone: nothing to leave behind */ }
            }
            tr.Commit();
        }

        /// <summary>Midpoints of marker lines already in the drawing, so a station is never marked twice.</summary>
        public List<P2> ExistingMarkerMidpoints()
        {
            var midpoints = new List<P2>();
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var blockTable = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in modelSpace)
            {
                var dxf = id.ObjectClass.DxfName;
                if (!dxf.Equals("LWPOLYLINE", StringComparison.OrdinalIgnoreCase) &&
                    !dxf.Equals("LINE", StringComparison.OrdinalIgnoreCase)) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is not Curve marker) continue;
                if (!string.Equals(marker.Layer, TypicalSectionContracts.MarkerLayer, StringComparison.OrdinalIgnoreCase)) continue;
                midpoints.Add(new P2((marker.StartPoint.X + marker.EndPoint.X) / 2.0, (marker.StartPoint.Y + marker.EndPoint.Y) / 2.0));
            }
            return midpoints;
        }

        internal static ObjectId EnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex),
            };
            var id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        public void Dispose() => _read.Dispose();
    }
}
