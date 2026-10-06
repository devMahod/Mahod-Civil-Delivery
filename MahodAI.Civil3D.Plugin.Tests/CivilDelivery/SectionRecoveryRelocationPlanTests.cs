using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Local immutable PLAN replay is not native ownership/publication/geometry acceptance.</summary>
public sealed class SectionRecoveryRelocationPlanTests
{
    private const string RecordId = "cl-7CA3";
    private const string Medva = "6422-SP-MEDVA-ALL-2026-MHD.dwg";
    private const string MedvaHash = "e8c396b44342fd594124880c8e6528e7e954f94c168f1f698cb8dd0433236b9a";

    [LocalRecoveryPlansTheory]
    [InlineData("unchanged", true)]
    [InlineData("hash", false)]
    [InlineData("missing", false)]
    [InlineData("extra", false)]
    [InlineData("live-revision", false)]
    [InlineData("requires-live", false)]
    [InlineData("geometry", false)]
    public void ActualProducerAndFreshPlanExerciseBothMandatoryRecoveryInputGates(string change, bool expected)
    {
        // Read only these local JSON artifacts; embedded P: paths are never opened.
        var producer = Read(LocalRecoveryPlansTheoryAttribute.ProducerPath);
        var fresh = JsonNode.Parse(File.ReadAllText(LocalRecoveryPlansTheoryAttribute.CurrentPath))!;
        var sources = fresh["external_sources"]!.AsArray();
        var medva = sources.Single(s => s!["source_name"]!.GetValue<string>() == Medva)!;
        Assert.Equal(MedvaHash, medva["sha256"]!.GetValue<string>());
        switch (change)
        {
            case "hash": medva["sha256"] = new string('b', 64); break;
            case "missing": sources.Remove(medva); break;
            case "extra": sources.Add(medva.DeepClone()); break;
            case "live-revision": medva["live_database_revision"] = "changed-live-db"; break;
            case "requires-live":
                medva["requires_live_database"] = true;
                medva["live_database_revision"] = "changed-live-db";
                break;
            case "geometry":
                var row = fresh["records"]!.AsArray().Single(r => r!["record_id"]!.GetValue<string>() == RecordId)!;
                row["station"] = row["station"]!.GetValue<double>() + 0.01;
                break;
        }
        var current = fresh.Deserialize<SectionPlan>(SectionsWorkflowService.Json)!;
        var oldRecord = producer.Records.Single(r => r.RecordId == RecordId);
        var currentRecord = current.Records.Single(r => r.RecordId == RecordId);
        var selectedReason = SectionVerificationRecoveryPolicy.Rejection(
            SectionVerificationRecoveryService.Identity(producer, oldRecord),
            SectionVerificationRecoveryService.Identity(current, currentRecord), true, true, true, true);
        var external = SectionVerificationRecoveryService.CompareExternalSources(producer, current);
        Assert.Equal(expected, selectedReason == null && external.Equivalent);
        if (change == "geometry")
        {
            Assert.NotNull(selectedReason);
            Assert.True(external.Equivalent, external.Reason);
        }
        else Assert.Null(selectedReason);
        if (!expected) return;

        Assert.Equal(PlanAction.Unchanged, currentRecord.Action);
        Assert.Equal(DeliveryStatus.Ready, currentRecord.Status);
        Assert.Equal("91a0ca7043af939e099efbd063ada190031c7ec44dd589d867aee8b629792de2", currentRecord.InputFingerprint);
        var relocation = Assert.Single(external.Relocations);
        Assert.Equal(MedvaHash, relocation.Sha256);
        Assert.Equal(@"P:\data\6422\Drawing\Background\SD\6422-SP-MEDVA-ALL-2026-MHD.dwg", relocation.OriginalPath);
        Assert.Equal(@"C:\Users\arthurf\MahodCivilDelivery_Work\6422-local-mirror\Civil3d\PD\6422-SP-MEDVA-ALL-2026-MHD.dwg", relocation.CurrentPath);
        // Actual HA reader role differences must not expand the exact-path contract.
        var oldHa = producer.ExternalSources.Single(s => s.SourceName.Contains("-HA-", StringComparison.Ordinal));
        var newHa = current.ExternalSources.Single(s => s.SourceName == oldHa.SourceName);
        Assert.Equal(oldHa.SourcePath, newHa.SourcePath);
        Assert.NotEqual(string.Join(",", oldHa.Roles), string.Join(",", newHa.Roles));
    }

    private static SectionPlan Read(string path) =>
        JsonSerializer.Deserialize<SectionPlan>(File.ReadAllText(path), SectionsWorkflowService.Json)!;
}

public sealed class LocalRecoveryPlansTheoryAttribute : TheoryAttribute
{
    private static string Runs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "runs");
    public static string ProducerPath => Path.Combine(Runs, "sections-plan-20260907-142318-ee95fdb3", "section_plan.json");
    public static string CurrentPath => Path.Combine(Runs, "sections-plan-20260909-072454-9a41dfe4", "section_plan.json");
    public LocalRecoveryPlansTheoryAttribute()
    {
        if (!File.Exists(ProducerPath) || !File.Exists(CurrentPath))
            Skip = "The actual local Sep 7 producer and Sep 9 relocated PLAN artifacts are unavailable; this replay is not native acceptance.";
    }
}
