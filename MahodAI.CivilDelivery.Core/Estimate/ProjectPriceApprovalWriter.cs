using System;
using System.Collections.Generic;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

public static class ProjectPriceApprovalWriter
{
    public static ProjectProfileWriter.SaveResult Save(ProjectProfile profile, CatalogSnapshot catalog,
        ProjectPriceApprovalPolicy.Approval approval, string targetPath,
        ProjectProfileWriter.ExpectedProfileState expectedState)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ProjectPriceApprovalPolicy.RequireUnchanged(approval.Context, profile, catalog);
        var errors = ProjectPriceApprovalPolicy.Validate(approval.Price, approval.Source, approval.Reason,
            approval.ApprovedBy, approval.ApprovedAtUtc);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        ProjectProfileWriter.RequireExpectedStateUnchanged(profile, expectedState);
        var previous = profile.Estimate.ProjectOverrides.ToList();
        try
        {
            // The editor explicitly discloses every old decision for this exact code.
            // One approval replaces those competing decisions, never an unrelated item.
            profile.Estimate.ProjectOverrides.RemoveAll(value => string.Equals(value.ItemCode?.Trim(),
                approval.Context.ItemCode.Trim(), StringComparison.OrdinalIgnoreCase));
            profile.Estimate.ProjectOverrides.Add(new ProjectProfile.EstimateProfile.PriceOverride
            {
                ItemCode = approval.Context.ItemCode, Price = approval.Price, Source = approval.Source,
                Reason = approval.Reason, ApprovedBy = approval.ApprovedBy, ApprovedAtUtc = approval.ApprovedAtUtc,
                ApprovedCatalogId = approval.Context.CatalogId, ApprovedCatalogHash = approval.Context.CatalogHash,
                ApprovedCatalogItemFingerprint = approval.Context.ItemFingerprint, ExpectedUnit = approval.Context.Unit,
            });
            return ProjectProfileWriter.Save(profile, targetPath,
                "project price explicitly approved: " + approval.Context.ItemCode,
                approval.ApprovedBy, expectedState,
                new Dictionary<string, string> { [approval.Context.CatalogId] = approval.Context.CatalogHash });
        }
        catch
        {
            profile.Estimate.ProjectOverrides.Clear();
            profile.Estimate.ProjectOverrides.AddRange(previous);
            throw;
        }
    }
}
