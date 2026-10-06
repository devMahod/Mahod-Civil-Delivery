using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Produces review-only strip-name suggestions from independently evidenced,
/// homologous spans.  It never turns a suggestion into an engineering decision:
/// the plug-in persists a label only after the engineer selects that exact row.
/// </summary>
public static class SectionSpanSuggestionLogic
{
    public const double MinimumWidthToleranceM = 0.35;
    public const double RelativeWidthTolerance = 0.08;

    public enum Confidence
    {
        None,
        Low,
        Medium,
        High,
        Conflict,
    }

    /// <summary>
    /// One complete width-chain span.  Resolved observations must carry the exact
    /// source that resolved them (source mark, traffic arrow, boundary rule or
    /// reviewed manual profile); unresolved observations are suggestion targets.
    /// </summary>
    public sealed record Observation(
        string RecordKey,
        string Alignment,
        double? Station,
        double From,
        double To,
        string LeftKind,
        string RightKind,
        bool IsResolved,
        string? Label,
        string? EvidenceSource,
        string? EvidenceDigest,
        bool HasConflictingLocalEvidence = false);

    public sealed record Suggestion(
        string RecordKey,
        double From,
        double To,
        string HomologyGroupKey,
        int HomologousSpanCount,
        string? Label,
        Confidence Confidence,
        int SupportingRecordCount,
        int ConflictingLabelCount,
        IReadOnlyList<string> EvidenceSources,
        IReadOnlyList<string> EvidenceRecords)
    {
        /// <summary>
        /// High confidence is still not auto-approved.  The UI may pre-select it for
        /// one-click review, but the user must save the checked row explicitly.
        /// </summary>
        public bool IsStrongReviewCandidate =>
            Confidence == Confidence.High && !string.IsNullOrWhiteSpace(Label);
    }

    private sealed record Positioned(Observation Value, string Side, int Rank)
    {
        public double Width => Value.To - Value.From;
    }

    public static IReadOnlyList<Suggestion> Suggest(
        IEnumerable<Observation> observations)
    {
        if (observations == null) throw new ArgumentNullException(nameof(observations));
        var valid = observations
            .Where(IsValid)
            .Select(value => value with
            {
                RecordKey = value.RecordKey.Trim(),
                Alignment = value.Alignment.Trim(),
                LeftKind = value.LeftKind.Trim().ToLowerInvariant(),
                RightKind = value.RightKind.Trim().ToLowerInvariant(),
                Label = value.Label?.Trim(),
                EvidenceSource = value.EvidenceSource?.Trim().ToLowerInvariant(),
                EvidenceDigest = value.EvidenceDigest?.Trim(),
            })
            .ToList();

        var positioned = new List<Positioned>();
        foreach (var record in valid.GroupBy(value => value.RecordKey, StringComparer.Ordinal))
        {
            AddSide("left", record.Where(value => value.To <= -0.05)
                .OrderByDescending(value => value.To).ThenByDescending(value => value.From));
            AddSide("center", record.Where(value => value.From < 0.05 && value.To > -0.05)
                .OrderBy(value => value.From).ThenBy(value => value.To));
            AddSide("right", record.Where(value => value.From >= 0.05)
                .OrderBy(value => value.From).ThenBy(value => value.To));

            void AddSide(string side, IEnumerable<Observation> values)
            {
                var rank = 0;
                foreach (var value in values)
                    positioned.Add(new Positioned(value, side, rank++));
            }
        }

        var results = new List<Suggestion>();
        foreach (var target in positioned.Where(item => !item.Value.IsResolved)
                     .OrderBy(item => item.Value.Alignment, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Value.Station ?? double.MaxValue)
                     .ThenBy(item => item.Value.From))
        {
            var widthTolerance = Math.Max(
                MinimumWidthToleranceM,
                Math.Abs(target.Width) * RelativeWidthTolerance);
            var homologous = positioned.Where(candidate =>
                    string.Equals(candidate.Value.Alignment, target.Value.Alignment,
                        StringComparison.OrdinalIgnoreCase) &&
                    candidate.Side == target.Side &&
                    candidate.Rank == target.Rank &&
                    string.Equals(candidate.Value.LeftKind, target.Value.LeftKind,
                        StringComparison.Ordinal) &&
                    string.Equals(candidate.Value.RightKind, target.Value.RightKind,
                        StringComparison.Ordinal) &&
                    Math.Abs(candidate.Width - target.Width) <= widthTolerance)
                .ToList();

            var evidence = homologous
                .Where(candidate => candidate.Value.IsResolved &&
                                    !string.IsNullOrWhiteSpace(candidate.Value.Label) &&
                                    IsReviewableEvidence(candidate.Value.EvidenceSource))
                // Several annotations in one section are still one independent
                // section observation, not several votes.
                .GroupBy(candidate => candidate.Value.RecordKey, StringComparer.Ordinal)
                .Select(group => group.OrderBy(candidate =>
                        EvidenceStrength(candidate.Value.EvidenceSource)).Last())
                .ToList();
            var labels = evidence.Select(candidate => candidate.Value.Label!)
                .Distinct(StringComparer.Ordinal).ToList();
            var sources = evidence.Select(candidate => candidate.Value.EvidenceSource!)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var evidenceRecords = evidence
                .OrderBy(candidate => candidate.Value.Station ?? double.MaxValue)
                .ThenBy(candidate => candidate.Value.RecordKey, StringComparer.Ordinal)
                .Select(candidate => EvidenceRecord(candidate.Value))
                .Take(6)
                .ToList();

            var confidence = Confidence.None;
            string? label = null;
            // A majority in other sections cannot resolve contradictory evidence
            // on this exact strip. Keep peer evidence visible for the review.
            if (target.Value.HasConflictingLocalEvidence || labels.Count > 1)
            {
                confidence = Confidence.Conflict;
            }
            else if (labels.Count == 1)
            {
                label = labels[0];
                var strong = evidence.Count(candidate =>
                    EvidenceStrength(candidate.Value.EvidenceSource) >= 2);
                confidence = strong >= 2
                    ? Confidence.High
                    : strong == 1 || evidence.Count >= 2
                        ? Confidence.Medium
                        : Confidence.Low;
            }

            // Homologous widths can straddle a vehicle-fit limit. A name that
            // cannot be saved for the target is not a usable review suggestion.
            if (label != null && !IsCredibleLabel(label, target.Width))
            {
                label = null;
                confidence = Confidence.None;
            }

            results.Add(new Suggestion(
                target.Value.RecordKey,
                target.Value.From,
                target.Value.To,
                GroupKey(target),
                homologous.Select(item => item.Value.RecordKey)
                    .Distinct(StringComparer.Ordinal).Count(),
                label,
                confidence,
                evidence.Count,
                labels.Count,
                sources,
                evidenceRecords));
        }
        return results;
    }

