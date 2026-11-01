using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Reads one of the firm's section blocks (MahodCivilNet <c>MSV_*</c>) the way the cross-section tools read the
    /// engineers' sheets: by the layer each line and text stands on. <c>HW-CS-CL</c> is the centreline (offset 0),
    /// <c>HW-CS-TABL</c> carries the elevation ruler as unrotated whole numbers and the title, <c>HW-CS-D</c> is the
    /// width chain — tick lines and the width texts between them. A numbered variant of a layer belongs to its family
    /// (HW-CS-D1), exactly as the firm's drafting spreads a ruler. Everything is read in the block's own coordinates.
    /// <para>
    /// MahodCivilNet 2026 as drawn live (2026-10-28): the centreline is a two-vertex polyline, the chain is ONE polyline
    /// per side that steps up and back at every tick ((0,10.5) (-3.3,10.5) (-3.3,10.633) (-3.3,10.5) …), and a width too
    /// narrow for its number has the number turned 90° below the chain with a short vertical leader. So the ticks are
    /// the chain polyline's vertices; a short vertical polyline is a tick only when the block has no chain polyline.
    /// </para>
    /// </summary>
    internal static class FirmSectionBlockReader
    {
        public const string CentrelineLayer = "HW-CS-CL";
        public const string TableLayer = "HW-CS-TABL";
        public const string ChainLayer = "HW-CS-D";
        public const string ElevationLayer = "HW-CS-ELEV";

        /// <summary>A tick is a vertical line of the chain; wider than this and it is the chain's own baseline.</summary>
        private const double TickMaxSlopeX = 1e-6;

        /// <summary>Above the chain the firm prints the elevation of every break; names go above those.</summary>
        internal sealed record BlockRead(
            ObjectId BlockReferenceId,
            ObjectId DefinitionId,
            string BlockName,
            string? Title,
            FirmSectionFrameLogic.FirmSectionFrameInput Input,
            double ChainTextHeight,
            double HeaderTopY,
            ObjectId ChainTextStyleId,
            (double MinX, double MinY, double MaxX, double MaxY)? Extents);

        /// <summary>The family name itself, or the name followed by digits only (HW-CS-D, HW-CS-D1 — never HW-CS-D-X).</summary>
        internal static bool InFamily(string? layer, string family)
        {
            if (string.IsNullOrEmpty(layer) || string.IsNullOrEmpty(family)) return false;
            var name = layer.Trim();
            if (name.Length < family.Length) return false;
            if (!string.Equals(name.Substring(0, family.Length), family, StringComparison.OrdinalIgnoreCase)) return false;
            for (var i = family.Length; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }

        public static BlockRead Read(Transaction tr, Database db, Handle blockReferenceHandle)
        {
            if (!db.TryGetObjectId(blockReferenceHandle, out var referenceId) || referenceId.IsNull || referenceId.IsErased)
                throw new InvalidOperationException($"בלוק החתך {blockReferenceHandle} לא נמצא בשרטוט.");
            var reference = (BlockReference)tr.GetObject(referenceId, OpenMode.ForRead);
            var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);

            double? centrelineX = null;
            var ruler = new List<(double Elevation, double Y)>();
            var ticks = new List<double>();
            var chainVertices = new List<double>();
            var shortVerticals = new List<double>();
            var chainTexts = new List<(double X, string Text)>();
            double chainTextHeight = 0;
            var headerTop = double.NegativeInfinity;
            var chainStyle = ObjectId.Null;
            string? title = null;

            foreach (ObjectId id in definition)
            {
                Entity? entity;
                try { entity = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                switch (entity)
                {
                    case Line line when InFamily(line.Layer, CentrelineLayer):
                        centrelineX ??= (line.StartPoint.X + line.EndPoint.X) / 2.0;
                        break;
                    case Polyline polyline when InFamily(polyline.Layer, CentrelineLayer) && polyline.NumberOfVertices >= 2:
                    {
                        var start = polyline.GetPoint2dAt(0);
                        var end = polyline.GetPoint2dAt(polyline.NumberOfVertices - 1);
                        if (Math.Abs(end.X - start.X) <= TickMaxSlopeX) centrelineX ??= (start.X + end.X) / 2.0;
                        break;
                    }
                    case Line line when InFamily(line.Layer, ChainLayer):
                        if (Math.Abs(line.EndPoint.X - line.StartPoint.X) <= TickMaxSlopeX &&
                            Math.Abs(line.EndPoint.Y - line.StartPoint.Y) > TickMaxSlopeX)
                            ticks.Add(line.StartPoint.X);
                        break;
                    case Polyline polyline when InFamily(polyline.Layer, ChainLayer) && polyline.NumberOfVertices == 2:
                    {
                        var p0 = polyline.GetPoint2dAt(0);
                        var p1 = polyline.GetPoint2dAt(1);
                        if (Math.Abs(p1.X - p0.X) <= TickMaxSlopeX && Math.Abs(p1.Y - p0.Y) > TickMaxSlopeX)
                            shortVerticals.Add(p0.X);
                        break;
                    }
                    case Polyline polyline when InFamily(polyline.Layer, ChainLayer):
                        for (var i = 0; i < polyline.NumberOfVertices; i++)
                            chainVertices.Add(polyline.GetPoint2dAt(i).X);
                        break;
                    case DBText text when InFamily(text.Layer, ChainLayer):
                    {
                        if (!TryExtents(text, out var ext)) break;
                        chainTexts.Add(((ext.MinPoint.X + ext.MaxPoint.X) / 2.0, text.TextString));
                        chainTextHeight = Math.Max(chainTextHeight, text.Height);
                        headerTop = Math.Max(headerTop, ext.MaxPoint.Y);
                        if (chainStyle.IsNull) chainStyle = text.TextStyleId;
                        break;
                    }
                    case DBText text when InFamily(text.Layer, ElevationLayer):
                        if (TryExtents(text, out var elevationExtents)) headerTop = Math.Max(headerTop, elevationExtents.MaxPoint.Y);
                        break;
                    case DBText text when InFamily(text.Layer, TableLayer):
                    {
                        var raw = text.TextString.Trim();
                        if (Math.Abs(text.Rotation) < 0.1 && raw.Length > 0 && raw.IndexOf('.') < 0 &&
                            double.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elevation))
                            ruler.Add((elevation, text.Position.Y));
                        else if (title == null && raw.Contains(" - ", StringComparison.Ordinal))
                            title = raw;
                        break;
                    }
                }
            }

            (double, double, double, double)? extents = null;
            try
            {
                var e = reference.GeometricExtents;
                extents = (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
            }
            catch { /* a block with no computable extents still has readable content */ }

            // A short vertical beside a chain polyline is the leader of a turned width number, not a tick.
            ticks.AddRange(chainVertices.Count > 0 ? chainVertices : shortVerticals);
            return new BlockRead(referenceId, definition.ObjectId, definition.Name, title,
                new FirmSectionFrameLogic.FirmSectionFrameInput(centrelineX, ruler,
                    ticks.Distinct().OrderBy(x => x).ToList(), chainTexts),
                chainTextHeight, headerTop, chainStyle, extents);
        }

        /// <summary>
        /// Writes one Hebrew name above each matched chain segment, inside the block definition (so the names move
        /// and are purged with the firm's block), on <paramref name="layerName"/>. Earlier names on that layer are
        /// replaced. Returns how many were written.
        /// </summary>
        public static int WriteNames(
            Transaction tr, Database db, BlockRead block, FirmSectionFrameLogic.FirmSectionFrame frame,
            IReadOnlyList<(int SegmentIndex, string Label)> names, string layerName)
        {
            var definition = (BlockTableRecord)tr.GetObject(block.DefinitionId, OpenMode.ForWrite);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not DBText old) continue;
                if (!string.Equals(old.Layer, layerName, StringComparison.OrdinalIgnoreCase)) continue;
                old.UpgradeOpen();
                old.Erase();
            }
            if (names.Count == 0) return 0;

            var layerId = EnsureLayer(tr, db, layerName);
            var styleId = EnsureTrueTypeStyle(tr, db);
            var height = block.ChainTextHeight > 0 ? block.ChainTextHeight : 0.4;
            var y = (double.IsFinite(block.HeaderTopY) ? block.HeaderTopY : 0.0) + height * 0.8;
            var written = 0;
            foreach (var (index, label) in names)
            {
                if (index < 0 || index >= frame.Segments.Count) continue;
                var segment = frame.Segments[index];
                if (frame.XOf((segment.FromOffsetM + segment.ToOffsetM) / 2.0) is not { } x) continue;
                // A name wider than its strip stands upright, as the firm turns a number too wide for its width.
                var stripWidth = Math.Abs(segment.LengthM * (frame.HorizontalScale ?? 1.0));
                var upright = label.Length * height * 0.75 > stripWidth;
                var anchor = new Point3d(x, y, 0);
                var text = new DBText
                {
                    TextString = label,
                    Height = height,
                    LayerId = layerId,
                    TextStyleId = styleId,
                    Position = anchor,
                    Rotation = upright ? Math.PI / 2.0 : 0.0,
                    Justify = upright ? AttachmentPoint.MiddleLeft : AttachmentPoint.BottomCenter,
                    AlignmentPoint = anchor,
                };
                definition.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                text.AdjustAlignment(db);
                written++;
            }
            // A changed definition shows on its references only after they are told so.
            var reference = (BlockReference)tr.GetObject(block.BlockReferenceId, OpenMode.ForWrite);
            reference.RecordGraphicsModified(true);
            return written;
        }

        private static bool TryExtents(Entity entity, out Extents3d extents)
        {
            try { extents = entity.GeometricExtents; return true; }
            catch { extents = default; return false; }
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string name)
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7),
            };
            var id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        /// <summary>Hebrew needs a TrueType style; the firm's SHX chain style would print question marks.</summary>
        private static ObjectId EnsureTrueTypeStyle(Transaction tr, Database db)
        {
            const string styleName = "MHD-TYPICAL-NAMES";
            var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (styles.Has(styleName)) return styles[styleName];
            styles.UpgradeOpen();
            var style = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = "arial.ttf",
                Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Arial", false, false, 0, 0),
            };
            var id = styles.Add(style);
            tr.AddNewlyCreatedDBObject(style, true);
            return id;
        }
    }
}
