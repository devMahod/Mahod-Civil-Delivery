using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers
{
    /// <summary>
    /// Calculates summary statistics from extracted data.
    /// Only performs counts and basic statistics - compliance analysis is done by AI.
    /// </summary>
    public class SummaryAnalyzer : AnalyzerBase
    {
        public override string Name => "Summary";
        public override int Priority => 100;

        public override void Analyze(DrawingDataModel model)
        {
            // Alignment statistics
            CalculateAlignmentStats(model);

            // Profile statistics
            CalculateProfileStats(model);

            // Curve statistics
            CalculateCurveStats(model);

            // Layer statistics
            CalculateLayerStats(model);
        }

        private void CalculateAlignmentStats(DrawingDataModel model)
        {
            if (model.Alignments == null || model.Alignments.Count == 0)
                return;

            model.AlignmentCount = model.Alignments.Count;
            model.TotalAlignmentLength = Sanitize(model.Alignments.Sum(a => a.Length));

            var radii = model.Alignments
                .Where(a => a.MinRadius > 0)
                .Select(a => a.MinRadius)
                .ToList();

            model.MinimumRadius = radii.Count > 0 ? Sanitize(radii.Min()) : 0;

            // Count total curves and spirals
            model.TotalCurveCount = model.Alignments.Sum(a => a.CurveCount);
            model.TotalSpiralCount = model.Alignments.Sum(a => a.SpiralCount);
        }

        private void CalculateProfileStats(DrawingDataModel model)
        {
            if (model.Profiles == null || model.Profiles.Count == 0)
                return;

            model.ProfileCount = model.Profiles.Count;

            var allGrades = model.Profiles
                .SelectMany(p => p.Segments ?? new List<ProfileSegmentInfo>())
                .Where(s => s.SegmentType == "Tangent")
                .Select(s => Math.Abs(s.GradePercent))
                .ToList();

            model.MaxGradePercent = allGrades.Count > 0 ? Sanitize(allGrades.Max()) : 0;

            // Count vertical curves
            model.TotalVerticalCurveCount = model.Profiles
                .SelectMany(p => p.Segments ?? new List<ProfileSegmentInfo>())
                .Count(s => s.SegmentType == "VerticalCurve");
        }

        private void CalculateCurveStats(DrawingDataModel model)
        {
            if (model.AlignmentDetails == null || model.AlignmentDetails.Count == 0)
                return;

            // Collect all horizontal curves for statistics
            var allCurves = model.AlignmentDetails
                .SelectMany(a => a.HorizontalCurves ?? new List<HorizontalCurveData>())
                .ToList();

            // Include all arc types: Arc, SCS_Arc, etc.
            var arcs = allCurves
                .Where(c => c.Type == "Arc" || c.Type.EndsWith("_Arc") || c.Type.Contains("_Arc"))
                .Where(c => c.Radius > 0)
                .ToList();

            // Include all spiral types: Spiral, SCS_SpiralIn, SCS_SpiralOut, STS_SpiralIn, STS_SpiralOut, etc.
            var spirals = allCurves
                .Where(c => c.Type == "Spiral" || c.Type.Contains("Spiral"))
                .Where(c => c.Length > 0)
                .ToList();

            // Arc statistics
            if (arcs.Count > 0)
            {
                model.MinCurveRadius = Sanitize(arcs.Min(a => a.Radius));
                model.MaxCurveRadius = Sanitize(arcs.Max(a => a.Radius));
                model.TotalCurveLength = Sanitize(arcs.Sum(a => a.Length));
            }

            // Spiral statistics
            if (spirals.Count > 0)
            {
                model.MinSpiralLength = Sanitize(spirals.Min(s => s.Length));
                model.MaxSpiralLength = Sanitize(spirals.Max(s => s.Length));
                model.TotalSpiralLength = Sanitize(spirals.Sum(s => s.Length));
            }

            // Design speeds
            var speeds = model.AlignmentDetails
                .SelectMany(a => a.DesignSpeeds ?? new List<double>())
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            model.DesignSpeeds = speeds;
        }

        private void CalculateLayerStats(DrawingDataModel model)
        {
            if (model.Layers == null || model.Layers.Count == 0)
                return;

            model.LayerCount = model.Layers.Count;
            model.EmptyLayerCount = model.Layers.Count(l => l.EntityCount == 0);
            model.ActiveLayerCount = model.Layers.Count(l => l.EntityCount > 0 && !l.IsOff && !l.IsFrozen);
        }
    }
}
