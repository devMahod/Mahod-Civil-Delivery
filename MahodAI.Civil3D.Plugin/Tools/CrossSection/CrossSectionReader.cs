using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace MahodAI.Civil3D.Plugin.Tools.CrossSection
{
    /// <summary>
    /// Layer names of a cross-section sheet as the drafting team draws them.
    /// Collected here because every tool needs the same set and a typo in one
    /// place would silently read an empty section.
    /// </summary>
    public static class CrossSectionLayers
    {
        public const string Table = "HW-CS-TABL";        // grid box, elevation ruler, Offset/Elevation rows, titles
        public const string Centreline = "HW-CS-CL";     // offset zero
        public const string Existing = "HW-CS-EX";       // existing ground
        public const string DesignChain = "HW-CS-D";     // design dimension chain + its texts
        public const string DesignHeights = "HW-CS-ELEV";
        public const string SubgradeChain = "HW-CS-SBGR-D";
        public const string SubgradeHeights = "HW-CS-SBGR-ELEV";

        public static readonly string[] Design = { "HW-CS", "HW-CS2" };
        public static readonly string[] Subgrade = { "HW-CS-SBGR", "HW-CS-SBGR2" };

        /// <summary>Named feature markers — kerb, edge of asphalt, pipes.</summary>
        public static readonly string[] Markers = { "MB-TEMP-A", "MB-TEMP-A1", "קצה אספלט", "PIPE-60" };

        /// <summary>Working layer for previewed corrections. Nothing native is touched until accepted.</summary>
        public const string Preview = "MAHOD_FIX";
    }

    /// <summary>A numeric annotation text with the identity needed to edit or erase it.</summary>
    public sealed class SectionText
    {
        public ObjectId Id { get; set; }
        public string Raw { get; set; } = string.Empty;
        public double Value { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>One decoded cross-section frame and everything hanging off it.</summary>
    public sealed class SectionModel
    {
        public string Title { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public double Station { get; set; }

        public SectionFrameMath? Frame { get; set; }

        public ObjectId ChainId { get; set; }
        public double ChainBaseY { get; set; }
        public double TickHeight { get; set; } = 0.134;

        /// <summary>Tick ordinates in drawing X, deduped and ordered.</summary>
        public List<double> Ticks { get; } = new();

        public List<SectionText> Distances { get; } = new();
        public List<SectionText> Heights { get; } = new();

        /// <summary>Short leader polylines on the chain layer (label stubs).</summary>
        public List<ObjectId> Stubs { get; } = new();

        public List<Point2d> DesignVertices { get; } = new();

        /// <summary>Drawing X of each named marker in this frame.</summary>
        public List<double> MarkerX { get; } = new();

        public bool IsResolved => Frame != null && !ChainId.IsNull && Ticks.Count > 1;

        /// <summary>Chain level as an elevation — the number that is ragged on a broken sheet.</summary>
        public double ChainElevation => Frame == null ? double.NaN : Frame.ElevationAt(ChainBaseY);

        /// <summary>Spans between consecutive ticks, in metres.</summary>
        public List<double> Spans()
        {
            var spans = new List<double>();
            for (int i = 0; i < Ticks.Count - 1; i++)
                spans.Add((Ticks[i + 1] - Ticks[i]) / (Frame?.HorizontalScale ?? 1.0));
            return spans;
        }
    }

    /// <summary>
    /// Reads cross-section frames out of model space.
    ///
    /// A sheet holds several sections side by side, so everything is scoped by a
    /// window around each title. The title carries the number and station:
    /// "STG-A1 - 224 (4480.00)".
    /// </summary>
    public static class CrossSectionReader
    {
        // How far around a title to look. Frames sit ~115 m apart horizontally
        // and ~31 m vertically on the RD4 sheets; this window covers one frame
        // without reaching its neighbour.
        private const double WindowLeft = 50.0;
        private const double WindowRight = 60.0;
        private const double WindowBelow = 0.2;
        private const double WindowAbove = 28.0;

        public static List<SectionModel> ReadAll(Transaction tr, Database db)
        {
            var result = new List<SectionModel>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var ids = new List<ObjectId>();
            var titles = new List<(string Text, Point3d Pos)>();
            foreach (ObjectId id in ms)
            {
                ids.Add(id);
                if (tr.GetObject(id, OpenMode.ForRead) is DBText t && LooksLikeTitle(t.TextString))
                    titles.Add((t.TextString, t.Position));
            }

            foreach (var title in titles.OrderBy(t => t.Text, StringComparer.Ordinal))
                result.Add(ReadOne(tr, ids, title.Text, title.Pos));

            return result;
        }

        /// <summary>Reads the single frame whose window contains <paramref name="number"/>.</summary>
        public static SectionModel? ReadByNumber(Transaction tr, Database db, string number)
            => ReadAll(tr, db).FirstOrDefault(s => s.Number == number);

        /// <summary>"STG-A1 - 224 (4480.00)" — an alignment name, a number, a station in brackets.</summary>
        internal static bool LooksLikeTitle(string s)
            => !string.IsNullOrWhiteSpace(s)
               && s.Contains(" - ", StringComparison.Ordinal)
               && s.Contains('(')
               && s.TrimEnd().EndsWith(")", StringComparison.Ordinal);

        internal static (string Number, double Station) ParseTitle(string title)
        {
            string number = string.Empty;
            double station = double.NaN;

            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            int open = title.IndexOf('(');
            if (dash >= 0 && open > dash)
                number = title.Substring(dash + 3, open - dash - 3).Trim();

            int close = title.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                var raw = title.Substring(open + 1, close - open - 1).Trim();
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out station);
            }
            return (number, station);
        }

        private static SectionModel ReadOne(Transaction tr, List<ObjectId> ids, string title, Point3d titlePos)
        {
            var (number, station) = ParseTitle(title);
            var model = new SectionModel { Title = title, Number = number, Station = station };

            double tx = titlePos.X, ty = titlePos.Y;
            bool InFrame(Point3d p) =>
                p.X >= tx - WindowLeft && p.X <= tx + WindowRight &&
                p.Y >= ty + WindowBelow && p.Y <= ty + WindowAbove;

            double clX = double.NaN, gridTop = double.NaN;
            var ruler = new List<(double Elevation, double Y)>();
            var rawTicks = new List<double>();
            var pendingDistances = new List<SectionText>();
            var pendingHeights = new List<SectionText>();

            foreach (var id in ids)
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is not Entity ent) continue;
                string layer = ent.Layer;

                if (obj is Polyline pl && InFrame(pl.StartPoint))
                {
                    if (layer == CrossSectionLayers.Table && pl.NumberOfVertices == 4)
                    {
                        double minX = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                        for (int i = 0; i < 4; i++)
                        {
                            var v = pl.GetPoint2dAt(i);
                            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y);
                        }
                        // the wide box is the Offset/Elevation table; narrow ones are ruler ticks
                        if (maxX - minX > 50) gridTop = maxY;
                    }
                    else if (layer == CrossSectionLayers.Centreline)
                    {
                        clX = pl.StartPoint.X;
                    }
                    else if (layer == CrossSectionLayers.DesignChain)
                    {
                        if (pl.NumberOfVertices > 6)
                        {
                            model.ChainId = id;
                            model.ChainBaseY = pl.GetPoint2dAt(0).Y;
                            double top = model.ChainBaseY;
                            for (int i = 0; i < pl.NumberOfVertices; i++)
                            {
                                var v = pl.GetPoint2dAt(i);
                                top = Math.Max(top, v.Y);
                                if (Math.Abs(v.Y - model.ChainBaseY) < 0.01) rawTicks.Add(v.X);
                            }
                            model.TickHeight = top - model.ChainBaseY;
                        }
                        else
                        {
                            model.Stubs.Add(id);
                        }
                    }
                    else if (CrossSectionLayers.Design.Contains(layer))
                    {
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                            model.DesignVertices.Add(pl.GetPoint2dAt(i));
                    }
                    else if (CrossSectionLayers.Markers.Contains(layer) && pl.NumberOfVertices == 2)
                    {
                        model.MarkerX.Add(pl.StartPoint.X);
                    }
                }
                else if (obj is DBText txt && InFrame(txt.Position))
                {
                    if (!double.TryParse(txt.TextString.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        continue;

                    var item = new SectionText
                    {
                        Id = id,
                        Raw = txt.TextString,
                        Value = value,
                        X = txt.Position.X,
                        Y = txt.Position.Y
                    };

                    // the elevation ruler sits left of the frame, unrotated
                    if (layer == CrossSectionLayers.Table &&
                        txt.Rotation < 0.01 &&
                        txt.Position.X < tx - 40)
                    {
                        ruler.Add((value, txt.Position.Y));
                    }
                    else if (layer == CrossSectionLayers.DesignChain) pendingDistances.Add(item);
                    else if (layer == CrossSectionLayers.DesignHeights) pendingHeights.Add(item);
                }
            }

            if (!double.IsNaN(clX) && !double.IsNaN(gridTop) && ruler.Count >= 2)
            {
                try
                {
                    model.Frame = SectionFrameMath.FromRuler(clX, gridTop, ruler);
                }
                catch (ArgumentException)
                {
                    model.Frame = null;   // unreadable ruler — the audit reports it rather than guessing
                }
            }

            model.Ticks.AddRange(SectionFrameMath.Dedupe(rawTicks));
            model.Distances.AddRange(pendingDistances.OrderBy(t => t.X));
            model.Heights.AddRange(pendingHeights.OrderBy(t => t.X));
            model.DesignVertices.Sort((a, b) => a.X.CompareTo(b.X));
            model.MarkerX.Sort();

            return model;
        }
    }
}
