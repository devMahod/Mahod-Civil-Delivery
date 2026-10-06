using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Detached input values at the existing measured-layout solver boundary.
    /// Capture does not read native objects, infer anchors, solve layout or write files.
    /// Label IDs are the native handles. Join display metadata at the owner-side call.
    /// </summary>
    internal sealed record SectionMeasuredLayoutInputs(
        IReadOnlyList<SectionAnnotationPlacementLogic.LabelBox> TopLabels,
        IReadOnlyList<SectionAnnotationPlacementLogic.LabelBox> BottomLabels,
        IReadOnlyList<SectionAnnotationPlacementLogic.Bounds> FixedObstacles,
        double MedianTextHeight,
        double Clearance,
        double MaximumRise)
    {
        internal static SectionMeasuredLayoutInputs Capture(
            IReadOnlyList<SectionAnnotationPlacementLogic.LabelBox> topLabels,
            IReadOnlyList<SectionAnnotationPlacementLogic.LabelBox> bottomLabels,
            IReadOnlyList<SectionAnnotationPlacementLogic.Bounds> fixedObstacles,
            double medianTextHeight, double clearance, double maximumRise) =>
            new(
                Array.AsReadOnly(topLabels.ToArray()),
                Array.AsReadOnly(bottomLabels.ToArray()),
                Array.AsReadOnly(fixedObstacles.ToArray()),
                medianTextHeight, clearance, maximumRise);
    }
}
