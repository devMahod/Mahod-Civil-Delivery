using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>A new verification may consume old APPLY evidence, never its old green status.</summary>
public static class SectionVerificationRecoveryPolicy
{
    public sealed record Identity(string Drawing, string ProfileId, string ProfileHash,
        string RecordId, string LogicalKey, string InputContract);

    public static string? Rejection(Identity producer, Identity current,
        bool producerPublished, bool committed, bool recordApplied, bool uniquelyOwned)
    {
        if (!producerPublished) return "The original PLAN/APPLY publication is missing or changed.";
        if (!committed || !recordApplied) return "The original APPLY did not authoritatively commit this record.";
        if (!uniquelyOwned) return "The live section is not uniquely bound to its original APPLY run.";
        if (new[] { producer.Drawing, producer.ProfileId, producer.ProfileHash, producer.RecordId,
                producer.LogicalKey, producer.InputContract, current.Drawing, current.ProfileId,
                current.ProfileHash, current.RecordId, current.LogicalKey, current.InputContract }
            .Any(string.IsNullOrWhiteSpace)) return "Verification identity is incomplete.";
        if (!string.Equals(producer.Drawing, current.Drawing, StringComparison.OrdinalIgnoreCase))
            return "The drawing differs from the original APPLY drawing.";
        if (producer.ProfileId != current.ProfileId || producer.ProfileHash != current.ProfileHash)
            return "The approved project profile changed; update the section before verifying.";
        if (producer.RecordId != current.RecordId || producer.LogicalKey != current.LogicalKey)
            return "The selected record does not match the original APPLY identity.";
        if (producer.InputContract != current.InputContract)
            return "The section inputs changed; update the section before verifying.";
        return null;
    }

    public readonly record struct Sample(double Offset, double Elevation);

    public enum SurfaceMismatchKind
    {
        None, InvalidTolerance, IncompleteOrAmbiguousChain, CutIntervalMismatch, ElevationMismatch,
    }

    /// <summary>
    /// Compares entire piecewise-linear chains at the union of their breakpoints.
    /// Equal endpoints alone cannot hide an added terrain crest. No extrapolation,
    /// duplicate offsets, nonfinite values or different coverage are accepted.
    /// </summary>
    public static string? SurfaceMismatch(IReadOnlyList<Sample> source,
        IReadOnlyList<Sample> section, double tolerance = 0.005)
        => SurfaceMismatch(source, section, out _, tolerance);

    public static string? SurfaceMismatch(IReadOnlyList<Sample> source,
        IReadOnlyList<Sample> section, out SurfaceMismatchKind kind, double tolerance = 0.005)
    {
        kind = SurfaceMismatchKind.InvalidTolerance;
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            return FormattableString.Invariant($"Invalid tolerance: {tolerance:R}.");
        var coverage = $"source {DescribeChain(source)}; section {DescribeChain(section)}";
        kind = SurfaceMismatchKind.IncompleteOrAmbiguousChain;
        if (!Valid(source) || !Valid(section))
            return $"Surface or Section chain is incomplete/ambiguous; {coverage}; source-valid={Valid(source)}; section-valid={Valid(section)}.";
        kind = SurfaceMismatchKind.CutIntervalMismatch;
        if (Math.Abs(source[0].Offset - section[0].Offset) > 0.0005 ||
            Math.Abs(source[^1].Offset - section[^1].Offset) > 0.0005)
            return FormattableString.Invariant($"Surface and Section do not cover the same cut interval; {coverage}; delta(source-section)=[{source[0].Offset - section[0].Offset:R},{source[^1].Offset - section[^1].Offset:R}]; offset-tolerance=0.0005.");
        kind = SurfaceMismatchKind.ElevationMismatch;
        foreach (var offset in source.Select(p => p.Offset).Concat(section.Select(p => p.Offset)))
        {
            var a = At(source, offset); var b = At(section, offset);
            if (!a.HasValue || !b.HasValue || Math.Abs(a.Value - b.Value) > tolerance)
                return FormattableString.Invariant($"Surface/Section elevation mismatch at offset {offset:R}: source={Number(a)}, section={Number(b)}, delta(source-section)={Number(a.HasValue && b.HasValue ? a.Value - b.Value : null)}, elevation-tolerance={tolerance:R}; {coverage}; offset-tolerance=0.0005.");
        }
        kind = SurfaceMismatchKind.None;
        return null;
    }

