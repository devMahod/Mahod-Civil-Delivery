using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// 984 (06.10): the CL drawing keeps its stations as AeccTickLine blocks on HW-ALGN-SEC-NAME, each holding one LINE on
    /// layer "0". Discovery offered them as layer "0" (mixed with host lines on "0"), and a same-named host layer would
    /// have been read as CL too. The effective layer and the "source-file" scope fix both, without touching profiles
    /// that do not set the new fields.
    /// </summary>
    public class ClEffectiveLayerTests
    {
        private static string PluginSourceDir =>
            typeof(ClEffectiveLayerTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        [Theory]
        [InlineData("GFC111", null, "GFC111")]                       // model space: own layer (6422 CL.dwg)
        [InlineData("0", null, "0")]                                 // model space layer 0 stays 0
        [InlineData("0", "HW-ALGN-SEC-NAME", "HW-ALGN-SEC-NAME")]    // 984 tick LINE inside AeccTickLine
        [InlineData("0", "0", "0")]                                  // a block on 0 inside model space
        [InlineData("HW-ALGN", "HW-ALGN-SEC-NAME", "HW-ALGN")]       // a named layer inside a block keeps its own
        [InlineData("0", "ETCH-AR-MIFLAS-2.40|C-PROP-LINE", "ETCH-AR-MIFLAS-2.40|C-PROP-LINE")]
        public void Layer_zero_content_is_drawn_on_its_reference_layer(string own, string? inherited, string expected) =>
            ClEffectiveLayer.Resolve(own, inherited).Should().Be(expected);

        [Fact]
        public void Nested_references_resolve_through_every_level()
        {
            // INSERT on "SEC" -> nested INSERT on "0" -> LINE on "0"  ==> "SEC"
            var outer = ClEffectiveLayer.Resolve("SEC", null);
            var nested = ClEffectiveLayer.Resolve("0", outer);
            ClEffectiveLayer.Resolve("0", nested).Should().Be("SEC");
        }

        [Fact]
        public void An_explicit_scope_never_falls_back_to_the_host()
        {
            var cl = new ProjectProfile.SectionsProfile.ClProfile();
            ClInstructionReader.IsSourceFileScoped(cl).Should().BeFalse("legacy null keeps reading host and file");
            ProjectProfileSchemaPolicy.ClScopeProblems(cl).Should().BeEmpty();

            cl.LayerScope = ClInstructionReader.LayerScopeSourceFile;
            ClInstructionReader.IsSourceFileScoped(cl).Should().BeTrue("the scope alone decides; the host is not read");
            ProjectProfileSchemaPolicy.ClScopeProblems(cl).Should().ContainSingle()
                .Which.Should().Contain("לא הוגדר קובץ CL", "a scope without a file blocks instead of reading the host");

            cl.SourceFiles.Add(@"..\..\Drawing\Data Files\HW\PD\HW-FoorGrd-CL-M30.dwg");
            ProjectProfileSchemaPolicy.ClScopeProblems(cl).Should().BeEmpty();

            cl.LayerScope = "host-and-file";
            ProjectProfileSchemaPolicy.ClScopeProblems(cl).Should().ContainSingle().Which.Should().Contain("אינו מוכר");
            ClInstructionReader.IsSourceFileScoped(cl).Should().BeFalse();
        }

        [Fact]
        public void The_reader_decides_scope_before_reading_the_host()
        {
            var reader = Read("CivilDelivery", "Sections", "Services", "ClInstructionReader.cs");
            var problems = reader.IndexOf("var scopeProblems = ProjectProfileSchemaPolicy.ClScopeProblems(profile.Sections.Cl);");
            var hostLoop = reader.IndexOf("foreach (ObjectId id in readHost ? ms.Cast<ObjectId>() : Enumerable.Empty<ObjectId>())");
            problems.Should().BeGreaterThan(0);
            hostLoop.Should().BeGreaterThan(problems);
            reader.Should().Contain("if (readHost) AttachNearbyLabels(result.Records, labels, profile.Sections.Cl.Numbering.LabelLayerPatterns);");
        }

        [Fact]
        public void Schema_11_marks_and_validates_the_new_CL_fields()
        {
            var profile = new ProjectProfile { ProfileId = "984", SchemaVersion = ProjectProfileSchemaPolicy.Schema9 };
            ProjectProfileSchemaPolicy.HasSchema11Content(profile).Should().BeFalse();
            profile.Sections.Cl.Mode = ProjectProfile.SectionsProfile.ClProfile.ModeStationMarkers;
            ProjectProfileSchemaPolicy.HasSchema11Content(profile).Should().BeTrue();
            ProjectProfileSchemaPolicy.Validate(profile).Should().Contain(f => f.Code == "SHR-PROFILE-SCHEMA11-CONTENT")
                .And.Contain(f => f.Code == "SHR-PROFILE-CL-SCOPE", "station markers without a width or approver are refused");
            profile.SchemaVersion = ProjectProfileSchemaPolicy.Schema11;
            profile.Sections.Cl.StationMarkerHalfWidthM = 12.5;
            profile.Sections.Cl.StationMarkerApprovedBy = "מהנדס";
            ProjectProfileSchemaPolicy.Validate(profile).Should().NotContain(f => f.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void Unset_new_fields_leave_the_profile_yaml_and_effective_hash_unchanged()
        {
            var profile = new ProjectProfile();
            var json = JsonSerializer.Serialize(profile.Sections.Cl);
            json.Should().NotContain("LayerScope").And.NotContain("StationMarker");
            using (var document = JsonDocument.Parse(json))
                document.RootElement.TryGetProperty("Mode", out _).Should().BeFalse("Numbering.Mode is a different field");

            var yaml = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve).Build().Serialize(profile.Sections.Cl);
            yaml.Should().NotContain("layer_scope").And.NotContain("station_marker").And.NotContain("\nmode:");

            var before = EstimateTraceIdentity.EffectiveProfileHash(profile);
            profile.Sections.Cl.LayerScope = ClInstructionReader.LayerScopeSourceFile;
            EstimateTraceIdentity.EffectiveProfileHash(profile).Should().NotBe(before, "a set scope is part of identity");
        }

        [Fact]
        public void Discovery_and_reader_match_the_same_effective_layer()
        {
            var reader = Read("CivilDelivery", "Sections", "Services", "ClInstructionReader.cs");
            reader.Should().NotContain("MatchesLayer(candidate.Entity.Layer")
                .And.Contain("MatchesLayer(candidate.EffectiveLayer, layerPatterns)")
                .And.Contain("SourceLayer = candidate.EffectiveLayer")
                .And.Contain("ClEffectiveLayer.Resolve(reference.Layer, inheritedLayer)")
                .And.Contain("ClEffectiveLayer.Resolve(ent.Layer, inheritedLayer)");

            var scanner = Read("CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs");
            scanner.Should().Contain("ClEffectiveLayer.Resolve(entity.Layer, frame.InheritedLayer)")
                .And.Contain("perLayer[effectiveLayer] = acc = new LayerAccumulator();")
                .And.NotContain("perLayer[ent.Layer]");
        }

        [Fact]
        public void A_layer_picked_from_a_separate_CL_drawing_is_scoped_to_that_drawing()
        {
            var control = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");
            var pick = control.IndexOf("private void OnPickCl(");
            var save = control.IndexOf("ProjectProfileWriter.Save(", pick);
            var scope = control.IndexOf("profileForSave.Sections.Cl.LayerScope = ClInstructionReader.LayerScopeSourceFile;", pick);
            scope.Should().BeGreaterThan(pick).And.BeLessThan(save);

            var reader = Read("CivilDelivery", "Sections", "Services", "ClInstructionReader.cs");
            reader.Should().Contain("var readHost = !IsSourceFileScoped(profile.Sections.Cl);");
        }
    }
}
