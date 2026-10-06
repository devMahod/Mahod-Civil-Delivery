using System.IO;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSelectedRebuildTests
{
    private const string Id = "cl-7CA3";
    private static T Read<T>(string run, string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(
        Path.Combine(Live52RebuildTheoryAttribute.Runs, run, file)), SectionsWorkflowService.Json)!;
    private static SectionSelectedRebuildPolicy.Authority Actual()
    {
        var plan = Read<SectionPlan>(Live52RebuildTheoryAttribute.Plan, "section_plan.json");
        var failed = Read<SectionVerifyResult>(Live52RebuildTheoryAttribute.Verify, "verify_result.json");
        var producer = Read<SectionPlan>("sections-plan-20260907-142318-ee95fdb3", "section_plan.json");
        var applied = Read<SectionApplyResult>("sections-apply-selected-20260907-142545-ec60b399", "apply_result.json");
        return new(plan, failed, new(producer, applied));
    }

    [Live52RebuildTheory]
    [InlineData("actual", true)]
    [InlineData("stale", false)]
    [InlineData("unselected", false)]
    [InlineData("no-failed-verify", false)]
    [InlineData("ownership-failure", false)]
    [InlineData("record-not-ready", false)]
    [InlineData("different-revision", false)]
    public void ActualFailed77CheckRunAdmitsOnlyExplicitFreshSelectedRebuild(string change, bool accepted)
    {
        var authority = Actual();
        var plan = authority.Plan; var failed = authority.FailedVerification;
        var selected = plan.Records.Single(r => r.RecordId == Id);
        var originalPlanJson = JsonSerializer.Serialize(plan, SectionsWorkflowService.Json);
        if (change == "ownership-failure") failed.Records[0].Checks.Add(new SectionVerifyCheck
            { Check = "section_view_ownership", Expected = "owned", Actual = "foreign", Pass = false });
        if (change == "record-not-ready") selected.Status = DeliveryStatus.ReviewRequired;
        if (change == "different-revision") failed.VerifiedDatabaseRevision = "other-revision";
        Assert.Equal(accepted, SectionSelectedRebuildPolicy.Rejection(plan,
            change == "unselected" ? "cl-7D23" : Id,
            change == "no-failed-verify" ? null : failed, change != "stale") == null);
        if (!accepted) return;
        SectionSelectedRebuildPolicy.RequirePublished(authority);
        Assert.Equal(PlanAction.Unchanged, selected.Action);
        Assert.Equal(originalPlanJson, JsonSerializer.Serialize(plan, SectionsWorkflowService.Json));
    }

    [Live52RebuildTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactProducerOwnershipAdmitsAndForeignOwnerRejects(bool foreign)
    {
        var authority = Actual();
        var selected = authority.Plan.Records.Single(r => r.RecordId == Id);
        var applied = authority.Producer.Applied;
        var handles = applied.Records.Single(r => r.RecordId == Id).Handles;
        OwnershipMetadata Owner(string role) => new()
        {
            Feature = "sections", Role = role, ProjectProfileId = foreign ? "foreign-project" : authority.Plan.ProjectProfileId,
            RunId = applied.RunId, LogicalKey = selected.LogicalKey!, InputFingerprint = selected.InputFingerprint!,
            CreatedByToolVersion = "civil-delivery/1.2.48",
        };
        Assert.Equal(!foreign, SectionSelectedRebuildPolicy.OwnershipRejection(authority, authority.Plan, selected,
            Owner("section-view"), Owner("sample-line"), handles.SectionView!, handles.SampleLine!, handles.SampleLine!) == null);
        Assert.NotNull(SectionSelectedRebuildPolicy.OwnershipRejection(authority, authority.Plan, selected,
            Owner("section-view"), Owner("sample-line"), handles.SectionView!, handles.SampleLine!, "OTHER"));
    }

    [Live52RebuildTheory]
    [InlineData("mutated-verify")]
    public void ChangedPublishedFailureCannotAuthorizeRebuild(string _)
    {
        var authority = Actual();
        var previous = authority.FailedVerification.Records[0].Checks[0];
        authority.FailedVerification.Records[0].Checks[0] = new SectionVerifyCheck
        {
            Check = previous.Check, Expected = previous.Expected,
            Actual = previous.Actual + " changed", Pass = previous.Pass,
        };
        Assert.ThrowsAny<Exception>(() => SectionSelectedRebuildPolicy.RequirePublished(authority));
    }

    [Live52RebuildTheory]
    [InlineData("future-interval", true)]
    [InlineData("future-elevation", true)]
    [InlineData("future-incomplete", false)]
    [InlineData("future-missing-source", false)]
    [InlineData("future-stale-reference", false)]
    [InlineData("future-unreadable-reference", false)]
    [InlineData("legacy-missing-source", false)]
    [InlineData("legacy-stale-reference", false)]
    [InlineData("legacy-unreadable-reference", false)]
    [InlineData("legacy-incomplete", false)]
    [InlineData("legacy-interval-wrong-source", false)]
    [InlineData("legacy-interval-exception", false)]
    public void SourceIntegrityFailuresNeverAuthorizeRebuildAndTypedGeometryStillDoes(string change, bool accepted)
    {
        var authority = Actual();
        var checks = authority.FailedVerification.Records[0].Checks;
        var index = checks.FindIndex(c => !c.Pass && c.Check == "live_surface_cut_matches_section");
        var previous = checks[index];
        var kind = change == "future-interval" ? SectionVerificationRecoveryPolicy.SurfaceMismatchKind.CutIntervalMismatch :
            change == "future-elevation" ? SectionVerificationRecoveryPolicy.SurfaceMismatchKind.ElevationMismatch :
            SectionVerificationRecoveryPolicy.SurfaceMismatchKind.IncompleteOrAmbiguousChain;
        var check = change.StartsWith("legacy-", StringComparison.Ordinal) ? previous.Check :
            change is "future-interval" or "future-elevation" or "future-incomplete"
                ? SectionVerificationRecoveryService.SurfaceComparisonCheck(kind)
                : SectionVerificationRecoveryService.SurfaceSourceProofCheck;
        checks[index] = new SectionVerifyCheck
        {
            Check = check, Pass = false,
            Expected = change == "legacy-interval-wrong-source" ? "OTHER/DEAD: complete native source cut" :
                change == "legacy-interval-exception" ? "readable exact current surface cut" : previous.Expected,
            Actual = change.Contains("interval", StringComparison.Ordinal) ? previous.Actual :
                change.Contains("missing", StringComparison.Ordinal) ? "Surface references 'missing.dwg', which does not exist; the reference cannot be proven." :
                change.Contains("stale", StringComparison.Ordinal) ? "Surface is a stale data reference; synchronize it explicitly before verification." :
                change.Contains("unreadable", StringComparison.Ordinal) ? "Surface is a Civil data reference without a readable data-shortcut key." :
                change.Contains("incomplete", StringComparison.Ordinal) ? "Surface or Section chain is incomplete/ambiguous." :
                    "Surface/Section elevation mismatch at offset 1: source=11, section=10, delta(source-section)=1",
        };
        // Executable admission-policy replay; modified future-format artifacts are
        // not published and therefore cannot pass RequirePublished or execute APPLY.
        Assert.Equal(accepted, SectionSelectedRebuildPolicy.Rejection(authority.Plan, Id,
            authority.FailedVerification, true) == null);
        Assert.ThrowsAny<Exception>(() => SectionSelectedRebuildPolicy.RequirePublished(authority));
    }
}

public sealed class Live52RebuildTheoryAttribute : TheoryAttribute
{
    public static string Runs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "runs");
    public const string Plan = "sections-plan-20260909-075920-f5eec9bf";
    public const string Verify = "sections-verify-20260909-080132-8521cafa";
    public Live52RebuildTheoryAttribute()
    {
        if (!File.Exists(Path.Combine(Runs, Plan, "section_plan.json")) ||
            !File.Exists(Path.Combine(Runs, Verify, "verify_result.json")))
            Skip = "Actual local 1.2.52 PLAN/failed VERIFY unavailable; replay is not native replacement acceptance.";
    }
}
