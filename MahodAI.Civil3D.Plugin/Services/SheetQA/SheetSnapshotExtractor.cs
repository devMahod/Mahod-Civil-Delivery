using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcLine = Autodesk.AutoCAD.DatabaseServices.Line;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA
{
    /// <summary>Everything one layout contributes to the visual checks.</summary>
    public sealed class SheetSnapshot
    {
        public string Layout { get; set; } = string.Empty;
        public List<SheetTextRecord> Texts { get; } = new();
        public List<SheetCurveRecord> Curves { get; } = new();
        public SheetQaStats Stats { get; } = new();

        /// <summary>Model-space window the layout's viewport frames, in model coordinates.</summary>
        public bool HasModelViewport { get; set; }
        public double FootprintMinX { get; set; }
        public double FootprintMinY { get; set; }
        public double FootprintMaxX { get; set; }
        public double FootprintMaxY { get; set; }

        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Reads one paper-space layout the way it prints, and reduces it to the plain records
    /// the pure detectors consume.
    ///
    /// Ari's rule is that the sheet — a layout tab with its own legend — is the unit of
    /// work, so "visible" means visible through THIS layout's viewport, at its scale, with
    /// its layer state. Three things about that are easy to get wrong and were all measured
    /// against the real RD383 sheet on 2026-08-12:
    ///
    /// * <b>Viewport number 1 is paper space itself</b>, not a window onto the model. Only
    ///   viewports numbered 2 and up show model content.
    /// * <b>Xref entities already report host-prefixed layer names</b> ("survey|contours").
    ///   Prefixing them again makes every layer lookup miss, which silently lets frozen
    ///   layers through — that single bug inflated the overlap count from 567 to 1,393.
    /// * <b>Extents and rotation must be transformed</b> by the xref's BlockTransform, or
    ///   every xref label lands at the model origin with its authoring angle.
    ///
    /// Anything that throws while being read (proxy objects from the Civil 3D 2025 metro
    /// package, entities whose extents are undefined) is counted and skipped: a single bad
    /// entity must never abort a sheet, and a bad sheet must never abort the run.
    /// </summary>
    public sealed class SheetSnapshotExtractor
    {
        private readonly Transaction _tr;
        private readonly Database _db;
        private readonly SheetStyleResolver _styles;

        public SheetSnapshotExtractor(Transaction tr, Database db, SheetStyleResolver? styles = null)
        {
            _tr = tr ?? throw new ArgumentNullException(nameof(tr));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _styles = styles ?? new SheetStyleResolver(tr, db);
        }

        public SheetStyleResolver Styles => _styles;

        /// <summary>Layout tabs in tab order; model space is not a sheet and is excluded.</summary>
        public List<Layout> GetLayouts()
        {
            var layouts = new List<Layout>();
            if (_tr.GetObject(_db.LayoutDictionaryId, OpenMode.ForRead) is not DBDictionary dict) return layouts;

            foreach (DBDictionaryEntry entry in dict)
            {
                if (string.Equals(entry.Key, "Model", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (_tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) layouts.Add(layout);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SheetQA] Layout '{entry.Key}' could not be opened: {ex.Message}");
                }
            }

            return layouts.OrderBy(l => l.TabOrder).ToList();
        }

        public Layout? FindLayout(string layoutName) =>
            GetLayouts().FirstOrDefault(l =>
                string.Equals(l.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Collects everything the checks need for one layout.</summary>
        public SheetSnapshot Extract(Layout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            var snapshot = new SheetSnapshot { Layout = layout.LayoutName };
            if (_tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) is not BlockTableRecord paperBtr)
            {
                snapshot.Warnings.Add("Layout has no paper-space block record.");
                return snapshot;
            }

            var viewport = FindModelViewport(paperBtr, snapshot);
            CollectPaperSpace(paperBtr, snapshot);

            if (viewport != null)
            {
                CollectModelSpace(snapshot, viewport);
            }
            else
            {
                snapshot.Warnings.Add(
                    "No model viewport on this layout — only the sheet's own paper-space content was checked.");
            }

            return snapshot;
        }

        /// <summary>
        /// Picks the viewport that shows model content and records the model window it
        /// frames. Number 1 is paper space itself and is always skipped; when several real
        /// viewports exist the largest wins, since detail windows sit inside the main plan.
        /// </summary>
        private Viewport? FindModelViewport(BlockTableRecord paperBtr, SheetSnapshot snapshot)
        {
            Viewport? best = null;
            double bestArea = 0;

            foreach (ObjectId id in paperBtr)
            {
                Viewport? vp;
                try { vp = _tr.GetObject(id, OpenMode.ForRead) as Viewport; }
                catch { continue; }

                if (vp == null || vp.Number <= 1 || !vp.On) continue;
                if (vp.ViewHeight <= 0 || vp.Height <= 0 || vp.Width <= 0) continue;

                var area = vp.Width * vp.Height;
                if (area <= bestArea) continue;

                bestArea = area;
                best = vp;
            }

            if (best == null) return null;

            var modelHeight = best.ViewHeight;
            var modelWidth = modelHeight * (best.Width / best.Height);

            snapshot.HasModelViewport = true;
            snapshot.FootprintMinX = best.ViewCenter.X - modelWidth / 2;
            snapshot.FootprintMaxX = best.ViewCenter.X + modelWidth / 2;
            snapshot.FootprintMinY = best.ViewCenter.Y - modelHeight / 2;
            snapshot.FootprintMaxY = best.ViewCenter.Y + modelHeight / 2;

            var twist = NormalizedTwistDegrees(best);
            if (twist > 0.01)
            {
                snapshot.Warnings.Add($"Viewport is twisted {twist:F1}°; apparent text angles account for it.");
            }

            return best;
        }

        private void CollectPaperSpace(BlockTableRecord paperBtr, SheetSnapshot snapshot)
        {
            foreach (ObjectId id in paperBtr)
            {
                AcEntity? ent;
                try { ent = _tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { snapshot.Stats.UnreadableEntities++; continue; }

                if (ent == null) continue;
                if (IsMarkupLayer(ent.Layer)) continue;
                if (_styles.IsLayerHidden(ent.Layer)) { snapshot.Stats.HiddenLayerSkipped++; continue; }

                TryAddText(ent, null, Matrix3d.Identity, SheetSpace.Paper, 0, snapshot, checkFootprint: false);
                TryAddAttributes(ent, SheetSpace.Paper, snapshot);
            }
        }

        private void CollectModelSpace(SheetSnapshot snapshot, Viewport viewport)
        {
            if (_tr.GetObject(_db.BlockTableId, OpenMode.ForRead) is not BlockTable bt) return;
            if (_tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) is not BlockTableRecord ms) return;

            var vpFrozen = GetViewportFrozenLayers(viewport);
            var twistDeg = NormalizedTwistDegrees(viewport);

            foreach (ObjectId id in ms)
            {
                AcEntity? ent;
                try { ent = _tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { snapshot.Stats.UnreadableEntities++; continue; }

                if (ent == null) continue;
                if (IsMarkupLayer(ent.Layer)) continue;
                if (_styles.IsLayerHidden(ent.Layer) || vpFrozen.Contains(ent.Layer))
                {
                    snapshot.Stats.HiddenLayerSkipped++;
                    continue;
                }

                if (ent is BlockReference blockRef)
                {
                    CollectBlockReference(blockRef, snapshot, vpFrozen, twistDeg);
                    continue;
                }

                TryAddText(ent, null, Matrix3d.Identity, SheetSpace.Model, twistDeg, snapshot, checkFootprint: true);
                TryAddCurve(ent, null, Matrix3d.Identity, snapshot);
            }
        }

        /// <summary>
        /// Walks a block reference: its attributes always, and — when it is an xref — one
        /// level of its contents. Every organisation's network arrives as its own xref on
        /// these sheets, so this is where the bulk of the text and linework actually lives.
        /// Nested references inside an xref are not descended into; they are symbol blocks
        /// (manholes, poles) rather than annotation, and following them costs far more than
        /// it finds.
        /// </summary>
        private void CollectBlockReference(
            BlockReference blockRef, SheetSnapshot snapshot,
            HashSet<string> vpFrozen, double twistDeg)
        {
            TryAddAttributes(blockRef, SheetSpace.Model, snapshot);

            BlockTableRecord? definition;
            try { definition = _tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord; }
            catch { snapshot.Stats.UnreadableEntities++; return; }

            if (definition == null || !definition.IsFromExternalReference) return;

            snapshot.Stats.XrefsWalked++;
            var transform = blockRef.BlockTransform;
            var xrefName = definition.Name;

            foreach (ObjectId childId in definition)
            {
                AcEntity? child;
                try { child = _tr.GetObject(childId, OpenMode.ForRead) as AcEntity; }
                catch { snapshot.Stats.UnreadableEntities++; continue; }

                if (child == null || child is BlockReference) continue;

                // Xref content already reports the host-prefixed layer name, so it goes
                // straight into the same lookups as host geometry — no re-prefixing.
                if (_styles.IsLayerHidden(child.Layer) || vpFrozen.Contains(child.Layer))
                {
                    snapshot.Stats.HiddenLayerSkipped++;
                    continue;
                }

                TryAddText(child, xrefName, transform, SheetSpace.Model, twistDeg, snapshot, checkFootprint: true);
                TryAddCurve(child, xrefName, transform, snapshot);
            }
        }

        private void TryAddText(
            AcEntity ent, string? xrefName, Matrix3d transform, SheetSpace space,
            double twistDeg, SheetSnapshot snapshot, bool checkFootprint)
        {
            string raw;
            double rotation;
            double height;
            ObjectId styleId;

            switch (ent)
            {
                case DBText dbText:
                    raw = dbText.TextString;
                    rotation = dbText.Rotation;
                    height = dbText.Height;
                    styleId = dbText.TextStyleId;
                    break;
                case MText mText:
                    raw = mText.Text;
                    rotation = mText.Rotation;
                    height = mText.TextHeight;
                    styleId = mText.TextStyleId;
                    break;
                default:
                    return;
            }

            if (string.IsNullOrWhiteSpace(raw)) return;

            Extents3d extents;
            try { extents = ent.GeometricExtents; }
            catch { snapshot.Stats.UnreadableEntities++; return; }

            var apparentRotation = rotation;
            if (!transform.IsEqualTo(Matrix3d.Identity))
            {
                try
                {
                    extents.TransformBy(transform);
                    var direction = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0).TransformBy(transform);
                    apparentRotation = Math.Atan2(direction.Y, direction.X);
                }
                catch { snapshot.Stats.UnreadableEntities++; return; }
            }

            if (checkFootprint && !IntersectsFootprint(snapshot, extents))
            {
                snapshot.Stats.OutsideViewportSkipped++;
                return;
            }

            var degrees = RotationClassifier.Normalize(
                RotationClassifier.NormalizeRadians(apparentRotation) + twistDeg);

            snapshot.Texts.Add(new SheetTextRecord
            {
                RawText = raw,
                DisplayText = HebrewCadTextDecoder.Decode(raw, _styles.GetFontFile(styleId)),
                MinX = extents.MinPoint.X,
                MinY = extents.MinPoint.Y,
                MaxX = extents.MaxPoint.X,
                MaxY = extents.MaxPoint.Y,
                RotationDeg = degrees,
                Height = height,
                Layer = ent.Layer,
                SourceXref = xrefName,
                Space = space,
                Handle = SafeHandle(ent)
            });
        }

        private void TryAddAttributes(AcEntity ent, SheetSpace space, SheetSnapshot snapshot)
        {
            if (ent is not BlockReference blockRef) return;

            AttributeCollection attributes;
            try { attributes = blockRef.AttributeCollection; }
            catch { return; }

            foreach (ObjectId attId in attributes)
            {
                AttributeReference? attribute;
                try { attribute = _tr.GetObject(attId, OpenMode.ForRead) as AttributeReference; }
                catch { snapshot.Stats.UnreadableEntities++; continue; }

                if (attribute == null || attribute.Invisible) continue;
                if (_styles.IsLayerHidden(attribute.Layer)) { snapshot.Stats.HiddenLayerSkipped++; continue; }

                // Attributes are already positioned in the owning space's coordinates.
                TryAddText(attribute, null, Matrix3d.Identity, space, 0, snapshot,
                    checkFootprint: space == SheetSpace.Model);
            }
        }

        private void TryAddCurve(AcEntity ent, string? xrefName, Matrix3d transform, SheetSnapshot snapshot)
        {
            if (ent is not (AcLine or Polyline or Polyline2d or Arc)) return;

            Extents3d extents;
            try { extents = ent.GeometricExtents; }
            catch { snapshot.Stats.UnreadableEntities++; return; }

            if (!transform.IsEqualTo(Matrix3d.Identity))
            {
                try { extents.TransformBy(transform); }
                catch { snapshot.Stats.UnreadableEntities++; return; }
            }

            if (!IntersectsFootprint(snapshot, extents))
            {
                snapshot.Stats.OutsideViewportSkipped++;
                return;
            }

            snapshot.Curves.Add(new SheetCurveRecord
            {
                ColorIndex = _styles.ResolveColorIndex(ent),
                Linetype = _styles.ResolveLinetype(ent),
                Layer = ent.Layer,
                SourceXref = xrefName,
                MinX = extents.MinPoint.X,
                MinY = extents.MinPoint.Y,
                MaxX = extents.MaxPoint.X,
                MaxY = extents.MaxPoint.Y,
                Handle = SafeHandle(ent)
            });
        }

        /// <summary>
        /// Layers frozen in this specific viewport. This is what makes a per-sheet check
        /// honest: the same layer can print on one sheet and be hidden on the next.
        /// </summary>
        private HashSet<string> GetViewportFrozenLayers(Viewport viewport)
        {
            var frozen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ObjectId id in viewport.GetFrozenLayers())
                {
                    if (_tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord layer) frozen.Add(layer.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SheetQA] Could not read viewport-frozen layers: {ex.Message}");
            }
            return frozen;
        }

        /// <summary>
        /// Viewport twist in degrees, with a full turn treated as no twist — the RD383 sheet
        /// stores -2π, which is 0° of actual rotation.
        /// </summary>
        private static double NormalizedTwistDegrees(Viewport viewport)
        {
            try
            {
                var twist = RotationClassifier.NormalizeRadians(viewport.TwistAngle);
                return Math.Abs(twist - 360) < 0.01 ? 0 : twist;
            }
            catch { return 0; }
        }

        private static bool IntersectsFootprint(SheetSnapshot snapshot, Extents3d extents)
        {
            if (!snapshot.HasModelViewport) return true;

            return !(extents.MaxPoint.X < snapshot.FootprintMinX ||
                     extents.MinPoint.X > snapshot.FootprintMaxX ||
                     extents.MaxPoint.Y < snapshot.FootprintMinY ||
                     extents.MinPoint.Y > snapshot.FootprintMaxY);
        }

        internal static bool IsMarkupLayer(string layerName) =>
            string.Equals(layerName, VisualMarkupWriter.MarkupLayerName, StringComparison.OrdinalIgnoreCase);

        private static string? SafeHandle(AcEntity ent)
        {
            try { return ent.Handle.ToString(); }
            catch { return null; }
        }
    }
}
