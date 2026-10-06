using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Live 07/09 15:41 (1.2.45, reopened working copy): the selected APPLY was refused
    /// because the protected linetype written at 11:21 came back from the DWG with a
    /// style pointer on its gap element, and the replacement sample line was named
    /// "MCD-STA-12145.4-2" because the erased predecessor still counted as taken.
    /// These anchors keep both fixes wired into the plugin sources.
    /// </summary>
    public class LiveResourceRoundTripContractTests
    {
        private static string PluginSourceDir =>
            typeof(LiveResourceRoundTripContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Service(string name) => File.ReadAllText(Path.Combine(
            PluginSourceDir, "CivilDelivery", "Sections", "Services", name));

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, System.StringComparison.Ordinal);
            from.Should().BeGreaterThan(-1, start);
            var to = text.IndexOf(end, from + start.Length, System.StringComparison.Ordinal);
            to.Should().BeGreaterThan(from, end);
            return text.Substring(from, to - from);
        }

        [Fact]
        public void ProtectedLinetypes_AreWrittenAsPlainDashes_NeverAsEmptyTextElements()
        {
            var writer = Between(Service("SectionDecorationService.cs"),
                "private static void WriteLinetypeContract(",
                "internal static SectionAnnotationResourceContracts.LinetypeState ReadLinetypeState(");
            writer.Should().Contain("rec.NumDashes = 0;")
                .And.Contain("rec.NumDashes = spec.DashLengths.Count")
                .And.Contain("rec.SetDashLengthAt(i, spec.DashLengths[i])")
                .And.Contain("rec.SetShapeScaleAt(i, 1.0)")
                .And.Contain("rec.PatternLength = spec.PatternLength")
                .And.NotContain("SetTextAt(")
                .And.NotContain("SetShapeStyleAt(")
                .And.NotContain("SetShapeNumberAt(")
                .And.NotContain("SetShapeOffsetAt(")
                .And.NotContain("SetShapeRotationAt(")
                .And.NotContain("SetShapeIsUcsOrientedAt(")
                .And.NotContain("SetShapeIsUprightAt(");
        }

        [Fact]
        public void MahodOwnedLinetype_IsNormalizedInAnyScope_ForeignRecordStillBlocked()
        {
            var ensure = Between(Service("SectionDecorationService.cs"),
                "private static void EnsureLinetypes(Transaction tr, Database db, bool allowModify)",
                "private static void WriteLinetypeContract(");
            ensure.Should().Contain("SectionAnnotationResourceContracts.IsToolOwnedLinetype(spec, state)")
                .And.Contain("allowModify || mahodOwned ? SharedResourceLogic.Mode.NormalizeAll")
                .And.Contain("SharedResourceLogic.Mode.CreateOnlyNeverModify")
                .And.Contain("if (action == SharedResourceLogic.Action.Block)")
                .And.Contain("is XREF-dependent and cannot be normalized")
                .And.Contain("rec.Name = LegacyLinetypeName(table, spec.Name);")
                .And.Contain("rec = new LinetypeTableRecord { Name = spec.Name };");
            Service("SectionDecorationService.cs").Should().Contain("-LEGACY-");
        }

        [Fact]
        public void VerifyAfterApply_StartsFromAPlanThatHasSeenTheCreatedView()
        {
            var workflow = Service("SectionsWorkflowService.cs");
            var body = workflow.Substring(workflow.IndexOf("internal CurrentSelectedVerification VerifySelectedCurrent(", System.StringComparison.Ordinal));
            body = body.Substring(0, body.IndexOf("SectionVerificationRecoveryService.Authority authority;", System.StringComparison.Ordinal));
            body.Should().Contain("planned is { Action: PlanAction.Unchanged }")
                .And.Contain("? plan : Plan(doc, profile, profileHash);")
                .And.Contain("SectionInputIntegrityService.StaleReason(");
        }

        [Fact]
        public void VerifyLiveGeometry_UsesNormalizedRotationsAndExactPlannedOffsets()
        {
            // First complete live VERIFY (07/09 18:20): labels persisted at 3π/2 vs an
            // expected -π/2; vehicles/arrows recomputed from millimetre-rounded evidence.
            var verify = Service("SectionVerifyService.cs");
            verify.Should().Contain("SectionAnnotationPlacementLogic.RotationsEquivalent(actualRotation, rotation, 0.0000001)")
                .And.Contain("SectionAnnotationPlacementLogic.RotationsEquivalent(rotation, expectedRotation, 0.0000001)")
                .And.Contain("SectionAnnotationPlacementLogic.ExactPlannedOffset(")
                .And.Contain("planned.LaneMidOffsetM, ground.Value, stripKind.Value, flow,")
                .And.Contain("SectionFurnitureLogic.Car, offset, ground.Value,")
                .And.Contain("sectionView, spec, offset, ground.Value);")
                .And.Contain("TextPlacementMismatch(")
                .And.Contain("out var officeBlocksReason")
                .And.Contain("out var fallbackReason")
                .And.Contain("out var arrowReason")
                .And.NotContain("arrow.Offset, ground.Value")
                .And.NotContain("item.Offset, ground.Value")
                .And.NotContain("Near(actualRotation, rotation, 0.0000001)")
                .And.NotContain("Near(rotation, expectedRotation, 0.0000001)");
        }

        [Fact]
        public void ReadBack_StillJudgesEveryElementThroughTheSharedValidator()
        {
            var decoration = Service("SectionDecorationService.cs");
            var ensure = Between(decoration,
                "private static void EnsureLinetypes(Transaction tr, Database db, bool allowModify)",
                "private static void WriteLinetypeContract(");
            ensure.Should().Contain("SectionAnnotationResourceContracts.ValidateLinetype(")
                .And.Contain("failed semantic read-back");
            var reader = Between(decoration,
                "internal static SectionAnnotationResourceContracts.LinetypeState ReadLinetypeState(",
                "internal static IReadOnlyList<string> InvalidLinetypes(");
            reader.Should().Contain("rec.ShapeNumberAt(i)")
                .And.Contain("rec.TextAt(i)")
                .And.Contain("rec.DashLengthAt(i)");
        }

        [Fact]
        public void UpdatedSection_KeepsItsName_BecauseTheErasedPredecessorIsNotTaken()
        {
            var naming = Between(Service("SectionApplyService.cs"),
                "private static string UniqueSampleLineName(",
                "private ObjectId FindOrCreateGroup(");
            naming.Should().Contain("if (id.IsNull || id.IsErased) continue;")
                .And.Contain("!sl.IsErased && !string.IsNullOrEmpty(sl.Name)");
        }
    }
}
