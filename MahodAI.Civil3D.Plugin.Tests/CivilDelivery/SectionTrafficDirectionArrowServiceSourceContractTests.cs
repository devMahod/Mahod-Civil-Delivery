using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class SectionTrafficDirectionArrowServiceSourceContractTests
    {
        private static string PluginSourceDir =>
            typeof(SectionTrafficDirectionArrowServiceSourceContractTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        [Fact]
        public void RendererImportsPinnedNatalyOfficeBlockAndRegistersOneReference()
        {
            var source = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services",
                "SectionTrafficDirectionArrowService.cs"));
            var placement = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services",
                "SectionAnnotationPlacementContract.cs"));

            source.Should().Contain("SectionTrafficDirectionAnnotationLogic.TryBuild")
                .And.Contain("SectionAnnotationPlacementContract.Map(")
                .And.Contain("SectionAnnotationPlacementLogic.TryArrowPlacement")
                .And.Contain("GetManifestResourceStream(ResourceName)")
                .And.Contain("SHA256.HashData(bytes)")
                .And.Contain("targetDb.Insert")
                .And.Contain("GeometryFingerprint")
                .And.Contain("new BlockReference(placement.Position, _definition.BlockId)")
                .And.Contain("ColorIndex = placement.Layout.ColorIndex")
                .And.Contain("Rotation = placement.Rotation")
                .And.Contain("NormalizeByBlockDisplay")
                .And.Contain("FormatTrafficDirectionArrowReference")
                .And.Contain("rendered.Reference.Handle.ToString()")
                .And.Contain("rendered.Reference.ObjectId.IsNull")
                .And.NotContain("new Line(")
                .And.NotContain("OfficeCarViewForSignedOffset");

            placement.Should().Contain("FindXYAtOffsetAndElevation")
                .And.Contain("Point3d?");

            var assetPath = Path.Combine(PluginSourceDir, "assets", "HW-ARRW-01.dwg");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assetPath)));
            actual.Should().Be(
                "3E18667013887CA89A64AA043B5EB4FCB7195631FDD18D841FE24E46F96B2943");
            File.ReadAllText(Path.Combine(PluginSourceDir, "MahodAI.Civil3D.Plugin.csproj"))
                .Should().Contain("assets\\HW-ARRW-01.dwg")
                .And.Contain("MahodAI.Civil3D.Plugin.assets.sections.HW-ARRW-01.dwg");
        }

        [Fact]
        public void BuiltPluginEmbedsExactPinnedNatalyOfficeArrowBytes()
        {
            const string resourceName =
                "MahodAI.Civil3D.Plugin.assets.sections.HW-ARRW-01.dwg";
            const string expectedSha256 =
                "3E18667013887CA89A64AA043B5EB4FCB7195631FDD18D841FE24E46F96B2943";
            // Inspect the product beside this test assembly: an isolated release
            // lane places its exact staged DLL here. A repository bin/Debug path
            // can be absent or refer to an unrelated earlier build.
            var pluginPath = Path.Combine(
                AppContext.BaseDirectory, ProductAssembly.FileName);

            File.Exists(pluginPath).Should().BeTrue(
                "the executed test lane must include its product DLL");
            var pluginAssembly = Assembly.LoadFile(Path.GetFullPath(pluginPath));
            using var stream = pluginAssembly.GetManifestResourceStream(resourceName);
            stream.Should().NotBeNull(
                $"{resourceName} must be a deterministic embedded resource");
            using var bytes = new MemoryStream();
            stream!.CopyTo(bytes);

            Convert.ToHexString(SHA256.HashData(bytes.ToArray()))
                .Should().Be(expectedSha256);
        }

        [Fact]
        public void RendererFailsClosedWithoutPerStripDirectionProvenance()
        {
            var source = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services",
                "SectionTrafficDirectionArrowService.cs"));

            source.Should().Contain("!direction.IsResolved")
                .And.Contain("direction.EvidenceMode")
                .And.Contain("traffic-arrow strip kind contradicts motor/bicycle evidence mode")
                .And.Contain("office block was not placed because direction is unresolved")
                .And.Contain("No schematic arrow is an allowed fallback");
        }

        [Fact]
        public void NormalizedArrowDisplayFingerprint_IsStableAcrossImportVerifyAndReuse()
        {
            var source = File.ReadAllText(Path.Combine(
                PluginSourceDir, "CivilDelivery", "Sections", "Services",
                "SectionTrafficDirectionArrowService.cs"));

            var sourceModel = source.IndexOf("var sourceModel =", StringComparison.Ordinal);
            var normalizeSource = source.IndexOf(
                "NormalizeByBlockDisplay(sourceTr, sourceModel)", sourceModel,
                StringComparison.Ordinal);
            var sourceFingerprint = source.IndexOf(
                "sourceEntityFingerprint = SectionVehicleBlockService.EntityFingerprint",
                sourceModel, StringComparison.Ordinal);
            normalizeSource.Should().BeGreaterThan(sourceModel)
                .And.BeLessThan(sourceFingerprint,
                    "the expected hash must represent the normalized ByBlock contract");

            var existing = source.IndexOf("var existing =", StringComparison.Ordinal);
            var normalizeExisting = source.IndexOf(
                "NormalizeByBlockDisplay(tr, existing)", existing,
                StringComparison.Ordinal);
            var liveFingerprint = source.IndexOf("var liveEntityFingerprint", existing,
                StringComparison.Ordinal);
            normalizeExisting.Should().BeGreaterThan(existing)
                .And.BeLessThan(liveFingerprint,
                    "second-run reuse compares the same normalized display contract");

            var imported = source.IndexOf("var imported =", StringComparison.Ordinal);
            var normalizeImported = source.IndexOf(
                "NormalizeByBlockDisplay(tr, imported)", imported,
                StringComparison.Ordinal);
            var normalizeImportedHeader = source.IndexOf(
                "SectionVehicleBlockService.NormalizeTargetHeader(", normalizeImported,
                StringComparison.Ordinal);
            var importedFingerprint = source.IndexOf("var importedEntityFingerprint", imported,
                StringComparison.Ordinal);
            normalizeImported.Should().BeGreaterThan(imported)
                .And.BeLessThan(normalizeImportedHeader);
            normalizeImportedHeader.Should().BeLessThan(importedFingerprint,
                "first import must compare entity semantics only after display/header normalization");
            source.IndexOf("var importedFingerprint =", imported, StringComparison.Ordinal)
                .Should().BeGreaterThan(importedFingerprint,
                    "the persisted full definition hash must attest the finalized target header");
        }
    }
}