    /// <summary>Diagnostic only: callers retain their original parsing, sampling and acceptance checks.</summary>
    public static string DescribeMeasuredSlopeFailure(
        SectionAnnotationContractLogic.SlopeAnnotationEvidence? stored,
        SectionFurnitureLogic.SlopeEvidence? live,
        IReadOnlyList<(double Offset, double Elevation)> design,
        string parseError)
    {
        const string tolerance = "elevation-tolerance=0.000005; percent-tolerance=0.000005 percentage-points";
        if (stored == null)
            return $"stored=unavailable; live=not evaluated; parse-error={parseError}; {tolerance}";
        var previous = FormattableString.Invariant($"handle={stored.Handle}; stored offsets=[{stored.FromOffset:R},{stored.ToOffset:R}], elevations=[{stored.FromElevation:R},{stored.ToElevation:R}], percent={stored.Percent:R}");
        if (live == null)
        {
            var chain = design.Select(p => new Sample(p.Offset, p.Elevation)).OrderBy(p => p.Offset).ToList();
            var cause = chain.Count < 2 ? "fewer than two design samples" :
                chain.Any(p => !double.IsFinite(p.Offset) || !double.IsFinite(p.Elevation)) ? "non-finite design sample" :
                stored.FromOffset < chain[0].Offset - 1e-9 || stored.ToOffset > chain[^1].Offset + 1e-9 ?
                    "requested endpoint outside design interval (sampling endpoint tolerance=1E-09)" :
                    "TrySlopeEvidence could not establish finite one-sided endpoint interpolation and grade";
            return $"{previous}; live=unavailable: {cause}; design {DescribeChain(chain)}; {tolerance}";
        }
        return FormattableString.Invariant($"{previous}; live offsets=[{live.FromOffset:R},{live.ToOffset:R}], elevations=[{live.FromElevation:R},{live.ToElevation:R}], percent={live.Percent:R}; delta(stored-live) offsets=[{stored.FromOffset - live.FromOffset:R},{stored.ToOffset - live.ToOffset:R}], elevations=[{stored.FromElevation - live.FromElevation:R},{stored.ToElevation - live.ToElevation:R}], percent={stored.Percent - live.Percent:R}; {tolerance}");
    }

    private static string Number(double? value) => value.HasValue
        ? value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "unavailable";

    private static string DescribeChain(IReadOnlyList<Sample> points) => points.Count == 0
        ? "count=0 interval=unavailable"
        : FormattableString.Invariant($"count={points.Count} interval=[{points[0].Offset:R},{points[^1].Offset:R}]");

    private static bool Valid(IReadOnlyList<Sample> points) => points.Count >= 2 &&
        points.All(p => double.IsFinite(p.Offset) && double.IsFinite(p.Elevation)) &&
        points.Zip(points.Skip(1), (a, b) => b.Offset - a.Offset > 1e-8).All(v => v);

    private static double? At(IReadOnlyList<Sample> points, double offset)
    {
        if (Math.Abs(offset - points[0].Offset) <= 0.0005) return points[0].Elevation;
        if (Math.Abs(offset - points[^1].Offset) <= 0.0005) return points[^1].Elevation;
        for (var i = 1; i < points.Count; i++)
            if (offset >= points[i - 1].Offset && offset <= points[i].Offset)
                return points[i - 1].Elevation + (points[i].Elevation - points[i - 1].Elevation) *
                    (offset - points[i - 1].Offset) / (points[i].Offset - points[i - 1].Offset);
        return null;
    }
}
