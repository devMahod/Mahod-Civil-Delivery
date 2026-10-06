using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionOwnedStatePlanSourceContractTests
{
    private static string Source()
    {
        var pluginSrc = typeof(SectionOwnedStatePlanSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(pluginSrc, "CivilDelivery", "Sections",
            "Services", "SectionPlanService.cs"));
    }

    [Fact]
    public void UnchangedUsesTheExactOwnedPairAndLiveRegistryGate()
    {
        var source = Source();

        source.Should().Contain("sampleLine.GetSectionViewIds()")
            .And.Contain("parentSampleLineIdentity")
            .And.Contain("SectionAnnotationRegistry.ReadAnnotationContractEvidence(")
            .And.Contain("registry.Entries.Count(entry => entry.IsLive)")
            .And.Contain("SectionOwnedStateLogic.Evaluate(")
            .And.Contain("case SectionOwnedStateLogic.State.Complete:")
            .And.Contain("record.Action = PlanAction.Unchanged")
            .And.NotContain("owned[meta.LogicalKey] = meta",
                "dictionary overwrite hid duplicate owned roles and deleted linked views");
    }

    [Fact]
    public void PartialAndAmbiguousOwnedStateCannotBecomeUnchanged()
    {
        var source = Source();

        source.Should().Contain("case SectionOwnedStateLogic.State.Repairable:")
            .And.Contain("record.Action = PlanAction.Update")
            .And.Contain("SectionFindingCodes.OwnedStateIncomplete")
            .And.Contain("case SectionOwnedStateLogic.State.Conflict:")
            .And.Contain("record.Action = PlanAction.ReviewRequired")
            .And.Contain("record.Status = DeliveryStatus.ReviewRequired")
            .And.Contain("inventory.MarkUnreadable(alignmentName)");
    }
}
