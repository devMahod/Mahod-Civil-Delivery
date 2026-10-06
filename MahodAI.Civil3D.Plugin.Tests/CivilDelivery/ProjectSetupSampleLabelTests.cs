using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// 1.4.1 r9 (b38 live, shot b38_03; Codex 14:05): the CL layer picker listed 6.53/6.56/6.58 — station elevations on
/// HW-ALGN-SEC-ELEV — as the samples of HW-ALGN-SEC-NAME, because setup discovery took the nearest text of any layer.
/// Samples now follow the PLAN evidence order (ClLabelSelection). Display only; layer ranking is not touched.
/// </summary>
public class ProjectSetupSampleLabelTests
{
    private const string NameLayer = "HW-ALGN-SEC-NAME";
    private const string ElevLayer = "HW-ALGN-SEC-ELEV";
    private static readonly Pt2 Mid = new(0, 0);

    private static (Pt2, string, string) T(string text, double distance, string layer) => (new Pt2(distance, 0), text, layer);

    [Fact]
    public void Each_layer_shows_its_own_label_as_the_sample()
    {
        var labels = new[] { T("6.53", 0.6, ElevLayer), T("100", 1.4, NameLayer) };
        ProjectSetupScanner.SampleLabel(Mid, labels, NameLayer).Should().Be("100");
        ProjectSetupScanner.SampleLabel(Mid, labels, ElevLayer).Should().Be("6.53");
    }

    [Fact]
    public void A_layer_without_its_own_label_keeps_the_previous_nearest_rule_within_the_radius()
    {
        var labels = new[] { T("6.53", 0.6, ElevLayer), T("7.10", 2.0, "0"), T("999", 25.0, "GFC111") };
        ProjectSetupScanner.SampleLabel(Mid, labels, "GFC111").Should().Be("6.53",
            "no text on GFC111 within 20 m: nearest text with a digit, as before");
        ProjectSetupScanner.SampleLabel(Mid, new[] { T("999", 25.0, "GFC111") }, "GFC111").Should().BeNull();
    }

    [Fact]
    public void A_tie_between_different_texts_gives_no_sample_and_text_without_digits_is_not_a_sample()
    {
        ProjectSetupScanner.SampleLabel(Mid,
            new[] { T("STA BACK = 0+000.00", 1.0, NameLayer), T("STA AHEAD = 0+500.00", 1.0, NameLayer) }, NameLayer)
            .Should().BeNull("the picker must not depend on enumeration order");
        ProjectSetupScanner.SampleLabel(Mid, new[] { T("SECTION", 0.2, NameLayer), T("101", 3.0, NameLayer) }, NameLayer)
            .Should().Be("101");
    }

    [Fact]
    public void Layer_zero_text_in_a_tick_block_counts_on_the_block_layer()
    {
        var labels = new[] { T("6.53", 0.6, ElevLayer), T("105", 1.2, ClEffectiveLayer.Resolve("0", NameLayer)) };
        ProjectSetupScanner.SampleLabel(Mid, labels, NameLayer).Should().Be("105");
    }

    [Fact]
    public void Discovery_records_the_effective_text_layer_and_the_picker_samples_use_it()
    {
        var dir = typeof(ProjectSetupSampleLabelTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        var scanner = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs"));
        scanner.Should().Contain("ClEffectiveLayer.Resolve(entity.Layer, frame.InheritedLayer), labels);")
            .And.Contain("labels.Add((Transform(xform, t.Position), t.TextString.Trim(), effectiveLayer));")
            .And.Contain("labels.Add((Transform(xform, mt.Location), mt.Text.Trim(), effectiveLayer));")
            .And.Contain("var near = SampleLabel(mid, labels, candidate.Layer);")
            .And.NotContain(".OrderBy(l => l.Pos.DistanceTo(mid))");
        // Codex 14:21: the samples stay display-only while scoring precedes them; moving this order changes ranking.
        scanner.IndexOf("ScoreCandidate(candidate, probe.Count);").Should()
            .BeLessThan(scanner.IndexOf("AttachSampleLabels(candidate, acc, labels);"));
    }
}
