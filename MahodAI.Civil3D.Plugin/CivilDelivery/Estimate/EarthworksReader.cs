using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// The topography part of the bill of quantities: finds the ground as it is and the design in the drawing, works out the
/// cut and fill between them (<see cref="Earthworks"/>) and reports how. Read only — the drawing is never changed.
/// <list type="bullet">
/// <item>Ground: a Civil 3D surface recognised as existing (EG, MK, TOPO, קיים…), else one built from the survey in the
/// drawing and its XREFs (spot heights, contours and breaklines on the Survey of Israel relief layers).</item>
/// <item>Design: a datum / subgrade surface (already at the subgrade), else a design or corridor surface (lowered by the
/// pavement structure under road surfaces), else a level platform — typed, or the level that balances cut and fill.</item>
/// </list>
/// </summary>
internal static class EarthworksReader
{
    internal sealed record SurfaceInfo(string Name, EarthRole Role, ObjectId Id, double MinX, double MinY, double MaxX, double MaxY, int Points)
    {
        public double PlanArea => (MaxX - MinX) * (MaxY - MinY);
        public string RoleName => Role switch { EarthRole.Existing => "קרקע קיימת", EarthRole.Design => "תכנון", EarthRole.Subgrade => "שתית", _ => "לא מזוהה" };
    }

    internal sealed class Result
    {
        public string? Missing { get; init; }
        public string Ground { get; init; } = "";
        public string Design { get; init; } = "";
        public bool Platform { get; init; }
        public double? Level { get; init; }
        public bool LevelIsBalanced { get; init; }
        public Earthworks.Grid? Grid { get; init; }
        public Earthworks.Volumes? Volumes { get; init; }
        public Earthworks.Balance? Balance { get; init; }
        public Earthworks.Topography? Topography { get; init; }
        public IReadOnlyList<Earthworks.Row> Rows { get; init; } = Array.Empty<Earthworks.Row>();
        public string Basis { get; init; } = "";
        public bool Defaults { get; init; }
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        public IReadOnlyList<SurfaceInfo> Surfaces { get; init; } = Array.Empty<SurfaceInfo>();
        public bool SurveyAvailable { get; init; }
        public double MetresPerUnit { get; init; } = 1;
        public TimeSpan Took { get; init; }
    }

