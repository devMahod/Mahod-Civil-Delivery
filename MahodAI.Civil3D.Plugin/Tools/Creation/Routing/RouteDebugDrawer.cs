using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using NetTopologySuite.Geometries;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Draws the router's INTERNAL view into the drawing on dedicated debug layers, so a human
    /// can SEE exactly what the search saw and decided — no inference required:
    ///   • MAHOD_DBG_MASK  (gray)  — the walkable-mask boundary (what the router treats as
    ///                               buildable; NOT necessarily the visible surface outline,
    ///                               because the sliver filter + cell rasterisation reshape it).
    ///   • MAHOD_DBG_RAW   (red)   — the raw A* path BEFORE simplification/curve fitting (the
    ///                               search decision itself).
    ///   • MAHOD_DBG_PTS   (green clicked / cyan snapped) — the start/end the user clicked vs.
    ///                               where they were snapped onto the mask.
    /// All entities go on their own layers so the engineer can freeze/delete them in one step.
    /// </summary>
    public static class RouteDebugDrawer
    {
        public const string MaskLayer = "MAHOD_DBG_MASK";
        public const string RawLayer = "MAHOD_DBG_RAW";
        public const string PtsLayer = "MAHOD_DBG_PTS";

        /// <summary>Cap on mask-boundary segments so a pathological mask can't flood the drawing.</summary>
        private const int MaxMaskSegments = 60000;

        public sealed class DebugStats
        {
            public int MaskSegments { get; init; }
            public bool MaskTruncated { get; init; }
            public int RawPoints { get; init; }
        }

        /// <summary>
        /// Draws the mask boundary, the raw path, and the clicked/snapped endpoints. Best-effort:
        /// any drawing failure is swallowed (debug aid must never fail the real operation).
        /// </summary>
        public static DebugStats Draw(
            Transaction tr,
            Database db,
            bool[,] walkable,
            Envelope env,
            double cellSize,
            GridRouter.RouteResult route,
            Pt2 clickedStart,
            Pt2 clickedEnd)
        {
            int maskSegments = 0;
            bool truncated = false;
            int rawPoints = 0;
            try
            {
                ObjectId maskLayer = EnsureLayer(tr, db, MaskLayer, 8);   // gray
                ObjectId rawLayer = EnsureLayer(tr, db, RawLayer, 1);     // red
                ObjectId ptsLayer = EnsureLayer(tr, db, PtsLayer, 3);     // green (clicked)

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // ── Mask boundary: every walkable cell edge that faces a non-walkable cell ──
                int nx = walkable.GetLength(0), ny = walkable.GetLength(1);
                for (int gx = 0; gx < nx && !truncated; gx++)
                {
                    double x0 = env.MinX + gx * cellSize;
                    double x1 = x0 + cellSize;
                    for (int gy = 0; gy < ny; gy++)
                    {
                        if (!walkable[gx, gy]) continue;
                        double y0 = env.MinY + gy * cellSize;
                        double y1 = y0 + cellSize;
                        // A face is on the boundary when the neighbour across it is off-mask.
                        if (!InMask(walkable, gx + 1, gy, nx, ny)) { AddLine(ms, tr, maskLayer, x1, y0, x1, y1); maskSegments++; }
                        if (!InMask(walkable, gx - 1, gy, nx, ny)) { AddLine(ms, tr, maskLayer, x0, y0, x0, y1); maskSegments++; }
                        if (!InMask(walkable, gx, gy + 1, nx, ny)) { AddLine(ms, tr, maskLayer, x0, y1, x1, y1); maskSegments++; }
                        if (!InMask(walkable, gx, gy - 1, nx, ny)) { AddLine(ms, tr, maskLayer, x0, y0, x1, y0); maskSegments++; }
                        if (maskSegments >= MaxMaskSegments) { truncated = true; break; }
                    }
                }

                // ── Raw A* path (pre-simplification) ──
                if (route.RawPath != null && route.RawPath.Length >= 2)
                {
                    var pl = new Polyline(route.RawPath.Length) { LayerId = rawLayer };
                    for (int i = 0; i < route.RawPath.Length; i++)
                        pl.AddVertexAt(i, new Point2d(route.RawPath[i].X, route.RawPath[i].Y), 0, 0, 0);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    rawPoints = route.RawPath.Length;
                }

                // ── Endpoints: clicked (green) vs snapped (cyan). Marker radius scales with cell. ──
                double r = Math.Max(cellSize * 1.5, 5.0);
                AddCircle(ms, tr, ptsLayer, clickedStart, r, 3);   // green clicked start
                AddCircle(ms, tr, ptsLayer, clickedEnd, r, 3);     // green clicked end
                if (route.StartSnap != null) AddCircle(ms, tr, ptsLayer, route.StartSnap.Point, r * 0.6, 4); // cyan snapped
                if (route.EndSnap != null) AddCircle(ms, tr, ptsLayer, route.EndSnap.Point, r * 0.6, 4);
            }
            catch
            {
                // Debug overlay is best-effort; never let it fail the routing operation.
            }

            return new DebugStats { MaskSegments = maskSegments, MaskTruncated = truncated, RawPoints = rawPoints };
        }

        private static bool InMask(bool[,] walkable, int x, int y, int nx, int ny)
            => x >= 0 && y >= 0 && x < nx && y < ny && walkable[x, y];

        private static void AddLine(BlockTableRecord ms, Transaction tr, ObjectId layer,
                                    double x0, double y0, double x1, double y1)
        {
            var ln = new Line(new Point3d(x0, y0, 0), new Point3d(x1, y1, 0)) { LayerId = layer };
            ms.AppendEntity(ln);
            tr.AddNewlyCreatedDBObject(ln, true);
        }

        private static void AddCircle(BlockTableRecord ms, Transaction tr, ObjectId layer,
                                      Pt2 c, double radius, short colorIndex)
        {
            var circle = new Circle(new Point3d(c.X, c.Y, 0), Vector3d.ZAxis, radius)
            {
                LayerId = layer,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
            };
            ms.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db, string name, short colorIndex)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
            };
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