    public static bool IsCredibleLabel(string label, double widthM)
    {
        if (string.IsNullOrWhiteSpace(label) || !double.IsFinite(widthM) || widthM <= 0)
            return false;
        var vehicle = SectionFurnitureLogic.VehicleForStrip(label.Trim());
        return vehicle == null ||
            (SectionFurnitureLogic.FitsStrip(vehicle, widthM) &&
             widthM <= SectionFurnitureLogic.MaxSingleVehicleStripWidthM);
    }

    private static bool IsValid(Observation value) =>
        value != null &&
        !string.IsNullOrWhiteSpace(value.RecordKey) &&
        !string.IsNullOrWhiteSpace(value.Alignment) &&
        double.IsFinite(value.From) && double.IsFinite(value.To) &&
        value.To > value.From &&
        !string.IsNullOrWhiteSpace(value.LeftKind) &&
        !string.IsNullOrWhiteSpace(value.RightKind) &&
        (!value.IsResolved || (!string.IsNullOrWhiteSpace(value.Label) &&
                              IsReviewableEvidence(value.EvidenceSource)));

    private static bool IsReviewableEvidence(string? source) =>
        EvidenceStrength(source) > 0;

    private static int EvidenceStrength(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "source-mark" => 3,
            "source-hatch-region" => 3,
            "traffic-arrow" => 3,
            "boundary-rule" => 2,
            "manual-profile" => 1,
            _ => 0,
        };

    private static string GroupKey(Positioned target)
    {
        var bucket = Math.Round(target.Width * 2.0,
            MidpointRounding.AwayFromZero) / 2.0;
        return string.Join("|", new[]
        {
            target.Value.Alignment.ToUpperInvariant(),
            target.Side,
            target.Rank.ToString(CultureInfo.InvariantCulture),
            $"{target.Value.LeftKind}>{target.Value.RightKind}",
            $"w~{bucket.ToString("F1", CultureInfo.InvariantCulture)}",
        });
    }

    private static string EvidenceRecord(Observation value)
    {
        var station = value.Station?.ToString("F2", CultureInfo.InvariantCulture) ?? "?";
        var digest = string.IsNullOrWhiteSpace(value.EvidenceDigest)
            ? "no-digest"
            : value.EvidenceDigest!.Length <= 12
                ? value.EvidenceDigest
                : value.EvidenceDigest.Substring(0, 12);
        return $"{value.RecordKey}@{station}:{value.Label}:{value.EvidenceSource}:{digest}";
    }
}
