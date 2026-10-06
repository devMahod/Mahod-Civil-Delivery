using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The corridor BoQ lane ("כמויות מקורידורים"): source wiring of the read-only collection, the 6422 project/rules/pricebook
    /// identity gate, freshness before collection / after collection / after the workbook and before publication, the typed
    /// measures file the combined bill reads, and the combined bill's use of the verified corridor run. Code shape and the
    /// guard helper only; no Autodesk member runs. Ported from Codex corridor-contracts (0D50DD4D, 30.09.2026).
    /// </summary>
    public sealed class CorridorBoqContractTests
    {
        private static string PluginRoot => typeof(CorridorBoqContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginRoot, "CivilDelivery" }.Concat(parts).ToArray()));

        private static string Service => Source("Estimate", "EstimateWorkflowService.CorridorBoq.cs");

        private static void Before(string text, string first, string second)
        {
            var a = text.IndexOf(first, StringComparison.Ordinal);
            var b = text.IndexOf(second, StringComparison.Ordinal);
            a.Should().BeGreaterThanOrEqualTo(0, first);
            b.Should().BeGreaterThan(a, $"{first} comes before {second}");
        }

        [Fact]
        public void CollectorOnlyOpensForReadAndDoesNotOwnATransaction()
        {
            var s = Source("Estimate", "CorridorBoqCollector.cs");
            foreach (var forbidden in new[] { "OpenMode.ForWrite", "UpgradeOpen(", "StartTransaction(", ".Commit(", ".Rebuild(", ".Erase(" })
                s.Should().NotContain(forbidden);
            var reads = Regex.Matches(s, @"tr\.GetObject\([^\r\n;]+").Cast<Match>().ToList();
            reads.Should().NotBeEmpty();
            reads.Should().OnlyContain(read => read.Value.Contains("OpenMode.ForRead"));
        }

        [Fact]
        public void ServiceLocksAbortsAndDoesNotWriteAProfile()
        {
            Service.Should().Contain("using (doc.LockDocument())").And.Contain("tr.Abort();").And.NotContain("tr.Commit(");
            foreach (var file in new[] { Service, Source("UI", "CivilDeliveryControl.CorridorBoq.cs") })
                foreach (var forbidden in new[] { "ProjectProfileWriter", "SaveApprovedMappings", "SaveFamilyDecisions", "SaveReviewedMappings" })
                    file.Should().NotContain(forbidden);
        }

        [Fact]
        public void SavedRevisionIsCheckedBeforeAndAfterCollectionAndAfterTheWorkbook()
        {
            Before(Service, "CorridorBoqExportGuard.RequireIdentity(profile.ProfileId, rules, rulesetSha256)", "CorridorBoqCollector.Collect(");
            Before(Service, "DrawingRevisionTracker.CaptureLive(doc)", "CorridorBoqCollector.Collect(");
            Before(Service, "InitialFailure(", "CorridorBoqCollector.Collect(");
            Before(Service, "CorridorBoqCollector.Collect(", "EstimateSourceSnapshotPolicy.FreshnessFailure(");
            Before(Service, "EstimateSourceSnapshotPolicy.FreshnessFailure(", "CorridorBoqExport.WriteWorkbook(");
            Before(Service, "CorridorBoqExport.WriteWorkbook(", "var publishSource = DrawingRevisionTracker.CaptureLive(doc)");
            Before(Service, "if (publishFailure != null)", "SectionsWorkflowService.PersistEvidenceBundle(");
            Service.Should().Contain("startSource.DatabaseRevision, publishSource.DrawingHash, publishSource.DatabaseRevision, publishSource.DbMod");
        }

        [Fact]
        public void ProjectGateIsAlsoVisibleAtTheAction()
        {
            Source("UI", "CivilDeliveryControl.CorridorBoq.cs").Should()
                .Contain("CorridorBoqExportGuard.SupportsEmbeddedRules(_profile?.ProfileId)")
                .And.Contain("ToolTipService.SetShowOnDisabled(BtnExportCorridorBoq, true)")
                .And.Contain("כללי פרויקט 6422");
        }

        [Fact]
        public void ReceiptPinsRulesPricebookAndPublishesTheTypedMeasuresFile()
        {
            Service.Should().Contain("RulesetSha256 = rulesetSha256")
                .And.Contain("PricebookId = rules.Pricebook.Id")
                .And.Contain("PricebookEdition = rules.Pricebook.Edition")
                .And.Contain("PricebookSourceSha256 = rules.Pricebook.SourceSha256")
                .And.Contain("CorridorBoqMeasuresFile.Serialize(export, context, rulesetSha256, profile.ProfileId)")
                .And.Contain("File.Copy(tempMeasures, Path.Combine(pendingRoot, runId, CorridorBoqMeasuresFile.FileName), overwrite: false);");
            // The measures file is staged with the other artifacts, so the manifest lists it with its SHA-256.
            Before(Service, "stageAdditionalArtifacts:", "File.Copy(tempMeasures,");
        }

        [Fact]
        public void PublicationRetainsDraftStatusAndFinalHashVerification()
        {
            Service.Should().Contain("DeliveryStatus.ReviewRequired : DeliveryStatus.Blocked")
                .And.Contain("ArtifactHash.Sha256OfFile(finalPath), xlsxHash")
                .And.NotContain("DeliveryStatus.Ready");
        }

        [Fact]
        public void TheCombinedBillPricesOnlyAVerifiedCorridorRunAndRechecksItBeforePublishing()
        {
            var boq = Source("Estimate", "EstimateWorkflowService.BoqRules.cs");
            boq.Should().Contain("BoqRulesSourceResolver.ResolveCorridor(SectionsWorkflowService.RunsRoot, scan.ProjectProfileId, corridorRules,")
                .And.Contain("BoqRulesWorkbookWriter.Write(result, tempPath, new BoqRulesWorkbookWriter.Context(DateTime.Now), corridor.Input)")
                // The corridor notes reach the engine's warnings, before the run, only when no measurement was taken (L05: via
                // the shared preparation's warningsBeforeRun).
                .And.Contain("var plan = PrepareBoqPlan(scan, rules, corridor.Input == null ? corridor.Notes : null);")
                .And.Contain("foreach (var (path, sha256) in corridor.Evidence) inputs.Add(new RunManifestInput(path, sha256));");
            Before(boq, "BoqRulesWorkbookWriter.Write(", "var corridorAfter = ResolveCorridor(rules, scan, selectedCorridorDrawing);");
            Before(boq, "var corridorAfter = ResolveCorridor(rules, scan, selectedCorridorDrawing);", "SectionsWorkflowService.PersistEvidenceBundle(");
            // The recheck runs whether or not a measurement was taken, and compares absence too (null -> input is a change).
            boq.Should().NotContain("if (corridor.Input != null && ResolveCorridor(")
                .And.Contain("corridorAfter.Input?.RunId != corridor.Input?.RunId")
                .And.Contain("!corridorAfter.Notes.SequenceEqual(corridor.Notes)");
            // Review 01/10: the corridor source is the chosen (or only) drawing, the same before and after the write; the
            // choice is evidence of the run; several measured drawings make the palette ask, and cancelling exports nothing.
            boq.Should().Contain("var corridor = ResolveCorridor(rules, scan, selectedCorridorDrawing);")
                .And.Contain("CorridorBoqMeasuresFile.EmbeddedRulesetSha256(), selectedDrawing);")
                .And.Contain("CorridorSourceSelected = selectedCorridorDrawing,")
                .And.Contain("BoqRulesSourceResolver.ListCorridorSources(SectionsWorkflowService.RunsRoot, scan.ProjectProfileId)");
            var ui = Source("UI", "CivilDeliveryControl.BoqRules.cs");
            ui.Should().Contain("if (corridorOptions.Count > 1)")
                .And.Contain("new CorridorSourcePickerDialog(corridorOptions)")
                .And.Contain("SetStatus(\"הייצוא בוטל — לא נבחרה מדידת קורידורים\");");
            Before(ui, "new CorridorSourcePickerDialog(corridorOptions)", "_boqRulesExporting = true;");
            // Live b10 review 01/10: the chapter range reads left-to-right inside the Hebrew sentence (it showed "51.04–51.01"),
            // each row is announced by its file name, and the result note says "chosen" once (the Core note already does).
            Source("UI", "CorridorSourcePickerDialog.xaml").Should().Contain("&#x200E;51.01–51.04&#x200E;");
            Source("UI", "CorridorSourcePickerDialog.xaml.cs").Should().Contain("public override string ToString() => Path.GetFileName(RawPath);");
            boq.Should().NotContain("(השרטוט נבחר בייצוא זה.)");
            Source("UI", "CivilDeliveryControl.BoqRules.cs").Should().Contain("{written.Corridor}");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("other-project")]
        [InlineData("6422-copy")]
        public void OtherProfilesCannotBorrow6422Prices(string? id)
        {
            var act = () => CorridorBoqExportGuard.RequireIdentity(id, CorridorBoqRuleset.LoadEmbedded6422(), new string('a', 64));
            act.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void TheGuardRejectsAnotherRuleProjectOrAMissingPricebookHash()
        {
            var rules = CorridorBoqRuleset.LoadEmbedded6422();
            CorridorBoqExportGuard.RequireIdentity("6422", rules, new string('a', 64));
            var otherProject = () => CorridorBoqExportGuard.RequireIdentity("6422", rules with { Project = "other" }, new string('a', 64));
            otherProject.Should().Throw<InvalidDataException>();
            var noPricebookHash = () => CorridorBoqExportGuard.RequireIdentity("6422", rules with { Pricebook = rules.Pricebook with { SourceSha256 = "" } }, new string('a', 64));
            noPricebookHash.Should().Throw<InvalidDataException>();
        }

        [Fact]
        public void TheRuleHashIsTheExactEmbeddedBytesAndMatchesTheMeasuresFileIdentity()
        {
            var rules = CorridorBoqExportGuard.LoadEmbeddedRules(out var hash);
            using var stream = typeof(CorridorBoqRuleset).Assembly.GetManifestResourceStream(CorridorBoqRuleset.EmbeddedResource6422)!;
            hash.Should().Be(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            hash.Should().Be(CorridorBoqMeasuresFile.EmbeddedRulesetSha256(), "the export and the combined bill name the same rules");
            CorridorBoqExportGuard.RequireIdentity("6422", rules, hash);
        }

        [Fact]
        public void ARevisionChangeIsNotHiddenByAnUnchangedSavedHash()
        {
            EstimateSourceSnapshotPolicy.FreshnessFailure(new string('a', 64), "r1", new string('a', 64), "r2", 0).Should().NotBeNull();
            EstimateSourceSnapshotPolicy.FreshnessFailure(new string('a', 64), "r1", new string('a', 64), "r1", 0).Should().BeNull();
        }
    }
}
