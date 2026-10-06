using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA.Pure
{
    /// <summary>One pair of texts whose bounding boxes collide on the sheet.</summary>
    public sealed class TextOverlapPair
    {
        public SheetTextRecord First { get; set; } = new();
        public SheetTextRecord Second { get; set; } = new();

        /// <summary>Intersection area as a fraction of the SMALLER box: 1.0 = fully buried.</summary>
        public double Ratio { get; set; }

        /// <summary>Bounding box enclosing both texts — what gets circled.</summary>
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    /// <summary>
    /// Finds text that collides with other text on a sheet.
    ///
    /// Overlap is measured against the SMALLER of the two boxes, so a short chainage buried
    /// under a long note scores ~1.0 and is reported, which a union-area or larger-box
    /// measure would dilute to nearly nothing. Boxes are axis-aligned; rotated text is
    /// therefore approximated by its enclosing box, which over-reports slightly for steeply
    /// rotated labels — deliberate, since a near miss on a dense utility sheet is still
    /// worth a drafter's eye.
    /// </summary>
    public static class TextOverlapDetector
    {
        /// <summary>An intersection below this share of the smaller box is normal kerning slack, not a collision.</summary>
        public const double DefaultMinOverlapRatio = 0.20;

        public static List<TextOverlapPair> Detect(
            IReadOnlyList<SheetTextRecord> texts,
            double minOverlapRatio = DefaultMinOverlapRatio)
        {
            var pairs = new List<TextOverlapPair>();
            if (texts == null || texts.Count < 2) return pairs;

            // Sweep on X: once a candidate starts to the right of the current box's right
            // edge, no later candidate can touch it either, so the scan stops early.
            var order = Enumerable.Range(0, texts.Count)
                .OrderBy(i => texts[i].MinX)
                .ToArray();

            for (int a = 0; a < order.Length; a++)
            {
                var first = texts[order[a]];
                for (int b = a + 1; b < order.Length; b++)
                {
                    var second = texts[order[b]];
                    if (second.MinX > first.MaxX) break;

                    double ix = Math.Min(first.MaxX, second.MaxX) - Math.Max(first.MinX, second.MinX);
                    double iy = Math.Min(first.MaxY, second.MaxY) - Math.Max(first.MinY, second.MinY);
                    if (ix <= 0 || iy <= 0) continue;

                    double smaller = Math.Min(first.Area, second.Area);
                    if (smaller <= 0) continue;

                    double ratio = (ix * iy) / smaller;
                    if (ratio < minOverlapRatio) continue;

                    pairs.Add(new TextOverlapPair
                    {
                        First = first,
                        Second = second,
                        Ratio = ratio,
                        MinX = Math.Min(first.MinX, second.MinX),
                        MinY = Math.Min(first.MinY, second.MinY),
                        MaxX = Math.Max(first.MaxX, second.MaxX),
                        MaxY = Math.Max(first.MaxY, second.MaxY)
                    });
                }
            }

            return pairs.OrderByDescending(p => p.Ratio).ToList();
        }

        /// <summary>Fully buried text is worse than a clipped corner; the report leads with the worst.</summary>
        public static string SeverityFor(double ratio) =>
            ratio >= 0.75 ? VisualFindingSeverities.High
            : ratio >= 0.40 ? VisualFindingSeverities.Medium
            : VisualFindingSeverities.Low;
    }
}
