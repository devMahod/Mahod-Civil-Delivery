using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Tools.PolylineEdit
{
    /// <summary>
    /// PLTOOLS <c>PL-JOIN</c> / <c>PL-JOIN3D</c> / <c>PL-CSE</c>: chains separate polylines into
    /// longer ones by matching endpoints within a tolerance.
    ///
    /// AutoCAD's own <c>Curve.JoinEntity</c> demands exact coincidence, which is why the LISP
    /// original asks for a fuzz value — surveyed and hand-drawn linework misses by millimetres. The
    /// matching is pure list work, so it lives here with tests rather than inside a tool.
    ///
    /// A piece may be reversed to fit (bulges and widths carried by
    /// <see cref="PolylineOrientation.Reverse"/>), and a chain whose two ends meet comes out closed.
    /// </summary>
    public static class PolylineJoiner
    {
        /// <summary>One chained result and the inputs that went into it.</summary>
        public sealed class Chain
        {
            public required PlShape Shape { get; init; }

            /// <summary>Indices into the input list, in the order they were consumed.</summary>
            public required List<int> SourceIndices { get; init; }

            /// <summary>True when the chain's own ends met and it was closed.</summary>
            public bool ClosedUp { get; init; }
        }

        /// <summary>
        /// Chains the given shapes. Every input ends up in exactly one output chain — a piece that
        /// matches nothing comes back as a chain of its own, so nothing is silently dropped.
        /// </summary>
        public static List<Chain> Chain2d(IReadOnlyList<PlShape> shapes, double fuzz)
        {
            if (shapes == null) throw new ArgumentNullException(nameof(shapes));
            if (fuzz < 0.0) fuzz = 0.0;

            var used = new bool[shapes.Count];
            var chains = new List<Chain>();

            for (int seed = 0; seed < shapes.Count; seed++)
            {
                if (used[seed]) continue;
                if (shapes[seed].Count < 2)
                {
                    used[seed] = true;
                    continue;
                }

                used[seed] = true;
                var chain = shapes[seed].Clone();
                var members = new List<int> { seed };

                bool grew = true;
                while (grew && !chain.Closed)
                {
                    grew = false;
                    for (int i = 0; i < shapes.Count; i++)
                    {
                        if (used[i] || shapes[i].Count < 2) continue;
                        if (shapes[i].Is3d != chain.Is3d) continue;
                        if (shapes[i].Closed) continue;          // a closed loop has no free ends
                        if (chain.Closed) break;

                        var piece = shapes[i].Clone();
                        if (TryAttach(chain, piece, fuzz))
                        {
                            used[i] = true;
                            members.Add(i);
                            grew = true;
                        }
                    }
                }

                // Did the finished chain close on itself?
                bool closedUp = false;
                if (!chain.Closed && chain.Count > 2 &&
                    Near(chain.Vertices[0], chain.Vertices[^1], fuzz, chain.Is3d))
                {
                    chain.Vertices.RemoveAt(chain.Count - 1);
                    chain.Closed = true;
                    closedUp = true;
                }

                chains.Add(new Chain { Shape = chain, SourceIndices = members, ClosedUp = closedUp });
            }

            return chains;
        }

        /// <summary>
        /// Attaches <paramref name="piece"/> to either end of <paramref name="chain"/>, reversing it
        /// when that is what makes the ends meet. Returns false when nothing matches.
        /// </summary>
        private static bool TryAttach(PlShape chain, PlShape piece, double fuzz)
        {
            var chainStart = chain.Vertices[0];
            var chainEnd = chain.Vertices[^1];
            var pieceStart = piece.Vertices[0];
            var pieceEnd = piece.Vertices[^1];
            bool is3d = chain.Is3d;

            if (Near(chainEnd, pieceStart, fuzz, is3d))
            {
                AppendAtEnd(chain, piece);
                return true;
            }
            if (Near(chainEnd, pieceEnd, fuzz, is3d))
            {
                PolylineOrientation.Reverse(piece);
                AppendAtEnd(chain, piece);
                return true;
            }
            if (Near(chainStart, pieceEnd, fuzz, is3d))
            {
                PrependAtStart(chain, piece);
                return true;
            }
            if (Near(chainStart, pieceStart, fuzz, is3d))
            {
                PolylineOrientation.Reverse(piece);
                PrependAtStart(chain, piece);
                return true;
            }
            return false;
        }

        /// <summary>
        /// chain … chainEnd + pieceStart … pieceEnd. The chain's last vertex becomes the start of
        /// the piece's first segment, so it has to inherit that segment's bulge and widths — the
        /// step that turns a naive concatenation into a correct one.
        /// </summary>
        private static void AppendAtEnd(PlShape chain, PlShape piece)
        {
            var seam = piece.Vertices[0];
            chain.Vertices[^1] = chain.Vertices[^1] with
            {
                Bulge = seam.Bulge,
                StartWidth = seam.StartWidth,
                EndWidth = seam.EndWidth,
            };
            for (int i = 1; i < piece.Count; i++) chain.Vertices.Add(piece.Vertices[i]);
        }

        /// <summary>pieceStart … pieceEnd + chainStart … chain. The piece's last vertex is dropped.</summary>
        private static void PrependAtStart(PlShape chain, PlShape piece)
        {
            var head = new List<PlVertex>(piece.Vertices);
            var seam = head[^1];
            head.RemoveAt(head.Count - 1);

            // The chain's old start vertex keeps its own outgoing data but takes the seam's
            // position — they are the same point within fuzz, and the piece's copy is authoritative
            // for where the joint sits.
            chain.Vertices[0] = chain.Vertices[0] with { X = seam.X, Y = seam.Y, Z = seam.Z };
            chain.Vertices.InsertRange(0, head);
        }

        private static bool Near(PlVertex a, PlVertex b, double fuzz, bool use3d)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            double d2 = dx * dx + dy * dy;
            if (use3d)
            {
                double dz = a.Z - b.Z;
                d2 += dz * dz;
            }
            return d2 <= fuzz * fuzz;
        }

        /// <summary>Total vertex count across a chain list — for reporting.</summary>
        public static int TotalVertices(IEnumerable<Chain> chains) => chains.Sum(c => c.Shape.Count);
    }
}
