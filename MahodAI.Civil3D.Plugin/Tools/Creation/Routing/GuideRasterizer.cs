using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Builds a per-cell A* cost multiplier from discovered guide geometry:
    /// <list type="bullet">
    /// <item>Inside each <b>zone polygon</b>: <c>costMul = 1 - zoneStrength</c> (default 0.7).</item>
    /// <item>On/near each <b>guide centerline</b> (dilated by <c>guideBandM</c>): linear ramp from <c>1 - guideStrength</c> at the line (default 0.15) to 1.0 at the band edge.</item>
    /// </list>
    /// Combined via <c>min()</c> per cell so the strongest discount wins.
    /// </summary>
    public static class GuideRasterizer
    {
        public static double[,] Build(
            GuideDiscovery.DiscoveredGuides guides,
            bool[,] walkable, Envelope env, double cellSize,
            double guideBandM = 6.0,
            double guideStrength = 0.85,
            double zoneStrength = 0.30)
        {
            if (walkable == null) throw new ArgumentNullException(nameof(walkable));
            if (env == null) throw new ArgumentNullException(nameof(env));
            if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (guideBandM < 0) guideBandM = 0;
            if (guideStrength < 0) guideStrength = 0; if (guideStrength > 1) guideStrength = 1;
            if (zoneStrength < 0) zoneStrength = 0; if (zoneStrength > 1) zoneStrength = 1;

            int nx = walkable.GetLength(0);
            int ny = walkable.GetLength(1);
            var costMul = new double[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    costMul[x, y] = 1.0;

            // Zones: rasterise polygon interiors with a moderate flat discount.
            ApplyZones(guides.Zones, costMul, walkable, env, cellSize, nx, ny, zoneStrength);

            // Centerlines: rasterise lines + dilate with a linear ramp.
            ApplyCenterlines(guides.Centerlines, costMul, walkable, env, cellSize, nx, ny,
                             guideBandM, guideStrength);

            return costMul;
        }

        private static void ApplyZones(
            List<List<Pt2>> zones, double[,] costMul, bool[,] walkable,
            Envelope env, double cellSize, int nx, int ny, double zoneStrength)
        {
            if (zones.Count == 0 || zoneStrength <= 0) return;
            double zoneCost = 1.0 - zoneStrength;

            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();
            foreach (var loop in zones)
            {
                if (loop.Count < 3) continue;
                var coords = new Coordinate[loop.Count + 1];
                for (int i = 0; i < loop.Count; i++) coords[i] = new Coordinate(loop[i].X, loop[i].Y);
                coords[loop.Count] = new Coordinate(loop[0].X, loop[0].Y);

                Polygon poly;
                try { poly = factory.CreatePolygon(coords); }
                catch { continue; }
                if (!poly.IsValid) continue;
                var prepared = PreparedGeometryFactory.Prepare(poly);

                // Iterate cells in the polygon's AABB only.
                var pe = poly.EnvelopeInternal;
                int gxMin = Math.Max(0, (int)Math.Floor((pe.MinX - env.MinX) / cellSize));
                int gxMax = Math.Min(nx - 1, (int)Math.Floor((pe.MaxX - env.MinX) / cellSize));
                int gyMin = Math.Max(0, (int)Math.Floor((pe.MinY - env.MinY) / cellSize));
                int gyMax = Math.Min(ny - 1, (int)Math.Floor((pe.MaxY - env.MinY) / cellSize));

                for (int gx = gxMin; gx <= gxMax; gx++)
                {
                    double cx = env.MinX + (gx + 0.5) * cellSize;
                    for (int gy = gyMin; gy <= gyMax; gy++)
                    {
                        if (!walkable[gx, gy]) continue;
                        double cy = env.MinY + (gy + 0.5) * cellSize;
                        var pt = factory.CreatePoint(new Coordinate(cx, cy));
                        if (prepared.Contains(pt) && zoneCost < costMul[gx, gy])
                            costMul[gx, gy] = zoneCost;
                    }
                }
            }
        }

        private static void ApplyCenterlines(
            List<List<Pt2>> lines, double[,] costMul, bool[,] walkable,
            Envelope env, double cellSize, int nx, int ny,
            double bandM, double strength)
        {
            if (lines.Count == 0 || strength <= 0) return;
            double bandCells = Math.Max(1.0, bandM / cellSize);
            int bandCellsInt = (int)Math.Ceiling(bandCells);
            double minCost = 1.0 - strength;

            foreach (var poly in lines)
            {
                if (poly.Count < 2) continue;
                for (int i = 1; i < poly.Count; i++)
                {
                    StampSegment(costMul, walkable, env, cellSize, nx, ny,
                                 poly[i - 1], poly[i],
                                 bandCells, bandCellsInt, minCost);
                }
            }
        }

        // For a single segment [s..e]: walk every cell within bandCellsInt of
        // either endpoint's AABB, compute perpendicular distance to the
        // segment, and apply the cost ramp.
        private static void StampSegment(
            double[,] costMul, bool[,] walkable, Envelope env, double cellSize,
            int nx, int ny, Pt2 s, Pt2 e,
            double bandCells, int bandCellsInt, double minCost)
        {
            double minSegX = Math.Min(s.X, e.X) - bandCells * cellSize;
            double maxSegX = Math.Max(s.X, e.X) + bandCells * cellSize;
            double minSegY = Math.Min(s.Y, e.Y) - bandCells * cellSize;
            double maxSegY = Math.Max(s.Y, e.Y) + bandCells * cellSize;

            int gxMin = Math.Max(0, (int)Math.Floor((minSegX - env.MinX) / cellSize));
            int gxMax = Math.Min(nx - 1, (int)Math.Floor((maxSegX - env.MinX) / cellSize));
            int gyMin = Math.Max(0, (int)Math.Floor((minSegY - env.MinY) / cellSize));
            int gyMax = Math.Min(ny - 1, (int)Math.Floor((maxSegY - env.MinY) / cellSize));

            double dx = e.X - s.X, dy = e.Y - s.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-18) return;

            for (int gx = gxMin; gx <= gxMax; gx++)
            {
                double cx = env.MinX + (gx + 0.5) * cellSize;
                for (int gy = gyMin; gy <= gyMax; gy++)
                {
                    if (!walkable[gx, gy]) continue;
                    double cy = env.MinY + (gy + 0.5) * cellSize;
                    // Perpendicular distance from (cx,cy) to segment [s..e].
                    double t = ((cx - s.X) * dx + (cy - s.Y) * dy) / len2;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    double px = s.X + t * dx, py = s.Y + t * dy;
                    double ddx = cx - px, ddy = cy - py;
                    double dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                    double distCells = dist / cellSize;
                    if (distCells > bandCells) continue;
                    double rampCost = minCost + (1.0 - minCost) * (distCells / bandCells);
                    if (rampCost < costMul[gx, gy]) costMul[gx, gy] = rampCost;
                }
            }
        }

        /// <summary>
        /// Combine two cost fields via element-wise min. Returns the same
        /// dimensions; any null input is treated as the all-1.0 "no discount"
        /// field. Use this to layer guide+zone costs over the slope-based
        /// fallback.
        /// </summary>
        public static double[,]? CombineMin(double[,]? a, double[,]? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            int nx = a.GetLength(0), ny = a.GetLength(1);
            if (b.GetLength(0) != nx || b.GetLength(1) != ny) return a;
            var result = new double[nx, ny];
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    result[x, y] = Math.Min(a[x, y], b[x, y]);
            return result;
        }
    }
}
