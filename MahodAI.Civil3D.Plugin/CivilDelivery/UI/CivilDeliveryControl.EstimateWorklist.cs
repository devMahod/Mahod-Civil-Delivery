using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void RefreshEstimateReviewOverview(bool sourceFresh)
    {
        // Index once, not one price-book or proposal scan for every quantity row.
        var descriptions = _proposals.GroupBy(proposal => proposal.RuleKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join("\n", group
                .OrderByDescending(proposal => proposal.Score)
                .DistinctBy(proposal => proposal.ProposedCode, StringComparer.OrdinalIgnoreCase)
                .Select(proposal => proposal.ProposedCode + " · " +
                    (_catalog != null && _catalog.Items.TryGetValue(proposal.ProposedCode, out var item)
                        ? item.Description : "הסעיף אינו זמין במחירון הפעיל"))), StringComparer.Ordinal);
        foreach (var row in _quantityRows)
        {
            row.ProposalSearchText = descriptions.GetValueOrDefault(row.RuleKey) ?? "";
            row.RefreshProposalDescription(_catalog);
        }
        var overview = EstimateReviewWorklist.Build(_quantityRows.ToList(),
            (IReadOnlyList<DeliveryFinding>?)_scan?.Findings ?? Array.Empty<DeliveryFinding>(), sourceFresh);
        EstimateReviewOverview.Text = overview.Text;
        EstimateReviewOverview.ToolTip = overview.Detail;
        EstimateReviewOverview.Visibility = overview.TotalGroups == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
