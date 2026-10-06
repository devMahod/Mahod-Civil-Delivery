using System;
using System.Globalization;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>Display only: never supplies a subset to measurement, approval, build or export.</summary>
internal static class QuantityReviewFilter
{
    internal enum Mode { All, Unmapped, Mapped, Ignored, History, Proposed, WithoutProposal, Measurement, DrawingNoise }
    internal static bool Matches(QuantityRowViewModel row, string? search, Mode mode)
    {
        var visible = mode switch
        {
            Mode.Unmapped => !row.IsIgnored && !row.IsHistorical && string.IsNullOrWhiteSpace(row.CatalogCode),
            Mode.Mapped => !row.IsIgnored && !row.IsHistorical && !string.IsNullOrWhiteSpace(row.CatalogCode),
            Mode.Ignored => row.IsIgnored,
            Mode.History => row.IsHistorical,
            Mode.Proposed => Pending(row) && !string.IsNullOrWhiteSpace(row.ProposedCode),
            Mode.WithoutProposal => Pending(row) && string.IsNullOrWhiteSpace(row.ProposedCode),
            // The group's own measurement, not the project-wide coverage that blocks every group alike.
            Mode.Measurement => !row.IsIgnored && !row.IsHistorical && row.Presentation is { } presentation &&
                (presentation.LocalMeasurementNeedsReview ||
                 presentation.MeasurementNeedsReview && presentation.ProjectCoverageBlockers == 0),
            Mode.DrawingNoise => row.IsBulkNoiseCandidate,
            _ => true,
        };
        if (!visible) return false;
        var tokens = Normalize(search).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;
        var text = Normalize(string.Join(" ", row.Layer, row.RuleKey, row.EntityType, row.Method,
            row.Unit, row.SourceCategory, row.CatalogCode, row.CatalogDescription, row.ProposedCode, row.ProposalSearchText, row.Findings));
        return tokens.All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
    private static bool Pending(QuantityRowViewModel row) => row.CanApproveCatalogMapping &&
        string.IsNullOrWhiteSpace(row.CatalogCode);
    private static string Normalize(string? value) => string.Concat((value ?? "")
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format));
}
