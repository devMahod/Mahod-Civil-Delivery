using System;
using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Tools.Creation.Routing;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// One polyline vertex, decoupled from AutoCAD so every vertex-editing algorithm in this
    /// namespace is unit-testable without a host (same reason <see cref="Pt2"/> exists).
    ///
    /// The width/bulge fields follow the LWPOLYLINE storage convention exactly: a vertex owns
    /// the data of the segment that STARTS at it. So <see cref="Bulge"/>, <see cref="StartWidth"/>
    /// and <see cref="EndWidth"/> on vertex i describe segment i → i+1, and the last vertex of an
    /// open polyline carries values nobody reads. Getting this wrong is what silently corrupts
    /// arcs and tapers on a reverse or a vertex delete.
    /// </summary>
    /// <param name="X">World X.</param>
    /// <param name="Y">World Y.</param>
    /// <param name="Z">Per-vertex elevation. Meaningful only for a 3D polyline; a 2D polyline
    /// keeps its single elevation on <see cref="PlShape.Elevation"/>.</param>
    /// <param name="Bulge">tan(Δ/4) of the segment starting here. 0 = straight, &gt;0 = counter-clockwise.</param>
    /// <param name="StartWidth">Width at this vertex for the segment starting here.</param>
    /// <param name="EndWidth">Width at the far end of the segment starting here.</param>
    public readonly record struct PlVertex(
        double X,
        double Y,
        double Z = 0.0,
        double Bulge = 0.0,
        double StartWidth = 0.0,
        double EndWidth = 0.0)
    {
        /// <summary>Planar location.</summary>
        public Pt2 P => new(X, Y);

        /// <summary>True when this vertex carries a non-zero width (a taper we must not destroy).</summary>
        public bool HasWidth => StartWidth != 0.0 || EndWidth != 0.0;

        public static PlVertex At(double x, double y) => new(x, y);

        public static PlVertex At(Pt2 p) => new(p.X, p.Y);

        public static PlVertex At(Pt2 p, double z) => new(p.X, p.Y, z);
    }

    /// <summary>
    /// An editable polyline: the vertex list plus the two flags that change how almost every
    /// operation behaves (closed → there is a segment from the last vertex back to the first;
    /// 3D → per-vertex Z and no bulges/widths at all, because a Polyline3d has none).
    /// </summary>
    public sealed class PlShape
    {
        public PlShape(IEnumerable<PlVertex>? vertices = null, bool closed = false, bool is3d = false, double elevation = 0.0)
        {
            Vertices = vertices != null ? new List<PlVertex>(vertices) : new List<PlVertex>();
            Closed = closed;
            Is3d = is3d;
            Elevation = elevation;
        }

        public List<PlVertex> Vertices { get; }

        /// <summary>Closed polylines have a segment from the last vertex back to vertex 0.</summary>
        public bool Closed { get; set; }

        /// <summary>Polyline3d: per-vertex Z, no bulge and no width.</summary>
        public bool Is3d { get; }

        /// <summary>Single elevation of a 2D polyline (ignored when <see cref="Is3d"/>).</summary>
        public double Elevation { get; set; }

        public int Count => Vertices.Count;

        /// <summary>Number of segments: one per vertex when closed, one less when open.</summary>
        public int SegmentCount => Count == 0 ? 0 : (Closed ? Count : Count - 1);

        /// <summary>True when any segment is an arc.</summary>
        public bool HasArcs
        {
            get
            {
                for (int i = 0; i < Count; i++)
                    if (Vertices[i].Bulge != 0.0) return true;
                return false;
            }
        }

        /// <summary>True when any vertex carries a width (variable-width polyline).</summary>
        public bool HasWidths
        {
            get
            {
                for (int i = 0; i < Count; i++)
                    if (Vertices[i].HasWidth) return true;
                return false;
            }
        }

        /// <summary>
        /// The minimum vertex count this shape may be reduced to: 2 for an open polyline
        /// (a single segment) and 3 for a closed one — below that AutoCAD has no polyline
        /// left, which is why both PLTOOLS and MAHOD-PL refuse the last deletions.
        /// </summary>
        public int MinVertices => Closed ? 3 : 2;

        /// <summary>Vertex index wrapped into range for a closed shape; clamped for an open one.</summary>
        public int NormalizeIndex(int index)
        {
            if (Count == 0) return 0;
            if (Closed)
            {
                int m = index % Count;
                return m < 0 ? m + Count : m;
            }
            return Math.Min(Math.Max(index, 0), Count - 1);
        }

        /// <summary>Start vertex of segment <paramref name="segmentIndex"/>.</summary>
        public PlVertex SegmentStart(int segmentIndex) => Vertices[NormalizeIndex(segmentIndex)];

        /// <summary>End vertex of segment <paramref name="segmentIndex"/> (wraps on a closed shape).</summary>
        public PlVertex SegmentEnd(int segmentIndex) => Vertices[NormalizeIndex(segmentIndex + 1)];

        /// <summary>Bulge of segment <paramref name="segmentIndex"/>; always 0 for a 3D polyline.</summary>
        public double SegmentBulge(int segmentIndex) =>
            Is3d ? 0.0 : Vertices[NormalizeIndex(segmentIndex)].Bulge;

        /// <summary>Planar length of one segment (arc length when it is an arc).</summary>
        public double SegmentLength(int segmentIndex) => BulgeMath.SegmentLength(
            SegmentStart(segmentIndex).P, SegmentEnd(segmentIndex).P, SegmentBulge(segmentIndex));

        /// <summary>Total planar length.</summary>
        public double TotalLength()
        {
            double sum = 0.0;
            for (int i = 0; i < SegmentCount; i++) sum += SegmentLength(i);
            return sum;
        }

        public PlShape Clone() => new(Vertices, Closed, Is3d, Elevation);
    }

    /// <summary>
    /// What an edit actually did. Every tool in this family reports before/after counts rather
    /// than a bare "ok" — a cleanup that removed nothing must not read as a cleanup that worked
    /// (the honesty contract the visual scan established).
    /// </summary>
    public sealed class PlEditResult
    {
        public int VerticesBefore { get; set; }
        public int VerticesAfter { get; set; }
        public int Removed { get; set; }
        public int Added { get; set; }
        /// <summary>
        /// True when the drawing was actually modified. Counts cover the add/remove cases;
        /// <see cref="ChangedFlag"/> covers edits that keep the vertex count (a move, a reverse,
        /// a width or bulge change).
        /// </summary>
        public bool Changed => Removed > 0 || Added > 0 || ChangedFlag;

        /// <summary>Set by count-preserving edits (move, reverse, set-start, width, bulge).</summary>
        public bool ChangedFlag { get; set; }
        public List<string> Notes { get; } = new();

        public static PlEditResult NoChange(int count, string? note = null)
        {
            var r = new PlEditResult { VerticesBefore = count, VerticesAfter = count };
            if (!string.IsNullOrEmpty(note)) r.Notes.Add(note!);
            return r;
        }
    }
}
