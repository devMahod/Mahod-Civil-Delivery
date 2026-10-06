using System;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Codex 11:51 review of r3: the "only from the separate CL file" decision uses explicit source identity (a host row of
/// the same name makes the layer mixed, never scoped), and a refused CL selection leaves the profile untouched.
/// </summary>
public class ProjectSetupClScopeTests
{
    private static ProjectSetupScan Scan() => new() { RunId = "r", ProjectProfileId = "984" };

    private static ClLayerCandidate Row(string layer, bool host, bool file) =>
        new() { Layer = layer, FromHost = host, FromSeparateFile = file };

    [Fact]
    public void A_layer_offered_only_by_the_separate_file_may_be_scoped_to_it()
    {
        var scan = Scan();
        scan.ExternalClHashes["HW-FoorGrd-CL-M30.dwg"] = new string('c', 64);
        scan.ClLayerCandidates.Add(Row("HW-ALGN-SEC-NAME", host: false, file: true));
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "HW-ALGN-SEC-NAME" }, scan).Should().BeTrue();
    }

    [Fact]
    public void A_mixed_host_and_file_layer_is_never_scoped_to_the_file()
    {
        var scan = Scan();
        scan.ExternalClHashes["CL.dwg"] = new string('c', 64);
        scan.ClLayerCandidates.Add(Row("GFC111", host: true, file: true));
        scan.ClLayerCandidates.Add(Row("TICKS", host: false, file: true));
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "GFC111" }, scan).Should().BeFalse();
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "GFC111", "TICKS" }, scan).Should().BeFalse();
        ProjectSetupLayerScope.AllFromSeparateFileOnly(Array.Empty<string>(), scan).Should().BeFalse();
    }

    [Fact]
    public void Without_a_separate_file_nothing_is_scoped()
    {
        var scan = Scan();
        scan.ClLayerCandidates.Add(Row("HW-ALGN-SEC-NAME", host: false, file: true));
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "HW-ALGN-SEC-NAME" }, scan).Should().BeFalse();
    }

    [Fact]
    public void Merging_a_file_candidate_into_a_same_named_host_row_marks_it_mixed()
    {
        var scan = Scan();
        scan.ClLayerCandidates.Add(Row("GFC111", host: true, file: false));
        var cl = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcd-cl-{Guid.NewGuid():N}.dwg");
        System.IO.File.WriteAllBytes(cl, new byte[] { 1, 2, 3 });
        try
        {
            var external = new ProjectSetupScanner.ExternalClScan
            {
                Path = cl, SourceHash = ArtifactHash.Sha256OfFile(cl), ScanComplete = true,
                SourceLastWriteUtc = System.IO.File.GetLastWriteTimeUtc(cl),
            };
            external.Candidates.Add(Row("GFC111", host: false, file: false));
            external.Candidates.Add(Row("TICKS", host: false, file: false));
            ProjectSetupService.MergeExternalClScan(scan, external);
        }
        finally { System.IO.File.Delete(cl); }

        var gfc = scan.ClLayerCandidates.Find(c => c.Layer == "GFC111")!;
        gfc.FromHost.Should().BeTrue("the host row keeps its own identity when the file row merges in");
        gfc.FromSeparateFile.Should().BeTrue();
        var ticks = scan.ClLayerCandidates.Find(c => c.Layer == "TICKS")!;
        ticks.FromHost.Should().BeFalse();
        ticks.FromSeparateFile.Should().BeTrue();
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "GFC111" }, scan).Should().BeFalse();
        ProjectSetupLayerScope.AllFromSeparateFileOnly(new[] { "TICKS" }, scan).Should().BeTrue();
    }

    [Theory]
    [InlineData("host-and-file", null, null, "מהנדס")]                                  // unknown scope
    [InlineData(null, "ticks", 12.5, "מהנדס")]                                           // unknown mode
    [InlineData(null, SectionStationMarkerLogic.ModeStationMarkers, 12.5, null)]         // no approver
    [InlineData(null, SectionStationMarkerLogic.ModeStationMarkers, 0.0, "מהנדס")]       // invalid width
    [InlineData(ClInstructionReader.LayerScopeSourceFile, null, null, "מהנדס")]          // scope without a CL file
    public void A_refused_CL_selection_leaves_the_profile_untouched(string? scope, string? mode, double? width, string? approver)
    {
        var profile = new ProjectProfile { ProfileId = "984" };
        profile.Sections.Cl.LayerPatterns.Add("BEFORE");
        profile.Sections.Alignments.AllowedNames.Add("100");
        var before = JsonSerializer.Serialize(profile);

        var act = () => ProjectSetupService.ApplySelection(profile, new ProjectSetupSelection
        {
            ClLayers = { "HW-ALGN-SEC-NAME" }, AllowedAlignments = { "200" },
            ClSourceFiles = scope == ClInstructionReader.LayerScopeSourceFile ? new() : new() { "HW-FoorGrd-CL-M30.dwg" },
            ClLayerScope = scope, ClMode = mode, StationMarkerHalfWidthM = width, ApprovedBy = approver,
        });

        act.Should().Throw<InvalidOperationException>();
        JsonSerializer.Serialize(profile).Should().Be(before, "validation happens before any mutation");
    }
}
