using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.CrossSection
{
    /// <summary>
    /// One planned change to a section. The planner never touches the drawing —
    /// it decides, and preview/accept carry it out. Both go through this class so
    /// what the engineer sees on MAHOD_FIX is exactly what accept applies.
    /// </summary>
    public sealed class SectionFixPlan
    {
        public string Number { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;

        // ── the chain sits at a ragged level and the whole block must drop or rise
        public bool MovesLevel { get; set; }
        public double LevelFrom { get; set; }
        public double LevelTo { get; set; }
        public double LevelDy { get; set; }

        // ── the kerb pair: two ticks ~0.5 m apart carrying two stacked heights
        public bool HasKerbPair { get; set; }
        public double KerbFirstX { get; set; }
        public double KerbSecondX { get; set; }
        public ObjectId KerbDistanceTextId { get; set; }   // the ".48" between them — deleted
        public ObjectId LowerHeightId { get; set; }        // the gutter gova — deleted
        public ObjectId SurvivingHeightId { get; set; }    // the kerb-top gova — moved left
        public double HeightMoveDx { get; set; }
        public ObjectId MergedSpanTextId { get; set; }     // the span that widens
        public string MergedSpanValue { get; set; } = string.Empty;
        public string MergedSpanWas { get; set; } = string.Empty;

        public List<ObjectId> OrphanStubs { get; } = new();
        public List<string> Notes { get; } = new();

        public bool IsEmpty => !MovesLevel && !HasKerbPair && OrphanStubs.Count == 0;
    }

    /// <summary>
    /// Decides what a section needs. Every rule here was derived from the
    /// engineer's own corrected section and verified against ten real frames.
    /// </summary>
    public static class CrossSectionFixPlanner
    {
        /// <summary>Widest gap that can still be a kerb. Real ones run 0.48–0.88 m.</summary>
        private const double MaxKerbWidth = 1.40;

        /// <summary>A level this close to whole is already correct.</summary>
        private const double LevelTolerance = 0.005;

        /// <summary>Label-to-tick nudge used throughout the sheets.</summary>
        private const double LabelOffset = 0.2;

        public static SectionFixPlan Plan(SectionModel s)
        {
            var plan = new SectionFixPlan { Number = s.Number, Title = s.Title };

            if (!s.IsResolved || s.Frame == null)
            {
                plan.Notes.Add("frame could not be decoded — nothing planned");
                return plan;
            }

            PlanLevel(s, plan);
            PlanKerbPair(s, plan);
            PlanOrphanStubs(s, plan);
            return plan;
        }

        /// <summary>
        /// The real defect. Every subgrade chain on a sound sheet sits on a whole
        /// elevation; a design chain at 29.450 is where a mouse let go. Moving it
        /// changes no number — the block travels together.
        /// </summary>
        private static void PlanLevel(SectionModel s, SectionFixPlan plan)
        {
            double current = s.ChainElevation;
            if (double.IsNaN(current)) return;

            double target = Math.Round(current, MidpointRounding.AwayFromZero);
            if (Math.Abs(current - target) <= LevelTolerance) return;

            plan.MovesLevel = true;
            plan.LevelFrom = current;
            plan.LevelTo = target;
            plan.LevelDy = s.Frame!.YFor(target) - s.ChainBaseY;
        }

        /// <summary>
        /// The kerb pair. Found at the START of the design line, never by scanning
        /// for any small gap — a section carries other sub-metre gaps (a 0.22 m
        /// step at the shoulder, for one) and treating those as kerbs would delete
        /// real points. Requiring two stacked heights also makes this
        /// self-disabling: once a section is fixed it stops matching.
        /// </summary>
        private static void PlanKerbPair(SectionModel s, SectionFixPlan plan)
        {
            var frame = s.Frame!;

            // first tick that carries a design vertex
            int first = -1;
            for (int i = 0; i < s.Ticks.Count; i++)
            {
                if (s.DesignVertices.Any(v => Math.Abs(v.X - s.Ticks[i]) < 0.02)) { first = i; break; }
            }
            if (first < 0 || first + 1 >= s.Ticks.Count) return;

            double t1 = s.Ticks[first], t2 = s.Ticks[first + 1];
            double gap = t2 - t1;
            if (gap <= 0 || gap > MaxKerbWidth) return;
            if (!s.DesignVertices.Any(v => Math.Abs(v.X - t2) < 0.02)) return;

            // two height labels bracketing the pair — the gutter and the kerb top
            var stacked = s.Heights
                .Where(h => h.X >= t1 - 0.05 && h.X <= t2 + LabelOffset + 0.05)
                .OrderBy(h => h.X)
                .ToList();
            if (stacked.Count < 2) return;

            var lower = stacked.OrderBy(h => h.Value).First();
            var upper = stacked.OrderByDescending(h => h.Value).First();
            if (lower.Id == upper.Id) return;

            // the small distance text lives between the two ticks
            var kerbText = s.Distances.FirstOrDefault(d => d.X >= t1 && d.X <= t2);
            if (kerbText == null) return;

            // the span that widens once the second tick goes
            double t3 = first + 2 < s.Ticks.Count ? s.Ticks[first + 2] : double.NaN;
            if (double.IsNaN(t3)) return;
            var mergedText = s.Distances.FirstOrDefault(d => d.X > t2 && d.X <= t3);
            if (mergedText == null) return;

            plan.HasKerbPair = true;
            plan.KerbFirstX = t1;
            plan.KerbSecondX = t2;
            plan.KerbDistanceTextId = kerbText.Id;
            plan.LowerHeightId = lower.Id;
            plan.SurvivingHeightId = upper.Id;
            plan.HeightMoveDx = -gap;
            plan.MergedSpanTextId = mergedText.Id;
            plan.MergedSpanWas = mergedText.Raw;
            plan.MergedSpanValue = SectionFrameMath.FormatDistance((t3 - t1) / frame.HorizontalScale);

            plan.Notes.Add(
                $"kerb {SectionFrameMath.FormatDistance(gap)} m: drop tick, delete {kerbText.Raw}, " +
                $"keep {upper.Raw} over {lower.Raw}, span {mergedText.Raw} → {plan.MergedSpanValue}");
        }

        /// <summary>
        /// Leader stubs whose text is gone. Matched by the SIDE the stub points —
        /// a stub pointing down belongs to a label written below the baseline.
        /// Matching on X alone lets a label above the line claim a downward stub,
        /// which leaves one orphan per section and deletes live ones elsewhere.
        /// </summary>
        private static void PlanOrphanStubs(SectionModel s, SectionFixPlan plan)
        {
            // callers resolve the stub geometry; the planner only records that the
            // pass should run, since a stub's fate depends on texts this plan deletes
            if (s.Stubs.Count > 0)
                plan.Notes.Add($"{s.Stubs.Count} leader stub(s) to re-check after deletions");
        }

        /// <summary>
        /// Zero-length and orphaned stubs, resolved against the texts that will
        /// SURVIVE the plan. Kept separate from <see cref="Plan"/> because it needs
        /// entity geometry the plan does not carry.
        /// </summary>
        public static List<ObjectId> ResolveOrphanStubs(
            Transaction tr,
            SectionModel s,
            SectionFixPlan plan,
            double maxDistance = 0.45)
        {
            var doomed = new HashSet<ObjectId>();
            if (!plan.KerbDistanceTextId.IsNull) doomed.Add(plan.KerbDistanceTextId);
            if (!plan.LowerHeightId.IsNull) doomed.Add(plan.LowerHeightId);

            // surviving texts, tagged by which side of the baseline they sit on
            var survivors = new List<(double X, bool Below)>();
            foreach (var t in s.Distances.Concat(s.Heights))
            {
                if (doomed.Contains(t.Id)) continue;
                double x = t.X;
                if (t.Id == plan.SurvivingHeightId) x += plan.HeightMoveDx;
                survivors.Add((x, t.Y < s.ChainBaseY));
            }

            var orphans = new List<ObjectId>();
            foreach (var id in s.Stubs)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;

                if (pl.Length < 1e-6) { orphans.Add(id); continue; }

                bool pointsDown = pl.EndPoint.Y < pl.StartPoint.Y - 1e-9;
                double nearest = double.MaxValue;
                foreach (var (x, below) in survivors)
                {
                    if (below != pointsDown) continue;
                    nearest = Math.Min(nearest, Math.Abs(x - pl.StartPoint.X));
                }
                if (nearest > maxDistance) orphans.Add(id);
            }
            return orphans;
        }
    }
}
