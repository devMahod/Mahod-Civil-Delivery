using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;
using static MahodAI.CivilDelivery.Shared.SectionProjectionLogic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Named plan strips along one marker chord (typical sections, 2026-10): the evidence PLAN uses for a CL record,
    /// minus the record-bound parts (traffic arrows, saved span decisions). Plan marks crossing the chord become the
    /// width chain; names come from the edge-kind table, the hatch regions and explicit source marks. An unnamed strip
    /// stays unnamed — nothing is guessed, and the firm's own width stays as it was drawn.
    /// </summary>
    internal sealed class TypicalSectionStripResolver
    {
        internal sealed record StripSet(
            IReadOnlyList<(double FromOffsetM, double ToOffsetM, string Label)> Named,
            IReadOnlyList<UnresolvedSpan> Unresolved,
            int PlanMarkCount,
            string RowState,
            string? Failure);

        private readonly SectionGeometryCollector.CollectResult _collected;
        private readonly SectionPlanService.RowMarkSelection _rowPolicy;

        private TypicalSectionStripResolver(
            SectionGeometryCollector.CollectResult collected, SectionPlanService.RowMarkSelection rowPolicy)
        {
            _collected = collected;
            _rowPolicy = rowPolicy;
        }

        /// <summary>Collects the plan marks of the host and its XREFs once; every pick is then resolved from them.</summary>
        public static TypicalSectionStripResolver Collect(Transaction tr, Database db, ProjectProfile profile, StageLog? log)
        {
            var collected = SectionGeometryCollector.Collect(tr, db, profile, log);
            var rowPolicy = SectionPlanService.SelectAuthoritativeRowMarks(
                collected.PlanMarks, profile.Sections.Projection.RowAuthorities);
            return new TypicalSectionStripResolver(collected, rowPolicy);
        }

        public IReadOnlyList<DeliveryFinding> CollectionFindings => _collected.Findings;

        public int PlanMarkTotal => _collected.PlanMarks.Count;

        public StripSet Resolve(TypicalSectionPick pick)
        {
            if (!SectionCutFrame.TryCreate(
                    new P2(pick.AX, pick.AY), new P2(pick.BX, pick.BY),
                    new P2(pick.AxisX, pick.AxisY), pick.TangentDeg, out var frame) || frame == null)
                return new StripSet(Array.Empty<(double, double, string)>(), Array.Empty<UnresolvedSpan>(), 0,
                    _rowPolicy.Selection.State.ToString(), "קו הסימון אינו מגדיר חתך ישר דרך נקודת הציר");

            var merged = SectionCutGeometry.DimensionCrossingsFor(_rowPolicy.PlanMarks, frame);
            var marks = merged.Select(DimensionPresentationMark).ToList();
            var baseAnalysis = AnalyzePresentationCoverage(marks, frame.MinOffset, frame.MaxOffset);
            var overrides = SectionHatchSpanLabelService.Resolve(_collected.PlanRegions, merged, baseAnalysis);
            var analysis = AnalyzePresentationCoverage(
                marks, frame.MinOffset, frame.MaxOffset, approvedOverrides: overrides);
            return new StripSet(
                analysis.StripLabels.Select(strip => (strip.From, strip.To, strip.Label)).ToList(),
                analysis.UnresolvedSpans.ToList(),
                analysis.Summary.PlanMarkCount,
                _rowPolicy.Selection.State.ToString(),
                null);
        }
    }
}
