using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Source wiring checks, not execution of Autodesk transactions.</summary>
public sealed class SectionUnusedOfficeBlockRecoveryWiringTests
{
    private static string Source => File.ReadAllText(Path.Combine(
        typeof(SectionUnusedOfficeBlockRecoveryWiringTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!,
        "CivilDelivery", "Sections", "Services", "SectionVehicleBlockService.cs"));

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0);
        to.Should().BeGreaterThan(from);
        return source.Substring(from, to - from);
    }

    [Fact]
    public void RecoveryHasPerAssetSavepointAndCommitsOnlyAfterValidatedImport()
    {
        var branch = Between(Source, "if (!allowModify)",
            "if (!TargetHeaderMatches(existing, BlockScaling.Any))");
        branch.Should().Contain("if (selectedHeaderMatches && selectedAttestationMatches)")
            .And.Contain("return existingId;")
            .And.Contain("using var recoveryTr = targetDb.TransactionManager.StartTransaction();")
            .And.Contain("RetireUnusedSelectedDefinition(recoveryTr,")
            .And.Contain("ImportSourceDefinition(recoveryTr,")
            .And.Contain("recoveryTr.Commit();")
            .And.Contain("return recoveredId;");
        branch.IndexOf("recoveryTr.Commit();", StringComparison.Ordinal).Should()
            .BeGreaterThan(branch.IndexOf("ImportSourceDefinition(recoveryTr,", StringComparison.Ordinal));
        branch.Should().NotContain("existing.Comments =")
            .And.NotContain("NormalizeTargetHeader(")
            .And.NotContain("catch (");
    }

    [Fact]
    public void RetirementRequiresBothCompleteNativeProofsBeforeTheOnlyMutation()
    {
        var retire = Between(Source, "private static void RetireUnusedSelectedDefinition(",
            "private static ObjectId ImportSourceDefinition(");
        retire.Should().Contain("!existing.IsLayout && !existing.IsAnonymous")
            .And.Contain("!existing.IsDynamicBlock && !existing.IsFromExternalReference")
            .And.Contain("!existing.IsFromOverlayReference")
            .And.Contain("existing.GetBlockReferenceIds(false, true).Count")
            .And.Contain("new ObjectIdCollection(new[] { existingId })")
            .And.Contain("targetDb.Purge(candidates);")
            .And.Contain("candidates.Count == 1 && candidates[0] == existingId")
            .And.Contain("SectionUnusedOfficeBlockRecoveryPolicy.RetirementRejection")
            .And.Contain("RetirementNameAvailable: !blockTable.Has(retiredName)")
            .And.Contain("existing.Name = retiredName;");
        retire.IndexOf("existing.UpgradeOpen();", StringComparison.Ordinal).Should()
            .BeGreaterThan(retire.IndexOf("if (rejection != null)", StringComparison.Ordinal));
        retire.Should().NotContain(".Erase(")
            .And.NotContain(".Comments =")
            .And.NotContain("NormalizeTargetHeader(");
    }

    [Fact]
    public void RetirementRetainsContentAndIdentityAndNewImportKeepsOriginalAttestationChecks()
    {
        var source = Source;
        var retire = Between(source, "private static void RetireUnusedSelectedDefinition(",
            "private static ObjectId ImportSourceDefinition(");
        retire.Should().Contain("selected-old-retired")
            .And.Contain("existing.Comments, originalComments, StringComparison.Ordinal")
            .And.Contain("retiredFingerprint, beforeFingerprint, StringComparison.Ordinal")
            .And.Contain("blockTable[retiredName] != existingId");
        source.Should().Contain("selected-old-before");
        var import = Between(source, "private static ObjectId ImportSourceDefinition(",
            "private static byte[] ReadAndVerify(");
        import.Should().Contain("targetDb.Insert(asset.BlockName, sourceDb, preserveSourceDatabase: true)")
            .And.Contain("importedEntityFingerprint, sourceEntityFingerprint")
            .And.Contain("imported.Comments = Provenance(asset, importedFingerprint)")
            .And.Contain("imported-readback")
            .And.Contain("changed after its final attestation");
    }
}
