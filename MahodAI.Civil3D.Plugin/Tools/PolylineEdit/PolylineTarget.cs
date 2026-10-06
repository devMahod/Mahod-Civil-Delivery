using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>What kind of entity is actually being edited.</summary>
    public enum PlTargetKind
    {
        /// <summary>LWPOLYLINE — the common case.</summary>
        Lightweight,

        /// <summary>Old "heavy" 2D POLYLINE with Vertex2d children.</summary>
        Heavy2d,

        /// <summary>3D POLYLINE with PolylineVertex3d children (no bulges, no widths).</summary>
        Poly3d,

        /// <summary>A loop of a non-associative hatch, edited in place.</summary>
        HatchLoop
    }

    /// <summary>
    /// A resolved edit target: the pure <see cref="PlShape"/> plus everything needed to write it
    /// back to the right entity, and to convert a picked point into the entity's own coordinate
    /// system.
    ///
    /// Coordinate systems are the quiet trap here. LWPOLYLINE and Hatch vertices live in the
    /// entity's OCS (its own plane), while a picked point arrives in the UCS; a Polyline3d's
    /// vertices are plain WCS. Mixing them up puts the new vertex somewhere in space that looks
    /// right only in plan view on a drawing whose UCS happens to be the world.
    /// </summary>
    public sealed class PolylineTarget
    {
        public required ObjectId Id { get; init; }
        public required PlTargetKind Kind { get; init; }
        public required PlShape Shape { get; init; }

        /// <summary>Loop index when <see cref="Kind"/> is <see cref="PlTargetKind.HatchLoop"/>.</summary>
        public int LoopIndex { get; init; }

        /// <summary>
        /// The hatch that owns this target, when the engineer clicked a hatch. For an ASSOCIATIVE
        /// hatch <see cref="Id"/> is its boundary polyline and this is the hatch to re-evaluate
        /// afterwards; for a non-associative one both point at the hatch itself.
        /// </summary>
        public ObjectId HatchId { get; init; } = ObjectId.Null;

        /// <summary>True when the original entity already carried per-vertex widths.</summary>
        public bool HadWidths { get; init; }

        /// <summary>WCS → entity coordinates (identity for a Polyline3d).</summary>
        public Matrix3d WorldToLocal { get; init; } = Matrix3d.Identity;

        /// <summary>Entity coordinates → WCS.</summary>
        public Matrix3d LocalToWorld { get; init; } = Matrix3d.Identity;

        /// <summary>Short Hebrew description of what is being edited, for chat/step lines.</summary>
        public string HebrewKind => Kind switch
        {
            PlTargetKind.Lightweight => "פוליליין",
            PlTargetKind.Heavy2d => "פוליליין (סוג ישן)",
            PlTargetKind.Poly3d => "פוליליין תלת-ממדי",
            PlTargetKind.HatchLoop => "קו מתאר של Hatch",
            _ => "פוליליין"
        };

        /// <summary>Converts a picked WCS point into the planar coordinates the shape uses.</summary>
        public Pt2 ToLocal(Point3d worldPoint)
        {
            var p = Kind == PlTargetKind.Poly3d ? worldPoint : worldPoint.TransformBy(WorldToLocal);
            return new Pt2(p.X, p.Y);
        }

        /// <summary>Converts planar shape coordinates back to a WCS point.</summary>
        public Point3d ToWorld(Pt2 local, double z)
        {
            var p = new Point3d(local.X, local.Y, Kind == PlTargetKind.Poly3d ? z : Shape.Elevation);
            return Kind == PlTargetKind.Poly3d ? p : p.TransformBy(LocalToWorld);
        }
    }

    /// <summary>
    /// Reads AutoCAD polyline-ish entities into the pure <see cref="PlShape"/> model and writes
    /// edited shapes back. Every geometric decision lives in the pure core; this file only knows
    /// how to talk to the database.
    ///
    /// Write strategy for the two "heavy" entity types (Polyline2d / Polyline3d), which have no
    /// insert-in-the-middle API worth trusting: bring the CHILD VERTEX COUNT to the target count
    /// by erasing from the end or appending to the end, then rewrite every position in order. A
    /// polyline is an ordered vertex list, so that reproduces any edit — including a mid-list
    /// insertion — without needing InsertVertexAt at all.
    /// </summary>
    public static class PolylineAdapter
    {
        /// <summary>Error code for an entity type this family cannot edit.</summary>
        public const string ErrorNotAPolyline = "NOT_A_POLYLINE";

        /// <summary>Error code for a fit/spline-fitted polyline, or a hatch loop of raw curves.</summary>
        public const string ErrorUnsupportedShape = "UNSUPPORTED_POLYLINE_SHAPE";

        /// <summary>Error code for an entity on a locked layer.</summary>
        public const string ErrorLayerLocked = "LAYER_LOCKED";

        /// <summary>
        /// Resolves an ObjectId to an editable target.
        /// </summary>
        /// <param name="hatchLoopIndex">Which loop to edit when the id is a non-associative hatch.</param>
        public static bool TryOpen(
            Transaction tr,
            ObjectId id,
            out PolylineTarget? target,
            out string? errorCode,
            out string? errorMessage,
            int hatchLoopIndex = 0)
        {
            target = null;
            errorCode = null;
            errorMessage = null;

            if (id.IsNull || id.IsErased)
            {
                errorCode = ToolErrorCodes.ObjectNotFound;
                errorMessage = "האובייקט לא נמצא בשרטוט";
                return false;
            }

            var obj = tr.GetObject(id, OpenMode.ForRead);

            if (obj is Hatch hatch)
                return TryOpenHatch(tr, hatch, hatchLoopIndex, out target, out errorCode, out errorMessage);

            if (obj is Entity ent && IsLayerLocked(tr, ent))
            {
                errorCode = ErrorLayerLocked;
                errorMessage = $"השכבה '{ent.Layer}' נעולה — בטלי את הנעילה ונסי שוב";
                return false;
            }

            switch (obj)
            {
                case Polyline lw:
                    target = ReadLightweight(lw);
                    return true;

                case Polyline2d heavy:
                    if (heavy.PolyType != Poly2dType.SimplePoly)
                    {
                        errorCode = ErrorUnsupportedShape;
                        errorMessage = "הפוליליין עבר Fit/Spline — הריצי PEDIT > Decurve ונסי שוב";
                        return false;
                    }
                    target = ReadHeavy2d(tr, heavy);
                    return true;

                case Polyline3d p3:
                    if (p3.PolyType != Poly3dType.SimplePoly)
                    {
                        errorCode = ErrorUnsupportedShape;
                        errorMessage = "פוליליין תלת-ממדי מסוג Spline — הריצי PEDIT > Decurve ונסי שוב";
                        return false;
                    }
                    target = ReadPoly3d(tr, p3);
                    return true;

                default:
                    errorCode = ErrorNotAPolyline;
                    errorMessage = $"האובייקט מסוג {obj.GetType().Name} אינו פוליליין או Hatch";
                    return false;
            }
        }

        /// <summary>
        /// A hatch resolves to whichever thing can actually be edited: an associative hatch's
        /// BOUNDARY polyline (edit it and the hatch follows), or the hatch's own loop when it has
        /// no boundary objects left. A loop built from raw lines/arcs is refused with guidance —
        /// same call the LISP makes, because rewriting such a loop as a polyline loop would
        /// silently change the hatch's definition.
        /// </summary>
        private static bool TryOpenHatch(
            Transaction tr,
            Hatch hatch,
            int loopIndex,
            out PolylineTarget? target,
            out string? errorCode,
            out string? errorMessage)
        {
            target = null;
            errorCode = null;
            errorMessage = null;

            if (IsLayerLocked(tr, hatch))
            {
                errorCode = ErrorLayerLocked;
                errorMessage = $"השכבה '{hatch.Layer}' נעולה — בטלי את הנעילה ונסי שוב";
                return false;
            }

            // Associative: edit the boundary, let AutoCAD re-evaluate the fill.
            if (hatch.Associative)
            {
                ObjectIdCollection? assoc = null;
                try { assoc = hatch.GetAssociatedObjectIds(); }
                catch { /* no association data */ }

                if (assoc != null)
                {
                    foreach (ObjectId aid in assoc)
                    {
                        if (aid.IsNull || aid.IsErased) continue;
                        var candidate = tr.GetObject(aid, OpenMode.ForRead);
                        if (candidate is not (Polyline or Polyline2d)) continue;

                        if (!TryOpen(tr, aid, out var boundaryTarget, out errorCode, out errorMessage))
                            return false;

                        target = new PolylineTarget
                        {
                            Id = boundaryTarget!.Id,
                            Kind = boundaryTarget.Kind,
                            Shape = boundaryTarget.Shape,
                            HadWidths = boundaryTarget.HadWidths,
                            WorldToLocal = boundaryTarget.WorldToLocal,
                            LocalToWorld = boundaryTarget.LocalToWorld,
                            HatchId = hatch.ObjectId,
                        };
                        return true;
                    }
                }
            }

            if (hatch.NumberOfLoops == 0)
            {
                errorCode = ErrorUnsupportedShape;
                errorMessage = "ל-Hatch אין לולאות גבול לעריכה";
                return false;
            }

            if (loopIndex < 0 || loopIndex >= hatch.NumberOfLoops) loopIndex = 0;

            // EVERY loop must be a polyline loop, not just the one being edited: writing a loop
            // back means re-appending all of them (the API has no in-place polyline-loop insert),
            // and a loop built from raw Curve2d edges cannot be reproduced faithfully that way.
            // Refusing here is the same call the LISP makes for a lines-and-arcs contour.
            for (int i = 0; i < hatch.NumberOfLoops; i++)
            {
                if ((hatch.LoopTypeAt(i) & HatchLoopTypes.Polyline) != 0) continue;
                errorCode = ErrorUnsupportedShape;
                errorMessage =
                    "קו המתאר של ה-Hatch מורכב מקווים/קשתות ולא מפוליליין — צרי סביבו פוליליין, " +
                    "החליפי את הגבול ואז ערכי אותו";
                return false;
            }

            var loop = hatch.GetLoopAt(loopIndex);
            var shape = new PlShape(closed: true, elevation: hatch.Elevation);
            foreach (BulgeVertex bv in loop.Polyline)
                shape.Vertices.Add(new PlVertex(bv.Vertex.X, bv.Vertex.Y, hatch.Elevation, bv.Bulge));

            var plane = new Plane(Point3d.Origin, hatch.Normal);
            target = new PolylineTarget
            {
                Id = hatch.ObjectId,
                Kind = PlTargetKind.HatchLoop,
                Shape = shape,
                LoopIndex = loopIndex,
                HatchId = hatch.ObjectId,
                WorldToLocal = Matrix3d.WorldToPlane(plane),
                LocalToWorld = Matrix3d.PlaneToWorld(plane),
            };
            return true;
        }

        private static PolylineTarget ReadLightweight(Polyline pl)
        {
            var shape = new PlShape(closed: pl.Closed, elevation: pl.Elevation);
            bool hadWidths = false;

            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                var p = pl.GetPoint2dAt(i);
                double bulge = pl.GetBulgeAt(i);
                double sw = 0.0, ew = 0.0;
                try
                {
                    sw = pl.GetStartWidthAt(i);
                    ew = pl.GetEndWidthAt(i);
                }
                catch
                {
                    // A polyline using ConstantWidth has no per-vertex widths — treat as 0 and
                    // leave ConstantWidth alone on write.
                }
                if (sw != 0.0 || ew != 0.0) hadWidths = true;
                shape.Vertices.Add(new PlVertex(p.X, p.Y, pl.Elevation, bulge, sw, ew));
            }

            var plane = new Plane(Point3d.Origin, pl.Normal);
            return new PolylineTarget
            {
                Id = pl.ObjectId,
                Kind = PlTargetKind.Lightweight,
                Shape = shape,
                HadWidths = hadWidths,
                WorldToLocal = Matrix3d.WorldToPlane(plane),
                LocalToWorld = Matrix3d.PlaneToWorld(plane),
            };
        }

        private static PolylineTarget ReadHeavy2d(Transaction tr, Polyline2d heavy)
        {
            var shape = new PlShape(closed: heavy.Closed, elevation: heavy.Elevation);
            bool hadWidths = false;

            foreach (ObjectId vid in heavy)
            {
                if (tr.GetObject(vid, OpenMode.ForRead) is not Vertex2d v) continue;
                if (v.VertexType == Vertex2dType.SplineControlVertex) continue;
                if (v.StartWidth != 0.0 || v.EndWidth != 0.0) hadWidths = true;
                shape.Vertices.Add(new PlVertex(
                    v.Position.X, v.Position.Y, heavy.Elevation, v.Bulge, v.StartWidth, v.EndWidth));
            }

            var plane = new Plane(Point3d.Origin, heavy.Normal);
            return new PolylineTarget
            {
                Id = heavy.ObjectId,
                Kind = PlTargetKind.Heavy2d,
                Shape = shape,
                HadWidths = hadWidths,
                WorldToLocal = Matrix3d.WorldToPlane(plane),
                LocalToWorld = Matrix3d.PlaneToWorld(plane),
            };
        }

        private static PolylineTarget ReadPoly3d(Transaction tr, Polyline3d p3)
        {
            var shape = new PlShape(closed: p3.Closed, is3d: true);
            foreach (ObjectId vid in p3)
            {
                if (tr.GetObject(vid, OpenMode.ForRead) is not PolylineVertex3d v) continue;
                if (v.VertexType == Vertex3dType.ControlVertex) continue;
                shape.Vertices.Add(new PlVertex(v.Position.X, v.Position.Y, v.Position.Z));
            }

            return new PolylineTarget
            {
                Id = p3.ObjectId,
                Kind = PlTargetKind.Poly3d,
                Shape = shape,
            };
        }

        /// <summary>
        /// Writes the (edited) shape back to its entity. The caller's transaction commits it —
        /// or rolls it all back, which is what makes a cancelled interactive edit side-effect-free.
        /// </summary>
        public static bool TryWrite(Transaction tr, PolylineTarget target, out string? errorMessage)
        {
            errorMessage = null;
            if (target.Shape.Count < 2 && target.Kind != PlTargetKind.HatchLoop)
            {
                errorMessage = "פחות משני קודקודים — אי אפשר לשמור פוליליין כזה";
                return false;
            }

            try
            {
                switch (target.Kind)
                {
                    case PlTargetKind.Lightweight:
                        WriteLightweight(tr, target);
                        break;
                    case PlTargetKind.Heavy2d:
                        WriteHeavy2d(tr, target);
                        break;
                    case PlTargetKind.Poly3d:
                        WritePoly3d(tr, target);
                        break;
                    case PlTargetKind.HatchLoop:
                        WriteHatchLoop(tr, target);
                        break;
                }

                // An associative hatch whose boundary we just moved has to be re-evaluated;
                // AutoCAD does not do it for us when the boundary is edited through the API.
                if (target.Kind != PlTargetKind.HatchLoop && !target.HatchId.IsNull && !target.HatchId.IsErased)
                {
                    if (tr.GetObject(target.HatchId, OpenMode.ForWrite) is Hatch hatch)
                    {
                        try { hatch.EvaluateHatch(true); }
                        catch (Exception ex)
                        {
                            // The boundary edit itself is still valid and committed; report the
                            // fill refresh as a note rather than failing the whole edit.
                            Utilities.MahodLogger.Info($"[polyline-edit] hatch re-evaluate failed: {ex.Message}");
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"כתיבת הפוליליין נכשלה: {ex.Message}";
                return false;
            }
        }

        private static void WriteLightweight(Transaction tr, PolylineTarget target)
        {
            var pl = (Polyline)tr.GetObject(target.Id, OpenMode.ForWrite);
            var shape = target.Shape;

            while (pl.NumberOfVertices > shape.Count)
                pl.RemoveVertexAt(pl.NumberOfVertices - 1);
            while (pl.NumberOfVertices < shape.Count)
                pl.AddVertexAt(pl.NumberOfVertices, new Point2d(0, 0), 0, 0, 0);

            bool writeWidths = target.HadWidths || shape.HasWidths;
            for (int i = 0; i < shape.Count; i++)
            {
                var v = shape.Vertices[i];
                pl.SetPointAt(i, new Point2d(v.X, v.Y));
                pl.SetBulgeAt(i, v.Bulge);
                if (writeWidths)
                {
                    pl.SetStartWidthAt(i, v.StartWidth);
                    pl.SetEndWidthAt(i, v.EndWidth);
                }
            }

            pl.Closed = shape.Closed;
        }

        private static void WriteHeavy2d(Transaction tr, PolylineTarget target)
        {
            var heavy = (Polyline2d)tr.GetObject(target.Id, OpenMode.ForWrite);
            var shape = target.Shape;

            var vertexIds = new List<ObjectId>();
            foreach (ObjectId vid in heavy)
            {
                if (tr.GetObject(vid, OpenMode.ForRead) is Vertex2d v &&
                    v.VertexType != Vertex2dType.SplineControlVertex)
                {
                    vertexIds.Add(vid);
                }
            }

            // Trim from the end …
            for (int i = vertexIds.Count - 1; i >= shape.Count; i--)
            {
                if (tr.GetObject(vertexIds[i], OpenMode.ForWrite) is Vertex2d v) v.Erase();
                vertexIds.RemoveAt(i);
            }

            // … grow at the end …
            while (vertexIds.Count < shape.Count)
            {
                var nv = new Vertex2d(new Point3d(0, 0, heavy.Elevation), 0, 0, 0, 0);
                heavy.AppendVertex(nv);
                tr.AddNewlyCreatedDBObject(nv, true);
                vertexIds.Add(nv.ObjectId);
            }

            // … then rewrite every position in order.
            bool writeWidths = target.HadWidths || shape.HasWidths;
            for (int i = 0; i < shape.Count; i++)
            {
                if (tr.GetObject(vertexIds[i], OpenMode.ForWrite) is not Vertex2d v) continue;
                var s = shape.Vertices[i];
                v.Position = new Point3d(s.X, s.Y, heavy.Elevation);
                v.Bulge = s.Bulge;
                if (writeWidths)
                {
                    v.StartWidth = s.StartWidth;
                    v.EndWidth = s.EndWidth;
                }
            }

            heavy.Closed = shape.Closed;
        }

        private static void WritePoly3d(Transaction tr, PolylineTarget target)
        {
            var p3 = (Polyline3d)tr.GetObject(target.Id, OpenMode.ForWrite);
            var shape = target.Shape;

            var vertexIds = new List<ObjectId>();
            foreach (ObjectId vid in p3)
            {
                if (tr.GetObject(vid, OpenMode.ForRead) is PolylineVertex3d v &&
                    v.VertexType != Vertex3dType.ControlVertex)
                {
                    vertexIds.Add(vid);
                }
            }

            for (int i = vertexIds.Count - 1; i >= shape.Count; i--)
            {
                if (tr.GetObject(vertexIds[i], OpenMode.ForWrite) is PolylineVertex3d v) v.Erase();
                vertexIds.RemoveAt(i);
            }

            while (vertexIds.Count < shape.Count)
            {
                var nv = new PolylineVertex3d(Point3d.Origin);
                p3.AppendVertex(nv);
                tr.AddNewlyCreatedDBObject(nv, true);
                vertexIds.Add(nv.ObjectId);
            }

            for (int i = 0; i < shape.Count; i++)
            {
                if (tr.GetObject(vertexIds[i], OpenMode.ForWrite) is not PolylineVertex3d v) continue;
                var s = shape.Vertices[i];
                v.Position = new Point3d(s.X, s.Y, s.Z);
            }

            p3.Closed = shape.Closed;
        }

        /// <summary>
        /// Rewrites a non-associative hatch's loops with the edited one in place.
        ///
        /// The Hatch API has no "replace this polyline loop" call: <c>InsertLoopAt</c> accepts only
        /// boundary object ids (verified against acdbmgd — it has no vertices overload at all), and
        /// the only vertex-level entry point is <c>AppendLoop(type, Point2dCollection,
        /// DoubleCollection)</c>. So every loop is read out, the edited one is swapped in, all loops
        /// are removed and then re-appended IN ORDER. That is safe here because
        /// <see cref="TryOpenHatch"/> already refused any hatch with a non-polyline loop, so all
        /// loops round-trip losslessly through vertices + bulges.
        /// </summary>
        private static void WriteHatchLoop(Transaction tr, PolylineTarget target)
        {
            var hatch = (Hatch)tr.GetObject(target.Id, OpenMode.ForWrite);
            var shape = target.Shape;

            var loops = new List<(HatchLoopTypes Type, Point2dCollection Vertices, DoubleCollection Bulges)>();
            for (int i = 0; i < hatch.NumberOfLoops; i++)
            {
                var type = hatch.LoopTypeAt(i) | HatchLoopTypes.Polyline;
                var vertices = new Point2dCollection();
                var bulges = new DoubleCollection();

                if (i == target.LoopIndex)
                {
                    foreach (var v in shape.Vertices)
                    {
                        vertices.Add(new Point2d(v.X, v.Y));
                        bulges.Add(v.Bulge);
                    }
                }
                else
                {
                    foreach (BulgeVertex bv in hatch.GetLoopAt(i).Polyline)
                    {
                        vertices.Add(bv.Vertex);
                        bulges.Add(bv.Bulge);
                    }
                }

                loops.Add((type, vertices, bulges));
            }

            while (hatch.NumberOfLoops > 0) hatch.RemoveLoopAt(0);
            foreach (var (type, vertices, bulges) in loops) hatch.AppendLoop(type, vertices, bulges);
            hatch.EvaluateHatch(true);
        }

        /// <summary>True when the entity's layer is locked (both originals check this first).</summary>
        public static bool IsLayerLocked(Transaction tr, Entity ent)
        {
            try
            {
                return tr.GetObject(ent.LayerId, OpenMode.ForRead) is LayerTableRecord ltr && ltr.IsLocked;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Every polyline-ish entity in model space, as (id, vertex count). Used by the tools that
        /// accept "every polyline on layer X" instead of a handle list.
        /// </summary>
        public static List<ObjectId> FindPolylines(Transaction tr, Database db, string? layerName = null)
        {
            var ids = new List<ObjectId>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                if (id.IsNull || id.IsErased) continue;
                var obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is not (Polyline or Polyline2d or Polyline3d)) continue;
                if (!string.IsNullOrEmpty(layerName) &&
                    obj is Entity ent &&
                    !ent.Layer.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ids.Add(id);
            }
            return ids;
        }
    }
}
