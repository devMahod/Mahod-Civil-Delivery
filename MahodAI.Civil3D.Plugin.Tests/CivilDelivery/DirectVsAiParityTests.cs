using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Direct-vs-AI parity (locked plan §12.3, directive §29).
    ///
    /// These assert over the plugin SOURCE, not over loaded types: instantiating a
    /// Civil tool requires the Autodesk assemblies, which are absent outside a Civil
    /// host, and the parity guarantees are structural anyway. This is the same
    /// mechanism the repo's protocol-contract suite already uses (see
    /// <c>MahodPluginSourceDir</c>), so the checks run in the always-green lane.
    /// Object-level parity on a live drawing belongs to the runtime gate.
    /// </summary>
    public class DirectVsAiParityTests
    {
        private static string PluginSourceDir =>
            typeof(DirectVsAiParityTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string ToolsDir => Path.Combine(PluginSourceDir, "Tools", "CivilDelivery");

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(parts.Length == 1 ? ToolsDir : PluginSourceDir, parts[^1]) is var _ && parts.Length == 1
                ? Path.Combine(ToolsDir, parts[0])
                : Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        private static readonly string[] ToolFiles = { "SectionsTools.cs", "EstimateTools.cs", "SetupTools.cs" };

        /// <summary>Every tool the Civil Delivery AI route exposes, in registration order.</summary>
        private static readonly string[] ExpectedToolNames =
        {
            "scan_civil_delivery_project_setup",
            "save_civil_delivery_project_setup",
            "plan_civil_delivery_sections",
            "approve_section_traffic_direction",
            "preview_civil_delivery_sections",
            "apply_civil_delivery_sections",
            "verify_civil_delivery_sections",
            "scan_civil_delivery_quantities",
            "propose_civil_delivery_mappings",
            "save_civil_delivery_mappings",
            "build_civil_delivery_estimate",
            "get_civil_delivery_estimate_trace",
        };

        private static List<string> DeclaredToolClasses() =>
            ToolFiles
                .SelectMany(f => Regex.Matches(Read(f), @"public class (\w+Tool)\s*:\s*DrawingToolBase")
                    .Select(m => m.Groups[1].Value))
                .ToList();

        private static List<string> DeclaredToolNames() =>
            ToolFiles
                .SelectMany(f => Regex.Matches(Read(f), @"public override string Name => ""([^""]+)""")
                    .Select(m => m.Groups[1].Value))
                .ToList();

        [Fact]
        public void ToolNamesMatchTheDocumentedContract()
        {
            DeclaredToolNames().Should().BeEquivalentTo(ExpectedToolNames);
        }

        [Fact]
        public void EveryCivilDeliveryToolIsRegisteredExactlyOnce()
        {
            var registry = File.ReadAllText(Path.Combine(PluginSourceDir, "Tools", "ToolRegistry.cs"));

            foreach (var cls in DeclaredToolClasses())
            {
                Regex.Matches(registry, $@"Register\(new CivilDelivery\.{cls}\(\)\)").Count
                    .Should().Be(1,
                        $"{cls} must be registered exactly once so both routes share one instance path");
            }
        }

        [Fact]
        public void RegisteredToolCountMatchesDeclaredTools()
        {
            var registry = File.ReadAllText(Path.Combine(PluginSourceDir, "Tools", "ToolRegistry.cs"));
            var registered = Regex.Matches(registry, @"Register\(new CivilDelivery\.(\w+)\(\)\)")
                .Select(m => m.Groups[1].Value).ToList();

            registered.Should().BeEquivalentTo(DeclaredToolClasses(),
                "a declared-but-unregistered tool is invisible to the AI; a registered-but-missing one breaks startup");
        }

        [Theory]
        [InlineData("SectionsTools.cs", "ApplySectionsTool")]
        [InlineData("EstimateTools.cs", "BuildEstimateTool")]
        public void MutatingTools_RequireExplicitConfirmation(string file, string toolClass)
        {
            var body = ClassBody(Read(file), toolClass);
            var schema = SchemaText(body);

            schema.Should().Contain(@"""confirm""", $"{toolClass} must take a confirm flag");
            schema.Should().MatchRegex(@"""required""\s*:\s*\[\s*""confirm""",
                $"{toolClass} must make confirm mandatory in its schema");
            body.Should().Contain(@"GetBoolParam(parameters, ""confirm"") != true",
                $"{toolClass} must actually enforce confirm at runtime, not just document it");
        }

        [Fact]
        public void SaveProjectSetupTool_RequiresEngineerChoiceAndApprover()
        {
            var body = ClassBody(Read("SetupTools.cs"), "SaveProjectSetupTool");

            SchemaText(body).Should().MatchRegex(@"""required""\s*:\s*\[""cl_layers"",\s*""approved_by""\]",
                "the AI may neither invent the CL source nor approve on a human's behalf");
            body.Should().Contain("approved_by is required",
                "the refusal must be explicit at runtime");
            body.Should().Contain("is not among the scanned candidates",
                "a layer the drawing never offered must be rejected");
            body.Should().Contain("does not exist in this drawing",
                "an alignment/source the drawing never offered must be rejected");
        }

        [Theory]
        [InlineData("SectionsTools.cs", "PlanSectionsTool")]
        [InlineData("EstimateTools.cs", "ScanEstimateQuantitiesTool")]
        [InlineData("SetupTools.cs", "ScanProjectSetupTool")]
        public void ReadOnlyTools_DoNotDemandConfirmation(string file, string toolClass)
        {
            var schema = SchemaText(ClassBody(Read(file), toolClass));
            schema.Should().NotMatchRegex(@"""required""\s*:\s*\[[^\]]*""confirm""",
                $"{toolClass} is read-only; demanding confirmation trains users to confirm blindly");
        }

        [Fact]
        public void BothRoutesShareOneApplyImplementation()
        {
            var tools = Read("SectionsTools.cs");

            tools.Should().Contain("SectionApplyService.SelectTargets",
                "the AI route must select targets through the same rule as the command");
            tools.Should().Contain("applyService.ApplyCore",
                "the AI route must create objects through the production service");
            tools.Should().NotContain("SampleLine.Create",
                "object creation lives in the service, never duplicated in a tool wrapper");
            tools.Should().NotContain("SectionView.Create");
            tools.Should().NotContain("SampleLineGroup.Create");
        }

        [Fact]
        public void AiApplyCore_EnforcesCompleteBatchInsideTheTransactionBoundary()
        {
            var apply = Read("CivilDelivery", "Sections", "Services", "SectionApplyService.cs");
            var start = apply.IndexOf("public bool ApplyCore(", StringComparison.Ordinal);
            var end = apply.IndexOf("private static void BlockForStaleScope", start, StringComparison.Ordinal);
            var body = apply.Substring(start, end - start);

            body.Should().Contain("SectionPlanLogic.UnresolvedBatchRecords(plan)")
                .And.Contain("SectionsWorkflowService.RequirePlanEvidence(plan)")
                .And.Contain("SectionFindingCodes.PlanEvidenceInvalid")
                .And.Contain("BlockForIncompleteBatch")
                .And.Contain("BlockForNoMutationEvidence")
                .And.Contain("expectedIds.SetEquals",
                    "ApplyCore itself must reject unresolved rows, synthetic no-op evidence, and partial target lists");

            var tool = ClassBody(Read("SectionsTools.cs"), "ApplySectionsTool");
            tool.Should().Contain("PlanAction.Excluded")
                .And.Contain("Status = SectionPlanLogic.HasValidExplicitExclusion(skipped)")
                .And.Contain("DeliveryStatus.Verified",
                    "only a fully approved exclusion may be reported as resolved by the AI wrapper");
        }

        [Fact]
        public void AiToolsDoNotImplementEngineeringMath()
        {
            foreach (var file in ToolFiles)
            {
                var text = Read(file);
                text.Should().NotContain("Math.Atan2", $"{file} must not compute geometry");
                text.Should().NotContain("SkewFromNormalDeg", $"{file} must not compute skew");
                text.Should().NotContain("SectionMath.", $"{file} must not reach into the geometry core");
                text.Should().NotContain("* line.Price", $"{file} must not compute money");
                text.Should().NotContain("new EstimateLine", $"{file} must not assemble estimate lines");
            }
        }

        [Fact]
        public void MappingApprovalTool_RequiresApproverAndRealScanGroups()
        {
            var body = ClassBody(Read("EstimateTools.cs"), "SaveEstimateMappingsTool");

            SchemaText(body).Should().MatchRegex(@"""required""\s*:\s*\[""mappings"",\s*""approved_by""\]",
                "a mapping is an engineering decision, not an AI conclusion");
            body.Should().Contain("does not exist in the last scan",
                "the AI cannot invent a quantity group to map");
            body.Should().Contain("SaveApprovedMappings",
                "saving must go through the validating service, not a local write");
            body.Should().Contain("RequireFreshForDecision(")
                .And.Contain("PublishProfileDecisionOrRestore(")
                .And.Contain("CivilDeliverySession.SetScan(",
                    "AI mapping must publish the same rebased evidence and advance state atomically");
        }

        [Fact]
        public void ProposalTool_IsReadOnlyAndLabelsSuggestionsUnapproved()
        {
            var body = ClassBody(Read("EstimateTools.cs"), "ProposeEstimateMappingsTool");

            body.Should().NotContain("SaveApprovedMappings",
                "proposing must never write to the profile");
            body.Should().Contain("suggestions only",
                "the tool result must tell the model it may not approve on its own");
        }

        [Theory]
        [InlineData("ProposeEstimateMappingsTool", "PublishMappingProposalEvidence")]
        [InlineData("SaveEstimateMappingsTool", "SaveApprovedMappings")]
        [InlineData("BuildEstimateTool", "workflow.Build")]
        public void EstimateAiSideEffects_RunOnlyAfterReadTransactionClosed(
            string className, string sideEffect)
        {
            var source = Read("EstimateTools.cs");
            source.Should().Contain(
                $"public class {className} : DrawingToolBase, IReadOnlyTransactionClosedObserver");
            var body = ClassBody(source, className);
            var callback = body.IndexOf(
                "public void OnReadOnlyTransactionClosed", StringComparison.Ordinal);
            callback.Should().BeGreaterThan(0);
            body[..callback].Should().NotContain(sideEffect,
                "durable evidence/profile/export work cannot happen while the host read transaction is live");
            body[callback..].Should().Contain(sideEffect);
        }

        [Fact]
        public void EstimateBuildAndTrace_AreEvidenceBoundAndBlockedExportIsExplicit()
        {
            var source = Read("EstimateTools.cs");
            var build = ClassBody(source, "BuildEstimateTool");
            build.Should().Contain("EstimatePreflightPolicy.ExportBlockingReasons")
                .And.Contain("correctly_blocked")
                .And.Contain("export_blockers")
                .And.Contain("if (blockers.Count == 0)");
            var trace = ClassBody(source, "GetEstimateTraceTool");
            trace.Should().Contain("RequirePublishedEstimateBuildEvidence")
                .And.Contain("ClearEstimateResult");
        }

        [Fact]
        public void SetupAndExternalClDiscovery_AreFailClosedReadOnlyEvidence()
        {
            var tools = Read("SetupTools.cs");
            foreach (var className in new[] { "ScanProjectSetupTool", "SaveProjectSetupTool" })
            {
                tools.Should().Contain(
                    $"public class {className} : DrawingToolBase, IReadOnlyTransactionClosedObserver");
            }
            var scanner = Read(
                "CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
            scanner.Should().Contain("sideTr.Abort()")
                .And.NotContain("sideTr.Commit()")
                .And.Contain("ScanComplete = false")
                .And.Contain("SetupScanIncomplete")
                .And.Contain("RequireSourceUnchanged")
                .And.Contain("SourceHash");
        }

        [Fact]
        public void SmokeEstimate_DoesNotClaimExcelWhenPreflightCorrectlyBlocksExport()
        {
            var command = Read("CivilDelivery", "Commands", "MhdEstimateCommand.cs");
            command.Should().Contain("if (EstimatePreflightPolicy.CanExport(estimate))")
                .And.Contain("export_preflight_blocked")
                .And.Contain("correctly_blocked")
                .And.Contain("ExportBlockingReasons(estimate)");
        }

        [Fact]
        public void AiToolsCannotResolveAmbiguityOrInventStatuses()
        {
            foreach (var file in ToolFiles)
            {
                var text = Read(file);
                text.Should().NotContain("SelectedAlignment =",
                    $"{file} must never pick an alignment — that is a REVIEW_REQUIRED decision");
                text.Should().NotContain("Status = DeliveryStatus.Ready",
                    $"{file} must never promote a record's engineering status");
                text.Should().NotContain("CandidateCatalogCode =",
                    $"{file} must never assign a catalog code as fact");
            }
        }

        [Fact]
        public void AiApplyRoute_SurfacesFailureInsteadOfCommitting()
        {
            var body = ClassBody(Read("SectionsTools.cs"), "ApplySectionsTool");

            body.Should().Contain("transaction must be aborted",
                "a failed batch must report failure so the executor's commit gate aborts");
            body.Should().Contain("ToolResult.Fail",
                "a failed apply must not return a success result");
        }

        [Theory]
        [InlineData("PreviewSectionsTool")]
        [InlineData("VerifySectionsTool")]
        public void AiReadOnlySectionTools_CannotCommitTheirCivilTransaction(string className)
        {
            var body = ClassBody(Read("SectionsTools.cs"), className);

            body.Should().Contain("ToolResult.ReadOnly",
                    $"{className} may succeed and return evidence but its Civil transaction must be aborted")
                .And.NotContain("ToolResult.Ok(",
                    $"{className} must not return the executor's committable success factory");
        }

        [Fact]
        public void AiApplySectionTool_RemainsCommittableOnSuccessfulAtomicApply()
        {
            var body = ClassBody(Read("SectionsTools.cs"), "ApplySectionsTool");

            body.Should().Contain("ToolResult.Ok(")
                .And.NotContain("ToolResult.ReadOnly",
                    "the read-only hardening must not roll back a successful APPLY");
        }

        [Theory]
        [InlineData("PreviewSectionsTool", "Preview.PreparePreview")]
        [InlineData("ApplySectionsTool", "applyService.ApplyCore")]
        [InlineData("VerifySectionsTool", "verifyService.Verify")]
        public void AiSectionConsumersRejectStaleSessionStateBeforeCivilWork(
            string className, string civilOperation)
        {
            var body = ClassBody(Read("SectionsTools.cs"), className);
            var guard = body.IndexOf(
                "SectionToolProfileFreshness.RequireCurrent", StringComparison.Ordinal);
            var operation = body.IndexOf(civilOperation, StringComparison.Ordinal);

            guard.Should().BeGreaterThan(-1);
            operation.Should().BeGreaterThan(guard,
                "drawing/profile scope must be proven before display, write, or read-back");
            body.Should().Contain("context.ProfileSource")
                .And.Contain("context.ProfileWriteTarget");
            Read("SectionsTools.cs").Should()
                .Contain("DrawingScopeIdentity.For(doc)")
                .And.Contain("ActiveProjectProfileService.ReloadForExistingWorkflow(");
        }

        [Fact]
        public void CentralPreviewAndVerifyRejectStaleScopeBeforeCivilAccess()
        {
            var source = Read("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
            var previewStart = source.IndexOf("public SectionPreviewDisplay Preview(", StringComparison.Ordinal);
            var previewEnd = source.IndexOf("public void ClearPreview", previewStart, StringComparison.Ordinal);
            var preview = source.Substring(previewStart, previewEnd - previewStart);
            preview.IndexOf("SectionPlanLogic.ScopeStaleReason", StringComparison.Ordinal)
                .Should().BeLessThan(preview.IndexOf("_previewService.PreparePreview", StringComparison.Ordinal));

            var verifyStart = source.IndexOf("public SectionVerifyResult Verify(", StringComparison.Ordinal);
            var verifyEnd = source.IndexOf("// ------------------------------------------------------------- evidence",
                verifyStart, StringComparison.Ordinal);
            var verify = source.Substring(verifyStart, verifyEnd - verifyStart);
            verify.IndexOf("SectionPlanLogic.ScopeStaleReason", StringComparison.Ordinal)
                .Should().BeLessThan(verify.IndexOf("StartTransaction", StringComparison.Ordinal));

            Read("CivilDelivery", "Commands", "MhdSectionsCommand.cs")
                .Should().Contain("doc, previewProfile, previewProfileHash, _lastPlan");
            Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs")
                .Should().Contain("var preview = _sections.Preview(")
                .And.Contain("selected.Record.RecordId",
                    "the palette preview is tied to the row the engineer selected");
        }

        [Fact]
        public void SessionStateIsSharedBetweenRoutes()
        {
            var session = Read("CivilDeliverySession.cs");
            foreach (var member in new[] { "LastPlan", "LastApply", "LastScan", "LastEstimate", "LastSetupScan" })
            {
                session.Should().Contain($"public static", "session exposes shared state");
                session.Should().Contain(member,
                    $"both routes must see {member} so the engineer gets one consistent workflow");
            }
        }

        [Fact]
        public void DirectCommandsAndToolsCallTheSameWorkflowServices()
        {
            var sectionsCommand = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Commands", "MhdSectionsCommand.cs"));
            var estimateCommand = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Commands", "MhdEstimateCommand.cs"));

            sectionsCommand.Should().Contain("SectionsWorkflowService",
                "the direct command must go through the shared workflow service");
            estimateCommand.Should().Contain("EstimateWorkflowService");

            Read("SectionsTools.cs").Should().Contain("SectionsWorkflowService");
            Read("EstimateTools.cs").Should().Contain("EstimateWorkflowService");
        }

        /// <summary>
        /// The JSON schemas live inside C# verbatim strings, where every quote is
        /// doubled. Collapse them so schema assertions read as plain JSON.
        /// </summary>
        private static string SchemaText(string classBody) => classBody.Replace("\"\"", "\"");

        /// <summary>Extracts one class body by brace matching, so assertions stay scoped to that tool.</summary>
        private static string ClassBody(string source, string className)
        {
            var start = source.IndexOf($"public class {className}", StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, $"class {className} must exist");

            var brace = source.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source[start..(i + 1)];
                }
            }
            throw new InvalidOperationException($"Unbalanced braces reading {className}");
        }
    }
}