    /// <summary>Every TIN surface of the drawing with what its name says it is.</summary>
    internal static IReadOnlyList<SurfaceInfo> Surfaces(Transaction tr)
    {
        var list = new List<SurfaceInfo>();
        CivilDocument? civil;
        try { civil = CivilApplication.ActiveDocument; } catch (System.Exception) { return list; }
        if (civil == null) return list;
        foreach (ObjectId id in civil.GetSurfaceIds())
        {
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not TinSurface s) continue;
                var p = s.GetGeneralProperties();
                if (p.NumberOfPoints < 3) continue;
                list.Add(new SurfaceInfo(s.Name, Earthworks.Classify(s.Name, s.Layer, s.Description ?? ""), id,
                    p.MinimumCoordinateX, p.MinimumCoordinateY, p.MaximumCoordinateX, p.MaximumCoordinateY, p.NumberOfPoints));
            }
            catch (System.Exception) { }   // an out-of-date or broken surface is not a source
        }
        return list;
    }

    /// <summary>The triangles of a Civil 3D surface inside the window (metres).</summary>
    private static TinModel Triangles(Transaction tr, ObjectId id, double f, (double X0, double Y0, double X1, double Y1)? window)
    {
        var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
        var tris = new List<(Xyz, Xyz, Xyz)>();
        foreach (TinSurfaceTriangle t in s.GetTriangles(false))
        {
            var a = t.Vertex1.Location; var b = t.Vertex2.Location; var c = t.Vertex3.Location;
            if (window is { } w && (Math.Max(a.X, Math.Max(b.X, c.X)) < w.X0 || Math.Min(a.X, Math.Min(b.X, c.X)) > w.X1 ||
                                    Math.Max(a.Y, Math.Max(b.Y, c.Y)) < w.Y0 || Math.Min(a.Y, Math.Min(b.Y, c.Y)) > w.Y1)) continue;
            tris.Add((M(a, f), M(b, f), M(c, f)));
        }
        return new TinModel(tris);
    }

    private static Xyz M(Point3d p, double f) => new(p.X * f, p.Y * f, p.Z * f);

    // ───────────── survey ─────────────

    internal sealed record Survey(TinModel Tin, int Points, int Objects, SurveyGround.Cleaned Cleaning, IReadOnlyList<string> Layers);

    /// <summary>Spot heights, contours and breaklines on relief layers, in the drawing and its XREFs, inside the window.</summary>
    internal static Survey? ReadSurvey(Database db, Transaction tr, double f, (double X0, double Y0, double X1, double Y1)? window, Action<int>? tick, CancellationToken cancel)
    {
        var points = new List<Xyz>();
        var layers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = 0;
        bool In(Point3d p) => window is not { } w || (p.X >= w.X0 && p.X <= w.X1 && p.Y >= w.Y0 && p.Y <= w.Y1);
        void Walk(BlockTableRecord space, Matrix3d xf, int depth)
        {
            foreach (ObjectId id in space)
            {
                cancel.ThrowIfCancellationRequested();
                if (tr.GetObject(id, OpenMode.ForRead, false, true) is not Entity e) continue;
                if (e is BlockReference br && tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr && btr.IsFromExternalReference)
                {
                    if (depth < 3 && btr.IsResolved && !btr.IsUnloaded) Walk(btr, xf * br.BlockTransform, depth + 1);
                    continue;
                }
                if (!SurveyGround.IsReliefLayer(e.Layer)) continue;
                var before = points.Count;
                try { Collect(e, xf); } catch (System.Exception) { }
                if (points.Count > before) { objects++; layers.Add(e.Layer.Split('|').Last()); if ((objects & 255) == 0) tick?.Invoke(objects); }
            }
        }
        void Add(Point3d p, Matrix3d xf) { var w = p.TransformBy(xf); if (In(w)) points.Add(M(w, f)); }
        void Collect(Entity e, Matrix3d xf)
        {
            switch (e)
            {
                case DBPoint pt: Add(pt.Position, xf); break;
                case BlockReference b:
                    var z = b.Position.Z;
                    if (Math.Abs(z) < 1e-9 && Level(b) is { } level) z = level;
                    Add(new Point3d(b.Position.X, b.Position.Y, z), xf);
                    break;
                case Line l: foreach (var p in SurveyGround.Densify(new[] { X(l.StartPoint), X(l.EndPoint) })) Add(P(p), xf); break;
                case Polyline or Polyline2d or Polyline3d:
                    var c = (Curve)e;
                    var n = (int)Math.Round(c.EndParam);
                    var verts = new List<Xyz>(n + 1);
                    for (var i = 0; i <= n; i++) verts.Add(X(c.GetPointAtParameter(Math.Min(i, c.EndParam))));
                    foreach (var p in SurveyGround.Densify(verts)) Add(P(p), xf);
                    break;
            }
        }
        double? Level(BlockReference b)
        {
            foreach (ObjectId a in b.AttributeCollection)
            {
                if (tr.GetObject(a, OpenMode.ForRead) is not AttributeReference ar) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(ar.Tag, "HEIGHT|ELEV|LEVEL|GOVA|גובה|רום|^Z$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                if (double.TryParse(ar.TextString.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            }
            return null;
        }
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Walk((BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead), Matrix3d.Identity, 0);
        if (points.Count < 3) return null;
        var clean = SurveyGround.Clean(points);
        var tin = TinModel.FromPoints(clean.Points);
        return tin.Triangles == 0 ? null : new Survey(tin, clean.Points.Count, objects, clean, layers.ToArray());

        static Xyz X(Point3d p) => new(p.X, p.Y, p.Z);
        static Point3d P(Xyz p) => new(p.X, p.Y, p.Z);
    }

    // ───────────── the whole run ─────────────

    /// <summary>
    /// Finds the sources, samples the work area and prices nothing — the bill rows come back for the BOQ window to price.
    /// <paramref name="area"/> is the marked area (host units), null for the whole drawing; <paramref name="pavement"/>
    /// the road outlines (metres) with the depth of their structure.
    /// </summary>
    internal static Result Run(Document doc, SpatialQuantityClip? area, double f, Earthworks.Settings settings,
        IReadOnlyList<(IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings, double DepthM)> pavement,
        Action<string>? step, Action<int>? tick, CancellationToken cancel = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        (double, double, double, double)? window = null;
        IReadOnlyList<IReadOnlyList<(double X, double Y)>>? rings = null;
        if (area != null)
        {
            var env = area.Boundary.EnvelopeInternal;
            var margin = Math.Max(50 / f, 0.1 * Math.Max(env.Width, env.Height));   // room for side slopes and for the TIN around the edge
            window = (env.MinX - margin, env.MinY - margin, env.MaxX + margin, env.MaxY + margin);
            rings = new[] { area.Boundary.ExteriorRing }.Concat(area.Boundary.InteriorRings)
                .Select(r => (IReadOnlyList<(double X, double Y)>)r.Coordinates.Take(r.Coordinates.Length - 1).Select(c => (c.X * f, c.Y * f)).ToArray()).ToArray();
        }
        using var _ = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartTransaction();
        step?.Invoke("מחפש משטחי קרקע ותכנון");
        var surfaces = Surfaces(tr);
        bool Overlaps(SurfaceInfo s) => window is not { } w || !(s.MaxX < w.Item1 || s.MinX > w.Item3 || s.MaxY < w.Item2 || s.MinY > w.Item4);
        var usable = surfaces.Where(Overlaps).ToList();

        // Ground: chosen by name, else an existing-ground surface (the largest), else the one an unnamed pair leaves.
        SurfaceInfo? ground = settings.ExistingName is { } en ? usable.FirstOrDefault(s => s.Name == en) : null;
        ground ??= usable.Where(s => s.Role == EarthRole.Existing).OrderByDescending(s => s.Points).FirstOrDefault();
        var unknown = usable.Where(s => s.Role == EarthRole.Unknown).OrderByDescending(s => s.PlanArea).ToList();
        if (ground == null && unknown.Count > 0) ground = unknown[0];   // the widest unnamed surface is the survey; the design covers less
        // Design: chosen by name, else a subgrade, else a design surface, else (two unnamed) the smaller one.
        SurfaceInfo? design = null;
        if (!settings.ForcePlatform)
        {
            design = settings.DesignName is { } dn ? usable.FirstOrDefault(s => s.Name == dn && s != ground) : null;
            design ??= usable.Where(s => s != ground && s.Role == EarthRole.Subgrade).OrderByDescending(s => s.Points).FirstOrDefault();
            design ??= usable.Where(s => s != ground && s.Role == EarthRole.Design).OrderByDescending(s => s.Points).FirstOrDefault();
            design ??= unknown.FirstOrDefault(s => s != ground);
        }

        // Without a marked area the work area is the design's own footprint; with neither there is nothing to grade.
        if (window == null)
        {
            if (design == null)
                return new Result { Missing = "אין משטח תכנון בשרטוט — סמנו אזור, ויחושב מפלס פילוס מאוזן (או הזינו מפלס בהגדרות).",
                    Surfaces = surfaces, MetresPerUnit = f, Ground = ground != null ? $"משטח {ground.Name}" : "" };
            var m = 50 / f;
            window = (design.MinX - m, design.MinY - m, design.MaxX + m, design.MaxY + m);
        }

        TinModel groundTin; string groundName; var notes = new List<string>(); var surveyAvailable = false;
        if (ground != null)
        {
            step?.Invoke("קורא את משטח הקרקע הקיימת");
            groundTin = Triangles(tr, ground.Id, f, window);
            groundName = $"משטח {ground.Name}";
        }
        else
        {
            step?.Invoke("בונה קרקע קיימת מנקודות המדידה");
            var survey = ReadSurvey(doc.Database, tr, f, window, tick, cancel);
            if (survey == null)
                return new Result { Missing = "לא נמצאה קרקע קיימת: אין בשרטוט משטח Civil 3D, ואין נקודות גובה, קווי גובה או קווי שבר בשכבות המדידה (M1502, M1506/M1507, M1510…).",
                    Surfaces = surfaces, MetresPerUnit = f };
            surveyAvailable = true;
            groundTin = survey.Tin;
            groundName = $"מדידה — {survey.Points:N0} נקודות גובה מ־{survey.Objects:N0} עצמים · שכבות {string.Join(", ", survey.Layers.Take(6))}";
            var dropped = survey.Cleaning.Zero + survey.Cleaning.Implausible + survey.Cleaning.Spikes;
            if (dropped > 0)
                notes.Add($"נפסלו {dropped:N0} נקודות מדידה שגויות: {survey.Cleaning.Zero:N0} בגובה 0, {survey.Cleaning.Implausible:N0} בגובה בלתי אפשרי, {survey.Cleaning.Spikes:N0} חריגות מול שכנותיהן.");
        }
        if (groundTin.Triangles == 0)
            return new Result { Missing = "משטח הקרקע הקיימת אינו מגיע לאזור המסומן.", Surfaces = surfaces, MetresPerUnit = f, Ground = groundName };

        TinModel? designTin = null; string designName;
        var subgrade = design?.Role == EarthRole.Subgrade;
        if (design != null)
        {
            step?.Invoke("קורא את משטח התכנון");
            designTin = Triangles(tr, design.Id, f, window);
            if (designTin.Triangles == 0) designTin = null;
        }
        tr.Commit();
        if (designTin == null && rings == null)
            return new Result { Missing = "אין משטח תכנון בשרטוט — סמנו אזור, ויחושב מפלס פילוס מאוזן (או הזינו מפלס בהגדרות).",
                Surfaces = surfaces, MetresPerUnit = f, Ground = groundName, SurveyAvailable = surveyAvailable };

        step?.Invoke("מחשב חפירה ומילוי");
        var grid = Earthworks.Sample(groundTin, designTin, rings, subgrade ? null : pavement, settings, cancel: cancel);
        if (grid.GroundCells == 0)
            return new Result { Missing = "הקרקע הקיימת אינה מכסה את תחום העבודה.", Surfaces = surfaces, MetresPerUnit = f, Ground = groundName };
        double? level = null; var balanced = false;
        if (designTin == null)
        {
            if (settings.PlatformLevel is { } typed) level = typed;
            else { level = Earthworks.BalancedLevel(grid, settings); balanced = true; }
            designName = balanced ? $"פילוס — מפלס מאוזן {level:0.00} · הצעה: חפירה = מילוי" : $"פילוס — מפלס {level:0.00} שהוזן";
        }
        else designName = $"משטח {design!.Name}" + (subgrade ? " · שתית" : settings.SubtractPavement && pavement.Count > 0 ? " פחות מבנה המיסעה" : "");
        var volumes = Earthworks.Measure(grid, settings, level);
        var balance = Earthworks.Balanced(volumes, settings);
        var topo = Earthworks.Analyse(grid, volumes, level);
        var rows = Earthworks.Rows(balance, settings);

        var coverage = grid.InsideCells > 0 ? (double)grid.GroundCells / grid.InsideCells : 1;
        if (coverage < 0.97) notes.Add($"הקרקע הקיימת מכסה {coverage * 100:0}% מתחום העבודה — הכמויות לחלק המכוסה בלבד.");
        if (topo.SteepFillShare > 0.05) notes.Add($"{topo.SteepFillShare * 100:0}% מהמילוי על קרקע בשיפוע מעל 15% — נדרש מידור (מדרגות ברוחב 2 מ׳ לפחות) לפני המילוי.");
        if (topo.Terrain == "הררית" && settings.RockPercent == 0) notes.Add("שטח הררי — צפויה חציבה. לפי המפרטים החציבה כלולה במחיר החפירה; אם דו״ח הקרקע קובע סעיף חציבה נפרד, הזינו אחוז סלע בהגדרות.");
        if (designTin == null) notes.Add($"מדרונות הפילוס: 1:{settings.FillSlope:0.#} במילוי, 1:{settings.CutSlope:0.#} בחפירה — ערכים טיפוסיים ללא דו״ח קרקע.");
        if (!subgrade && designTin != null && settings.SubtractPavement && volumes.PavedArea > 0)
            notes.Add($"תחת {volumes.PavedArea:N0} מ״ר של מיסעה החישוב עד פני השתית (תכנון פחות מבנה המיסעה שבכתב הכמויות).");
        if (volumes.SamplingSpread > 0.02) notes.Add($"רגישות לרשת הדגימה {volumes.SamplingSpread * 100:0.0}% — לדיוק גבוה יותר סמנו אזור קטן יותר.");

        var basis = $"קרקע: {groundName} · תכנון: {designName} · רשת {grid.Cell:0.##} מ׳";
        return new Result
        {
            Ground = groundName, Design = designName, Platform = designTin == null, Level = level, LevelIsBalanced = balanced,
            Grid = grid, Volumes = volumes, Balance = balance, Topography = topo, Rows = rows, Basis = basis,
            Defaults = settings.FactorsAreDefaults || balanced, Notes = notes, Surfaces = surfaces, SurveyAvailable = surveyAvailable,
            MetresPerUnit = f, Took = clock.Elapsed,
        };
    }
}
