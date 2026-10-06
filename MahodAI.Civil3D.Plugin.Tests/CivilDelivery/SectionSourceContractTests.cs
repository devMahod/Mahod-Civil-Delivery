using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Locks in the guarantee that a section actually CONTAINS what the plan promised.
    ///
    /// Creating sample lines and views is the easy half; a group with nothing flagged
    /// <c>IsSampled</c> produces perfectly valid, perfectly empty sections. These are
    /// source-level assertions because the behaviour only executes inside a Civil host —
    /// the live proof belongs to the GUI gate, but the wiring must not silently vanish
    /// in a future refactor.
    /// </summary>
    public class SectionSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string ServicesFile(string name) =>
            File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        [Fact]
        public void Apply_EnablesSectionSampling()
        {
            var apply = ServicesFile("SectionApplyService.cs");

            apply.Should().Contain("SectionSourceService.EnableSampling",
                "sample lines without sampled sources produce empty section views");
        }

        [Fact]
        public void Apply_RecordsWhatWasActuallySampled_NotWhatWasIntended()
        {
            var apply = ServicesFile("SectionApplyService.cs");

            apply.Should().Contain("SectionSourceService.SampledSourceNames",
                "the evidence must come from reading the group back, not from the plan");
        }

        [Fact]
        public void LegacyWildcardSampling_IsDisabledAndVerifyRejectsAnyRemainder()
        {
            var source = ServicesFile("SectionSourceService.cs");
            var apply = ServicesFile("SectionApplyService.cs");
            var verify = ServicesFile("SectionVerifyService.cs");

            source.Should().Contain("source.IsSampled = false",
                "an owned 1.2.x group may still have every wildcard-matched surface enabled");
            source.Should().Contain("exactGroupSourceNames")
                .And.Contain("Unexpected source")
                .And.Contain("List<string> Disabled");
            apply.Should().Contain("ExpectedGroupSampling")
                .And.Contain("recordResult.DisabledSources.AddRange(sourceOutcome.Disabled)",
                    "the apply artifact must name every legacy source it turned off");
            verify.Should().Contain("unexpected_sources_not_sampled")
                .And.Contain("unexpectedSources.Count == 0",
                    "presence of MK + design is insufficient while foreign surfaces remain sampled");
        }

        [Fact]
        public void Verify_ChecksUtilitiesByName()
        {
            var verify = ServicesFile("SectionVerifyService.cs");

            verify.Should().Contain("utilities_sampled_by_name",
                "a section count can be satisfied by the terrain surface alone while " +
                    "every utility is missing");
        }

        [Fact]
        public void Verify_GetSectionSourcesUsesWriteOpenAbortWithoutPersisting()
        {
            // Civil 3D 2027 fatally asserts eNotOpenForWrite when
            // SampleLineGroup.GetSectionSources is called on a ForRead group. VERIFY
            // therefore needs a narrowly scoped write-open transaction, but it must
            // abort that transaction because VERIFY is still a read-only operation.
            var source = ServicesFile("SectionSourceService.cs");
            var helperStart = source.IndexOf(
                "public static SampledSourceSnapshot SampledSourceNamesReadOnly", StringComparison.Ordinal);
            var helperEnd = source.IndexOf("private static string? ResolveName", helperStart,
                StringComparison.Ordinal);
            helperStart.Should().BeGreaterThan(-1);
            helperEnd.Should().BeGreaterThan(helperStart);
            var helper = source.Substring(helperStart, helperEnd - helperStart);

            helper.Should().Contain("StartTransaction()")
                .And.Contain("OpenMode.ForWrite")
                .And.Contain("ReadSampledSourcesStrict(sourceTr, group)")
                .And.Contain("sourceTr.Abort()")
                .And.NotContain("sourceTr.Commit()",
                    "Civil lazy updates discovered during VERIFY must be rolled back");

            var verify = ServicesFile("SectionVerifyService.cs");
            verify.Should().Contain("SampledSourceNamesReadOnly(db, slgId)")
                .And.NotContain("SampledSourceNames(tr, slg)",
                    "GetSectionSources must stay inside the write-open aborted helper");
        }

        [Fact]
        public void Verify_SnapshotsEachSharedGroupBeforeTheRecordLoopOpensItForRead()
        {
            // A second record commonly belongs to the same SampleLineGroup. Calling
            // the nested write-open helper from inside VerifyOne after the outer
            // transaction already opened that group ForRead can trigger Civil's
            // native eNotOpenForWrite termination instead of a managed exception.
            var verify = ServicesFile("SectionVerifyService.cs");
            var snapshotCall = verify.IndexOf(
                "var sampledSourcesByGroup = SnapshotSampledSourceNames", StringComparison.Ordinal);
            var recordLoop = verify.IndexOf(
                "foreach (var applyRecord in applied.Records.Where", StringComparison.Ordinal);
            var helperStart = verify.IndexOf(
                "private static IReadOnlyDictionary<ObjectId, SectionSourceService.SampledSourceSnapshot>",
                recordLoop, StringComparison.Ordinal);
            var verifyOneStart = verify.IndexOf(
                "private static void VerifyOne(", StringComparison.Ordinal);

            snapshotCall.Should().BeGreaterThan(-1);
            snapshotCall.Should().BeLessThan(recordLoop,
                "all native write-open readbacks must finish before any record opens its group ForRead");
            helperStart.Should().BeGreaterThan(recordLoop);
            verifyOneStart.Should().BeGreaterThan(helperStart);

            var helper = verify.Substring(helperStart, verifyOneStart - helperStart);
            helper.Should().Contain("var groupIds = new HashSet<ObjectId>()")
                .And.Contain("groupIds.Add(groupId)")
                .And.Contain("foreach (var slgId in groupIds)")
                .And.Contain("SampledSourceNamesReadOnly(db, slgId)");

            var verifyOne = verify.Substring(verifyOneStart);
            verifyOne.Should().Contain("sampledSourcesByGroup.TryGetValue")
                .And.NotContain("SampledSourceNamesReadOnly(",
                    "the per-record path must consume the pre-snapshot and never write-open a shared group again");
        }

        [Fact]
        public void Verify_SourceSnapshotIsStrictTypedAndNeverReturnsPartialNamesAsSuccess()
        {
            var source = ServicesFile("SectionSourceService.cs");
            var verify = ServicesFile("SectionVerifyService.cs");

            source.Should().Contain("public sealed record SampledSourceIdentity(")
                .And.Contain("public sealed record SampledSourceSnapshot(")
                .And.Contain("ReadSampledSourcesStrict")
                .And.Contain("source enumeration stopped before completion")
                .And.Contain("null or erased source object")
                .And.Contain("unsupported SectionSourceType")
                .And.Contain("Sampled source names are not unique")
                .And.Contain("SampledSourceSnapshot.Invalid(ex.Message)");
            verify.Should().Contain("group_sources_readable")
                .And.Contain("group_source_identity_and_cardinality_exact")
                .And.Contain("section_children_source_identity_exact")
                .And.Contain("CompareExpectedSourceIdentities(")
                .And.Contain("ExactSourceIdentitySet(")
                .And.Contain("ResolveLiveSourceIdentityStrict(")
                .And.Contain("sourceSnapshot.IsValid && missingGroupSources.Count == 0")
                .And.Contain("sourceSnapshot.IsValid && unexpectedSources.Count == 0");
        }

        [Fact]
        public void PaletteVerifyLocksDocumentAndAbortsItsReadBackTransaction()
        {
            var workflow = ServicesFile("SectionsWorkflowService.cs");
            var verifyStart = workflow.IndexOf(
                "public SectionVerifyResult Verify(", StringComparison.Ordinal);
            var verifyEnd = workflow.IndexOf(
                "// ------------------------------------------------------------- evidence",
                verifyStart, StringComparison.Ordinal);
            verifyStart.Should().BeGreaterThan(-1);
            verifyEnd.Should().BeGreaterThan(verifyStart);
            var verify = workflow.Substring(verifyStart, verifyEnd - verifyStart);

            verify.Should().Contain("using (doc.LockDocument())",
                    "the WPF palette runs modelessly outside a document command context")
                .And.Contain("tr.Abort()",
                    "VERIFY is read-only even when a Civil API requires write-open")
                .And.NotContain("tr.Commit()");
        }

        [Fact]
        public void Verify_RecomputesExactProjectedAnnotationSemanticsFromLiveEntities()
        {
            var decoration = ServicesFile("SectionDecorationService.cs");
            var registry = ServicesFile("SectionAnnotationRegistry.cs");
            var verify = ServicesFile("SectionVerifyService.cs");

            decoration.Should().Contain("SectionProjectionAnnotationSemantics.Fingerprint")
                .And.Contain("projectionFingerprints");
            registry.Should().Contain("ReadProjectionSemanticEvidence")
                .And.Contain("LiveFingerprint")
                .And.Contain("case Line line:")
                .And.Contain("case Circle circle:")
                .And.Contain("case DBText dbText:");
            verify.Should().Contain("projected_entity_live_semantics_exact")
                .And.Contain("projected_entity_registered_semantics_exact");
        }

        [Fact]
        public void SamplingIsCentralised_NoServiceSetsIsSampledDirectly()
        {
            // One place decides what gets sampled, so the finding codes and the evidence
            // trail cannot diverge between call sites.
            var dir = Path.Combine(PluginSourceDir, "CivilDelivery", "Sections", "Services");
            var offenders = Directory.GetFiles(dir, "*.cs")
                .Where(f => Path.GetFileName(f) != "SectionSourceService.cs")
                .Where(f => File.ReadAllText(f).Contains("IsSampled = true"))
                .Select(Path.GetFileName)
                .ToList();

            offenders.Should().BeEmpty(
                "sampling belongs to SectionSourceService alone");
        }

        [Fact]
        public void DiscoveryMode_IsBounded_SoPlanCannotFreezeCivil()
        {
            // 17,557 candidates x 22 alignments froze Civil for 13 minutes on the real
            // 6422 model (2026-08-19). Discovery mode must report, not brute-force.
            var plan = ServicesFile("SectionPlanService.cs");

            plan.Should().Contain("DiscoveryCandidateBound",
                "unbounded discovery on a real model is a freeze, not a feature");
            plan.Should().Contain("read.Records.Count > DiscoveryCandidateBound");
            plan.Should().Contain("Run Setup",
                "the finding must route the engineer to the step that fixes it");
        }

        [Fact]
        public void ExternalClDrawing_IsRead_ReadOnly()
        {
            // The CL instruction file is a separate drawing on 6422. It must be read as
            // a side database, never opened for edit or shown.
            var reader = ServicesFile("ClInstructionReader.cs");

            reader.Should().Contain("profile.Sections.Cl.SourceFiles",
                "the profile names the CL drawing; the reader must honour it");
            reader.Should().Contain("SideDwg.OpenReadOnly",
                "reads go through the sharing-tolerant path - the CL drawing may be open in a tab");
            reader.Should().NotContain("SaveAs(", "the CL drawing is never written");
        }

        [Fact]
        public void XrefFinding_DistinguishesUnloadedFromNotFound()
        {
            // An unloaded XREF is one click to fix; a missing one is a path problem.
            // Calling both "unresolved" sent the engineer hunting for a file that was
            // never missing (6422, 2026-08-19).
            var reader = ServicesFile("ClInstructionReader.cs");

            reader.Should().Contain("במצב UNLOADED בשרטוט", "the engineer reads the finding in Hebrew");
            reader.Should().Contain("לא נמצא — הגיאומטריה שלו");
            reader.Should().Contain("Reload", "the unloaded case must name the fix");
        }

        [Fact]
        public void SectionNames_AreUniqueByConstruction_AndAClashNeverAbortsTheBatch()
        {
            // 6422's CL.dwg carries the label "291.50" twice. Naming sections from the
            // label made Civil throw "Sample line name should not duplicate" on record
            // 25/27 and the atomic batch rolled back 24 good sections (2026-08-19).
            var apply = ServicesFile("SectionApplyService.cs");

            apply.Should().Contain("UniqueSampleLineName",
                "a name clash is a label problem, never an engineering failure");
            apply.Should().Contain("STA-{sta:F1}",
                "the station is the identity; the CL label is a readable suffix");
            apply.Should().Contain("Both sections were created",
                "a duplicate CL line is reported, not silently dropped and not fatal");
        }

        [Fact]
        public void Catalog_ResolvesTheRegisteredFilename_WithPinnedHashVerification()
        {
            // Price-book registration is the explicit migration boundary: once a book is
            // registered, both its file name and SHA-256 are part of its identity. A
            // same-hash workbook under another name must not silently replace it.
            var svc = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));

            svc.Should().Contain("LocateCatalog(profile.ProfileId, identity.CatalogFile")
                .And.Contain("configuredName = Path.GetFileName(fileName)")
                .And.Contain("HashOk(",
                    "the exact registered file only counts when its bytes match the pinned hash");

            // And the shipped file really is the pinned catalog.
            var repo = Path.GetFullPath(Path.Combine(PluginSourceDir, ".."));
            var profile = File.ReadAllText(Path.Combine(repo, "profiles", "civil-delivery", "6422", "project-profile.yaml"));
            var pinned = System.Text.RegularExpressions.Regex.Match(profile, "catalog_file_hash: \"([0-9a-f]{64})\"").Groups[1].Value;
            var shipped = Path.Combine(repo, "fixtures", "civil-delivery", "estimate", "nti-urban-082025.xlsx");
            using var sha = System.Security.Cryptography.SHA256.Create();
            var actual = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(shipped))).ToLowerInvariant();
            actual.Should().Be(pinned, "the installer ships exactly the catalog the profile pins");
        }

        [Fact]
        public void ProjectProfile_UsesTheObservedNatalySmGmLayerConventions()
        {
            var repo = Path.GetFullPath(Path.Combine(PluginSourceDir, ".."));
            var profile = File.ReadAllText(Path.Combine(
                repo, "profiles", "civil-delivery", "6422", "project-profile.yaml"));

            profile.Should().Contain("KAV_NETIVIM_NTZ")
                .And.Contain("*MIDRACHA*")
                .And.Contain("*PGVUL*")
                .And.Contain("6422-SM-MODEL-NATAZ.dwg")
                .And.Contain("6422-GM-MODEL-NATAZ.dwg");

            profile.IndexOf("- layer_pattern: \"*CURB-ILND*\"", StringComparison.Ordinal)
                .Should().BeLessThan(profile.IndexOf("- layer_pattern: \"*CURB*\"", StringComparison.Ordinal),
                    "an island kerb must not be consumed by the generic curb rule");
        }

        [Fact]
        public void EstimateScan_AlwaysDiscovers_RulesClassifyInsteadOfFilter()
        {
            // Approving the first mapping switched the scan to rules-only and 5,679
            // measured records vanished from the grid (6422, 2026-08-19). Unknown
            // mapping is not zero quantity - unmapped objects must stay visible.
            var svc = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Estimate", "CivilQuantityExtractionService.cs"));

            svc.Should().Contain("ResolveApprovedRule(profile, ruleKey",
                "discovery must classify by approved rule rather than hand off to a rules-only scan");
            svc.Should().Contain("approved != null ? DeliveryStatus.Ready : DeliveryStatus.ReviewRequired");
            svc.Should().Contain("return ExtractDiscovery(db, tr, profile, runId, result, drawingPath, drawingHash, log, savedHost);");
            svc.Should().NotContain("EstimatePreflightPolicy.RulesOnlySourceScopePolicy",
                "rules-only must never make supported host objects disappear");
        }

        [Fact]
        public void EstimateScanAndBuild_PersistTheRunManifestAfterTheirArtifacts()
        {
            var svc = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));

            svc.Should().Contain("SectionsWorkflowService.PersistEvidenceBundle(")
                .And.Contain("\"estimate_scan.json\"")
                .And.Contain("\"neutral_quantity_records.json\"")
                .And.Contain("\"quantity_preflight.json\"")
                .And.Contain("\"estimate_result.json\"")
                .And.Contain("(pendingRoot, publishedRoot) => WriteEstimateManifest(")
                .And.Contain("removeArtifacts: new[] { \"export_result.json\", \"partial_priced_export_result.json\" }")
                .And.Contain("RuntimeRunManifestService.Write(")
                .And.Contain("ManifestInputs(scan)")
                .And.Contain("scan.ProjectProfileHash");

            // Both the strict legacy entry and the palette's reviewed-line build
            // must reach the same artifact/manifest publication boundary.
            var strictStart = svc.IndexOf("public EstimateResult Build(", StringComparison.Ordinal);
            var reviewStart = svc.IndexOf("public EstimateResult BuildForReview(", strictStart, StringComparison.Ordinal);
            var reviewEnd = svc.IndexOf("internal sealed record IgnoredRuleApplication(", reviewStart, StringComparison.Ordinal);
            strictStart.Should().BeGreaterThan(0);
            reviewStart.Should().BeGreaterThan(strictStart);
            reviewEnd.Should().BeGreaterThan(reviewStart);
            svc[strictStart..reviewStart].Should().Contain("RequireFinalEstimateScope(profile)")
                .And.Contain("return BuildForReview(doc, scan, snapshot, profile)");
            var review = svc[reviewStart..reviewEnd];
            review.Should().Contain("RequirePublishedScanEvidence(scan)")
                .And.Contain("EstimateResultArtifact.From(result)")
                .And.Contain("(pendingRoot, publishedRoot) => WriteEstimateManifest(")
                .And.Contain("scan, \"build\", result.Status")
                .And.Contain("removeArtifacts: new[] { \"export_result.json\", \"partial_priced_export_result.json\" }");
            review.IndexOf("EstimateResultArtifact.From(result)", StringComparison.Ordinal)
                .Should().BeLessThan(review.IndexOf("(pendingRoot, publishedRoot) =>", StringComparison.Ordinal));
        }

        [Fact]
        public void EstimateRebase_ReplacesEveryCanonicalArtifactAndKeepsExplicitHistory()
        {
            var svc = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            var start = svc.IndexOf(
                "internal static void PublishRebasedScanEvidence(", StringComparison.Ordinal);
            var end = svc.IndexOf(
                "private static NeutralQuantityRecord WithApprovedClassification(",
                start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0);
            end.Should().BeGreaterThan(start);
            var body = svc[start..end];

            body.Should().Contain("\"neutral_quantity_records.json\", scan.Records")
                .And.Contain("\"quantity_preflight.json\", scan.Findings")
                .And.Contain("\"estimate_scan.before-profile-{profileVersion}.json.gz\"")
                .And.Contain("SectionsWorkflowService.WriteCompressedArtifact(")
                .And.Contain("previousScan")
                .And.Contain("removeArtifacts: DerivedEstimateArtifacts",
                    "rebase must invalidate every downstream artifact based on the previous classifications");
        }

        [Fact]
        public void EstimateManifestInputs_BindEachPathToItsOwnProducerHash()
        {
            var svc = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            var start = svc.IndexOf(
                "internal static IEnumerable<RunManifestInput> ManifestInputs(",
                StringComparison.Ordinal);
            var end = svc.IndexOf("public EstimateExcelWriter.WriteResult Export(",
                start, StringComparison.Ordinal);
            start.Should().BeGreaterThan(0);
            end.Should().BeGreaterThan(start);
            var body = svc[start..end];

            body.Should().Contain("new RunManifestInput(scan.SourceDrawing, scan.SourceDrawingHash)")
                .And.Contain("record.Source.DrawingPath ?? record.Source.Drawing")
                .And.Contain("record.Source.DrawingHash")
                .And.NotContain("record.Source.Drawing, record.Source.DrawingHash",
                    "a basename must never replace an available absolute producer path");
        }

        [Fact]
        public void SectionManifests_CoverDirectAndAiRoutesArtifactsAndSourceFiles()
        {
            var workflow = ServicesFile("SectionsWorkflowService.cs");
            var ai = File.ReadAllText(Path.Combine(
                PluginSourceDir, "Tools", "CivilDelivery", "SectionsTools.cs"));
            var shared = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Shared", "RuntimeRunManifestService.cs"));

            foreach (var method in new[]
                     { "WritePlanManifest(", "WriteApplyManifest(", "WriteVerifyManifest(" })
                workflow.Should().Contain(method, "the workflow owns every manifest schema");

            ai.Should().Contain("SectionsWorkflowService.WritePlanManifest(")
                .And.Contain("SectionsWorkflowService.PersistApplyEvidence(")
                .And.Contain("SectionsWorkflowService.PersistVerifyEvidence(");
            workflow.Should().Contain("(pendingRoot, publishedRoot) => WriteApplyManifest(")
                .And.Contain("(pendingRoot, publishedRoot) => WriteVerifyManifest(")
                .And.Contain("PersistEvidenceBundle(")
                .And.Contain("MoveEvidenceDirectory(pendingRun, finalRun")
                .And.Contain("move ??= Directory.Move;");

            workflow.Should().Contain("profile.Sections.Cl.SourceFiles")
                .And.Contain("cl.SourceXref")
                .And.Contain("cl.SourceDrawingHash")
                .And.Contain("record.ProjectedEntities")
                .And.Contain("projected.SourceXref")
                .And.Contain("RequirePlanEvidence(plan)")
                .And.Contain("RequireApplyEvidence(applied)")
                .And.Contain("new RunManifestArtifactInput(planProof.Path, planProof.Hash)")
                .And.Contain("section_plan.json")
                .And.Contain("apply_result.json");
            shared.Should().Contain("ExistingRunArtifacts(runId, runsRoot, publishedRunsRoot)")
                .And.Contain("RequirePublishedArtifact<T>(")
                .And.Contain("PluginRuntimeIdentityResolver.FromAssembly(")
                .And.Contain("PluginAssemblySha256 = pluginIdentity.AssemblySha256")
                .And.NotContain("TryFileHash(path)")
                .And.Contain("RunManifestWriter.Write(manifest, runsRoot)",
                    "the manifest must be written inside the same pending root as its result artifact");
            var artifactDiscovery = shared[
                shared.IndexOf("private static IEnumerable<ArtifactEvidence> ExistingRunArtifacts(",
                    StringComparison.Ordinal)..];
            artifactDiscovery = artifactDiscovery[..artifactDiscovery.IndexOf(
                "private static bool IsSha256(", StringComparison.Ordinal)];
            artifactDiscovery.Should().Contain("Directory.GetFiles(dir)")
                .And.NotContain("catch",
                    "unreadable artifact inventory must abort the complete evidence bundle, never publish []");
        }

        [Fact]
        public void Palette_ShowsAProjectDashboardBeforeAnyClick()
        {
            // The first thing the engineer sees must prove the tool understood THEIR
            // drawing: alignments, surfaces, CL lines in the CL file, existing sections.
            var ui = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().Contain("RefreshDashboard();", "the dashboard fills on load");
            ui.Should().Contain("ProjectDashboardService().Build(");
            ui.Should().Contain("UnmappedPotentialText()", "an unpriced estimate still shows the size of the project");

            var xaml = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            xaml.Should().Contain("x:Name=\"DashboardBody\"");
        }

        [Fact]
        public void Dashboard_NeverSaysPilot_AndNamesTheClFile()
        {
            var svc = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Sections", "Services", "ProjectDashboardService.cs"));
            svc.Should().NotContain("pilot", "the product is a release, not a trial");
            svc.Should().Contain("קובץ CL:", "the engineer sees which CL drawing was read and how many lines it holds");
            svc.Should().Contain("CountClLines", "the dashboard count comes from the same reader PLAN uses");
        }

        [Fact]
        public void EveryQuantityRow_CanBeShownInTheDrawing()
        {
            // An estimate line the engineer cannot point at in the model is not evidence.
            var xaml = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            xaml.Should().Contain("x:Name=\"BtnShowQuantity\"").And.Contain("Click=\"OnShowQuantity\"");

            var ui = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().Contain("QuantityLocatorService.Show(doc, records, _profile)");
            ui.Should().Contain("BtnShowQuantity.IsEnabled = profileUsable && estimateScanFresh &&")
                .And.Contain("QuantitiesGrid.SelectedItem != null",
                    "source navigation must be disabled for a stale scan or an unusable profile");

            var svc = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Estimate", "QuantityLocatorService.cs"));
            svc.Should().Contain("SetImpliedSelection", "the objects are selected, not only zoomed to");
            svc.Should().Contain("TryGeometryEvidenceExtents(")
                .And.Contain("r.Measurement.GeometryEvidence")
                .And.Contain("drawingUnits.LinearToMetres")
                .And.Contain("לא ניתנים לבחירה ישירה",
                    "XREF child handles are never mis-resolved in the host, but their verified host-WCS bounds still zoom honestly");
            svc.Should().Contain("NumberStyles.HexNumber", "handles are hex");
            svc.Should().Contain("NativeViewZoomService.TryZoom(doc, ext, margin: 1.25)",
                "quantity navigation shares the full WCS/DCS transform and verified native view contract")
                .And.NotContain("ZoomByCommand", "a queued command is not completed source navigation");
            var navigation = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Estimate", "NativeViewZoomService.cs"));
            navigation.Should().Contain("var worldToEye =")
                .And.Contain("ViewZoomPlan.TryCreate(")
                .And.Contain("ed.SetCurrentView(view)")
                .And.Contain("using var actual = ed.GetCurrentView()")
                .And.Contain("Close(actual.CenterPoint.X, fit.CenterX)")
                .And.Contain("Close(actual.Width, fit.Width)")
                .And.Contain("view.PerspectiveEnabled) return false")
                .And.Contain("ViewZoomPlan.AllowsModelGeometry(tileMode, viewport)")
                .And.NotContain("SendStringToExecute");
        }

        [Fact]
        public void LatinFileNames_AreNotBidiReordered_InHebrewSentences()
        {
            // Seen live 2026-08-19: "6422-CIVIL-WEST-WORK.dwg" rendered as "CIVIL-WEST-WORK.dwg-6422"
            // in the dashboard title. Every Latin file name inside Hebrew text is wrapped in an
            // explicit LTR embedding.
            var ui = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().Contain("internal static string Ltr(string? s)");
            ui.Should().Contain("internal static string Ltr(string? s) => Bidi.Ltr(s);", "one bidi helper for every surface");
            ui.Should().Contain("DashboardTitle.Text = \"השרטוט הזה — \" + Ltr(dash.DrawingName)");
            ui.Should().Contain("Ltr(Path.GetFileName(f))", "the footer drawing label too");
            ui.Should().NotContain("$\"השרטוט הזה — {dash.DrawingName}\"");
        }

        [Fact]
        public void FindingLines_AreHebrewFirst_CodeLast_Isolated()
        {
            var f = new MahodAI.CivilDelivery.Shared.DeliveryFinding
            {
                Code = "SEC-STYLE-MISSING", Domain = "sections",
                Severity = MahodAI.CivilDelivery.Shared.FindingSeverity.Warning, Title = "לא הוגדר סגנון",
            };
            var line = MahodAI.CivilDelivery.Shared.Bidi.FindingLine(f);
            line.Should().StartWith("אזהרה: לא הוגדר סגנון");
            line.Should().EndWith("(\u200ESEC-STYLE-MISSING\u200E)", "the code is last and LTR-anchored with LRM (WPF ignores LRE/PDF)");
            MahodAI.CivilDelivery.Shared.Bidi.Ltr("6422-CIVIL.dwg").Should().Be("\u200E6422-CIVIL.dwg\u200E");
            MahodAI.CivilDelivery.Shared.Bidi.Ltr(null).Should().Be("");

            // The palette uses it everywhere a finding is rendered - no raw "[Warning] CODE:" left.
            var ui = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().NotContain("$\"[{f.Severity}] {f.Code}: {f.Title}\"");
            ui.Should().Contain("Bidi.FindingLine(f)");
        }

        [Fact]
        public void EstimateTable_ShowsProposalInTheCodeColumn_AndHebrewMethod()
        {
            // Seen live: 5,000 rows with an empty "סעיף" column looked like the tool found
            // nothing. The top proposal is shown, marked with "?", until it is approved.
            var vm = new MahodAI.Civil3D.Plugin.CivilDelivery.UI.QuantityRowViewModel
            {
                RuleKey = "layer:WALL-EX|length", Layer = "WALL-EX", EntityType = "POLYLINE",
                Method = "polyline-length", ObjectCount = 377, Quantity = 9137.67, Unit = "מטר",
                MappingState = "דרוש מיפוי · הצעה", ProposedCode = "U40.01.0590",
            };
            vm.CatalogCodeDisplay.Should().Be("הצעה: ‎U40.01.0590‎", "a word, not \"?\", and the code LTR (01/10)");
            vm.MethodDisplay.Should().Be("אורך");
            vm.Severity.Should().Be("review");

            vm.CatalogCode = "U40.01.0590";
            vm.CatalogCodeDisplay.Should().Be("‎U40.01.0590‎");

            var xaml = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            xaml.Should().Contain("Text=\"{Binding CatalogCodeDisplay}\"")
                .And.Contain("Text=\"{Binding CatalogDescriptionDisplay}\"")
                .And.Contain("Binding=\"{Binding SourceCategory}\"");
            xaml.Should().NotContain("Header=\"הצעה\"", "the proposal lives in the code column now");
            xaml.Should().Contain("Binding=\"{Binding Source}\"         Width=\"*\"", "the source column absorbs the width, nothing is cut off");
            xaml.Should().Contain("TextTrimming\" Value=\"CharacterEllipsis\"", "cells trim with an ellipsis instead of cutting CURB-EXST to KST");

            var pal = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryPalette.cs"));
            pal.Should().Contain("MinimumSize = new System.Drawing.Size(600, 760)", "620 px left two table rows visible");
            // AutoCAD restores the saved size AFTER Visible=true, so one immediate resize
            // is not enough (live 2026-08-19: reopened at 225 px and the panel clipped).
            pal.Should().Contain("DeferEnsureUsableSize();");
            pal.Should().Contain("_paletteSet.Dock = DockSides.None;", "a dock too narrow to use is floated back, not left broken");
            var cmd = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Commands", "MhdCivilDeliveryCommand.cs"));
            cmd.Should().Contain("CivilDeliveryPalette.Toggle();", "the ribbon button toggles like every other palette");
            pal.Should().Contain("Application.Idle -= Handler;", "the deferred fix unsubscribes itself");
            var xamlRoot = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            // Both axes must be reachable below the usable minimum, while explicit
            // viewport sizing keeps DataGrid star columns finite.
            var rootMarkup = XDocument.Parse(xamlRoot);
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace xamlNames = "http://schemas.microsoft.com/winfx/2006/xaml";
            var viewport = rootMarkup.Descendants(wpf + "ScrollViewer")
                .Single(e => (string?)e.Attribute(xamlNames + "Name") == "PaletteViewport");
            ((string?)viewport.Attribute("HorizontalScrollBarVisibility")).Should().Be("Auto");
            ((string?)viewport.Attribute("VerticalScrollBarVisibility")).Should().Be("Auto");
            var contentPanel = viewport.Element(wpf + "DockPanel")!;
            ((string?)contentPanel.Attribute("LastChildFill")).Should().Be("True");
            ((string?)contentPanel.Attribute("MinWidth")).Should().Be("440");
            // b22 (Codex 06:58): the 900 px minimum is the style's base value. A local MinHeight attribute would outrank the
            // style trigger that raises it to 1130 while "פירוט מקורות ובדיקות" is open (QuantityPaletteLayoutTests).
            contentPanel.Attribute("MinHeight").Should().BeNull("a local value would silently disable the 1130 trigger");
            contentPanel.Element(wpf + "DockPanel.Style")!.Element(wpf + "Style")!.Elements(wpf + "Setter")
                .Single(s => (string?)s.Attribute("Property") == "MinHeight").Attribute("Value")!.Value.Should().Be("855",
                    "b25: the 900 px minimum without the 45 px status strip, now a fixed row below the scrolling content");
            ((string?)contentPanel.Attribute("Width")).Should().Contain("ViewportWidth");
            ((string?)contentPanel.Attribute("Height")).Should().Contain("ViewportHeight");
        }

        [Fact]
        public void Palette_UsesSeverityRows_DisablesApprovalWithoutCatalog_AndRepaintsApplyStatus()
        {
            var xaml = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
            xaml.Should().Contain("x:Key=\"SeverityRow\"")
                .And.Contain("Binding=\"{Binding Severity}\"")
                .And.Contain("Value=\"ok\"")
                .And.Contain("Value=\"review\"")
                .And.Contain("Value=\"blocked\"")
                .And.Contain("Value=\"muted\"");
            xaml.Should().Contain("x:Name=\"SectionsGrid\"")
                .And.Contain("x:Name=\"QuantitiesGrid\"")
                .And.Contain("RowStyle=\"{StaticResource SeverityRow}\"");

            var ui = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
            ui.Should().Contain("BtnApprove.IsEnabled = profileUsable && estimateScanFresh && _catalog != null &&")
                .And.Contain("QuantitiesGrid.SelectedItem != null")
                .And.Contain("_sectionDisplayStatuses[result.RecordId] = result.Status")
                .And.Contain("RebuildSectionRows();",
                    "APPLY/VERIFY outcomes must replace the stale PLAN-only status shown in the table");
        }

        [Fact]
        public void SectionRow_DisplayStatusOverride_DoesNotMutatePlanEvidence()
        {
            var planRecord = new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.SectionPlanRecord
            {
                RecordId = "CL-1",
                Cl = new MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts.ClSourceRecord
                {
                    RecordId = "CL-1", SourceDrawing = "cl.dwg", SourceDrawingHash = "sha",
                    SourceHandle = "A1", SourceEntityType = "LINE", SourceLayer = "CL",
                    SourceEndpoints = new[] { 0d, 0d, 10d, 0d },
                    WcsEndpoints = new[] { 0d, 0d, 10d, 0d },
                },
                Status = MahodAI.CivilDelivery.Shared.DeliveryStatus.Ready,
            };
            var vm = new MahodAI.Civil3D.Plugin.CivilDelivery.UI.SectionRowViewModel
            {
                Record = planRecord,
                StatusOverride = MahodAI.CivilDelivery.Shared.DeliveryStatus.Applied,
            };

            vm.Status.Should().Be("הוחל");
            vm.Severity.Should().Be("ok");
            planRecord.Status.Should().Be(MahodAI.CivilDelivery.Shared.DeliveryStatus.Ready,
                "display refresh must not rewrite immutable PLAN evidence before VERIFY");
        }

        [Theory]
        [InlineData(UtilityProjectionScanState.Complete, 8, 0,
            "לא נמצאה חציית מערכת בחתך זה")]
        [InlineData(UtilityProjectionScanState.Complete, 0, 0,
            "לא נמצאו מערכות בשרטוט")]
        [InlineData(UtilityProjectionScanState.Blocked, 0, 0,
            "סריקת מערכות XREF חסומה — לא ניתן לקבוע חציות")]
        public void SectionRow_StatesWhatUtilityCoverageWasActuallyProven(
            UtilityProjectionScanState state,
            int drawingEntities,
            int sectionCrossings,
            string expected)
        {
            var record = new SectionPlanRecord
            {
                RecordId = "CL-UTIL",
                Cl = new ClSourceRecord
                {
                    RecordId = "CL-UTIL", SourceDrawing = "cl.dwg",
                    SourceDrawingHash = "sha", SourceHandle = "A1",
                    SourceEntityType = "LINE", SourceLayer = "CL",
                    SourceEndpoints = new[] { 0d, 0d, 10d, 0d },
                    WcsEndpoints = new[] { 0d, 0d, 10d, 0d },
                },
                SelectedAlignment = "MAIN",
                SelectedCrossing = new AlignmentCrossing
                {
                    AlignmentName = "MAIN", Point = new[] { 5d, 0d },
                    Station = 100, TangentDeg = 90, GapDistance = 0,
                },
                Status = DeliveryStatus.Ready,
            };
            record.UtilityCoverage.ProjectionScanState = state;
            record.UtilityCoverage.ProjectionDrawingEntityCount = drawingEntities;
            record.UtilityCoverage.ProjectionSectionCrossingCount = sectionCrossings;

            new SectionRowViewModel { Record = record }.Utilities.Should().Be(expected);
        }

        [Fact]
        public void ProjectionCoverageState_IsCapturedBeforeThePanelSummarizesUtilities()
        {
            var contract = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Contracts", "UtilityCoverage.cs"));
            var plan = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services", "SectionPlanService.cs"));
            var viewModel = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryViewModels.cs"));
            var tool = File.ReadAllText(Path.Combine(
                PluginSourceDir, "Tools", "CivilDelivery", "SectionsTools.cs"));
            var smoke = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Commands", "MhdSmokeSectionsCommand.cs"));

            contract.Should().Contain("projection_scan_state")
                .And.Contain("projection_drawing_entity_count")
                .And.Contain("projection_section_crossing_count");
            plan.Should().Contain("ResolveProjectionScanState")
                // Pure failure-scope tests cover locality; the live wiring must
                // no longer erase utilities due to an unrelated sidewalk hatch.
                .And.Contain("SectionProjectionFailureScope.BlocksUtilityScan(f, record.Cl.WcsEndpoints)")
                .And.Contain("ProjectionDrawingEntityCount = projectable.Utilities.Count")
                .And.Contain("ProjectionSectionCrossingCount = merged.Count")
                .And.Contain("ProjectionScanState != UtilityProjectionScanState.Complete");
            viewModel.Should().Contain("SectionProjectionLogic.SystemsSummary")
                .And.Contain("סריקת XREF חסומה — הכיסוי חלקי")
                .And.NotContain("return \"אין מערכות בשרטוט\"");
            tool.Should().Contain("[\"projection_scan_state\"]")
                .And.Contain("[\"projection_drawing_entity_count\"]")
                .And.Contain("[\"projection_section_crossing_count\"]");
            smoke.Should().Contain("projection_scan_state =")
                .And.Contain("projection_drawing_entity_count =")
                .And.Contain("projection_section_crossing_count =");
        }

        [Fact]
        public void CreatedSectionViews_AreArrangedByTheirRealExtents()
        {
            // First real batch: 25 views placed 80 m apart, each ~200 m tall -> elevation
            // axes printed on top of each other. After creation the views are measured
            // and re-gridded. PLAN derives the grid origin by reading live model-space
            // extents; UpdateExt is forbidden because even PLAN must not dirty the DWG.
            var apply = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Sections", "Services", "SectionApplyService.cs"));
            apply.Should().Contain("internal static int ArrangeCreatedViews(");
            apply.Should().Contain("view.GeometricExtents", "the real size, not a guess");
            apply.Should().Contain("item.View.Location = item.View.Location + delta;", "a pure translation");
            apply.Should().Contain("SectionLayoutPlanner.ArrangeSheet(", "the grid maths is the unit-tested Core one");
            apply.Should().Contain("IsSheetOverlapFree(boxes, placements)", "the run log records the self-check");
            apply.Should().Contain("targets.Where(r => r.ManualSectionReuse == null)",
                "a foreign manual SectionView retains the engineer's chosen location");
            apply.Should().Contain("var moved = ArrangeCreatedViews(");
            apply.Should().Contain("tr, db, profile, arrangedTargets, result, layoutOrigin, _log);");
            apply.Should().NotContain("db.UpdateExt(",
                "APPLY consumes PLAN's persisted origin and does not mutate global extents");
            apply.IndexOf("var moved = ArrangeCreatedViews(", StringComparison.Ordinal)
                .Should().BeGreaterThan(apply.IndexOf("foreach (var record in targets)", StringComparison.Ordinal),
                    "arrangement runs after every view of the batch exists");
            var plan = File.ReadAllText(Path.Combine(PluginSourceDir, "CivilDelivery", "Sections", "Services", "SectionPlanService.cs"));
            plan.Should().NotContain("db.UpdateExt(")
                .And.Contain("table[BlockTableRecord.ModelSpace], OpenMode.ForRead")
                .And.Contain("if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;")
                .And.Contain("var ext = entity.GeometricExtents;")
                .And.Contain("RequireFiniteLayoutOrigin")
                .And.NotContain("catch { return (0, 0); }");
        }

        [Fact]
        public void PressureNetworkLimitation_IsStatedWhereItIsImplemented()
        {
            // Civil 3D 2027's SectionSourceType has no pressure-network member. Whoever
            // reads this service next must learn that from the code, not by discovering
            // it in front of an engineer.
            var svc = ServicesFile("SectionSourceService.cs");

            svc.Should().Contain("Pressure");
            svc.Should().Contain("SectionSourceType");
        }
    }
}
