using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;
using L = MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.ClLabelSelection.Label;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// 1.4.1 D2 (984 live b37): the 28 sections were named 6.53, 6.56… — the elevation text on HW-ALGN-SEC-ELEV sits
/// closer to each tick than the name "100" on HW-ALGN-SEC-NAME. Cases agreed with Codex 06.10 12:56.
/// </summary>
public class ClLabelSelectionTests
{
    private const string NameLayer = "HW-ALGN-SEC-NAME";
    private const string ElevLayer = "HW-ALGN-SEC-ELEV";

    [Fact]
    public void A_label_on_the_CL_line_own_layer_beats_a_closer_label_on_another_layer()
    {
        var choice = ClLabelSelection.Choose(new[]
        {
            new L("6.53", 0.6, ElevLayer),
            new L("100", 1.4, NameLayer),
        }, NameLayer, explicitPatterns: null);
        choice.Number.Should().Be("100");
        choice.Ambiguous.Should().BeFalse();
    }

    [Fact]
    public void Without_a_same_layer_label_the_previous_rule_applies()
    {
        ClLabelSelection.Choose(new[] { new L("6.53", 0.6, ElevLayer), new L("7.10", 2.0, "0") }, "GFC111", null)
            .Number.Should().Be("6.53", "no evidence on the CL layer: nearest label with a digit, as before");
    }

    [Fact]
    public void Explicit_label_layer_patterns_are_binding()
    {
        ClLabelSelection.Choose(new[] { new L("6.53", 0.6, ElevLayer), new L("100", 1.4, NameLayer) },
            "SOMETHING-ELSE", new[] { "*SEC-NAME" }).Number.Should().Be("100");
    }

    [Fact]
    public void Explicit_patterns_without_a_matching_label_choose_nothing_and_never_fall_back()
    {
        var choice = ClLabelSelection.Choose(new[] { new L("6.53", 0.6, ElevLayer) }, NameLayer, new[] { "*SEC-NAME" });
        choice.Number.Should().BeNull("an explicit label layer is binding: 6.53 on ELEV is not a fallback");
        choice.Ambiguous.Should().BeFalse();
    }

    [Fact]
    public void Two_different_texts_at_the_same_nearest_distance_are_not_decided_by_order()
    {
        var a = ClLabelSelection.Choose(new[] { new L("101", 1.0, NameLayer), new L("102", 1.0, NameLayer) }, NameLayer, null);
        var b = ClLabelSelection.Choose(new[] { new L("102", 1.0, NameLayer), new L("101", 1.0, NameLayer) }, NameLayer, null);
        a.Number.Should().BeNull();
        a.Ambiguous.Should().BeTrue();
        a.TiedTexts.Should().BeEquivalentTo(new[] { "101", "102" });
        b.Should().BeEquivalentTo(a, "the result must not depend on enumeration order");
        ClLabelSelection.Choose(new[] { new L("101", 1.0, NameLayer), new L("101", 1.0, NameLayer) }, NameLayer, null)
            .Number.Should().Be("101", "the same text twice is not a conflict");
    }

    [Fact]
    public void Texts_without_digits_are_never_section_numbers()
    {
        ClLabelSelection.Choose(new[] { new L("STA AHEAD", 0.2, NameLayer), new L("100", 3.0, NameLayer) }, NameLayer, null)
            .Number.Should().Be("100");
    }

    [Fact]
    public void Layer_zero_text_inside_a_block_carries_the_block_layer_into_the_choice()
    {
        // A label drawn on 0 inside an AeccTickLine-like block on HW-ALGN-SEC-NAME is a name label.
        var effective = ClEffectiveLayer.Resolve("0", NameLayer);
        ClLabelSelection.Choose(new[] { new L("6.53", 0.6, ElevLayer), new L("105", 1.2, effective) }, NameLayer, null)
            .Number.Should().Be("105");
    }

    [Fact]
    public void Two_insertions_of_one_XREF_do_not_lend_each_other_labels()
    {
        // Same XREF file twice: instance "A1F" holds the record; a closer "200" sits in instance "B20".
        var labels = new[]
        {
            new L("200", 0.4, NameLayer, "A1F > B20"),
            new L("100", 1.5, NameLayer, "A1F > 3C7"),
        };
        ClLabelSelection.Choose(labels, NameLayer, null, recordInstanceKey: "A1F > 3C7").Number.Should().Be("100");
        ClLabelSelection.Choose(labels, NameLayer, null, recordInstanceKey: "A1F > B20").Number.Should().Be("200");
        ClLabelSelection.Choose(labels, NameLayer, null, recordInstanceKey: null).Number.Should().BeNull(
            "a model-space line never takes a label from inside an XREF instance");
    }

    // The reader's per-reference step (TraverseReference): handle path, then the instance key below that reference.
    private readonly record struct Node(string? HandlePath, string? Key);
    private static readonly Node HostModelSpace = new(null, null);

    private static Node Enter(Node parent, string handle, bool xref)
    {
        var path = ClLabelSelection.HandlePath(parent.HandlePath, handle);
        return new Node(path, ClLabelSelection.InstanceKeyBelow(parent.Key, xref, path));
    }

    [Fact]
    public void An_XREF_inside_an_ordinary_block_inserted_twice_is_two_instances()
    {
        // Codex 13:21: wrapper block W inserted as A0 and B0 holds XREF reference C5. Keying by the XREF insertion
        // handle alone gave "C5" to both; the full handle path tells them apart.
        var viaA0 = Enter(Enter(HostModelSpace, "A0", xref: false), "C5", xref: true);
        var viaB0 = Enter(Enter(HostModelSpace, "B0", xref: false), "C5", xref: true);
        viaA0.Key.Should().Be("A0/C5");
        viaB0.Key.Should().Be("B0/C5");

        // The B0 copy's "101" lands 0.4 m from the A0 copy's tick; the A0 copy's own "100" is 1.5 m away.
        var labels = new[] { new L("101", 0.4, NameLayer, viaB0.Key), new L("100", 1.5, NameLayer, viaA0.Key) };
        ClLabelSelection.Choose(labels, NameLayer, null, viaA0.Key).Number.Should().Be("100");
        ClLabelSelection.Choose(labels, NameLayer, null, viaB0.Key).Number.Should().Be("101");
    }

    [Fact]
    public void Text_and_line_in_different_tick_blocks_of_one_XREF_instance_stay_eligible()
    {
        var xref = Enter(HostModelSpace, "C5", xref: true);
        var lineTick = Enter(xref, "T1", xref: false);
        var textTick = Enter(xref, "T2", xref: false);
        lineTick.Key.Should().Be("C5").And.Be(textTick.Key, "ordinary blocks below an XREF keep its instance key");
        ClLabelSelection.Choose(new[] { new L("100", 1.4, NameLayer, textTick.Key) }, NameLayer, null, lineTick.Key)
            .Number.Should().Be("100");

        // A nested XREF below a tick block is its own instance; model space keeps no key.
        Enter(lineTick, "D9", xref: true).Key.Should().Be("C5/T1/D9");
        Enter(HostModelSpace, "W1", xref: false).Key.Should().BeNull();
    }

    [Fact]
    public void The_reader_records_the_effective_text_layer_and_keeps_the_evidence_list()
    {
        var dir = typeof(ClLabelSelectionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        var reader = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "Sections", "Services", "ClInstructionReader.cs"));
        reader.Should().Contain("ClEffectiveLayer.Resolve(text.Layer, inheritedLayer)")
            .And.Contain("ClEffectiveLayer.Resolve(mtext.Layer, inheritedLayer)")
            .And.Contain("record.NearbyLabels.AddRange(near.Select(label => label.Text));")
            .And.Contain("record.CandidateSectionNumber = choice.Number;")
            .And.Contain("var currentHandle = ClLabelSelection.HandlePath(handlePath, reference.Handle.ToString());")
            .And.Contain("var source = parentSource;")
            .And.Contain("InstanceKey: ClLabelSelection.InstanceKeyBelow(parentSource.InstanceKey, enteringXref: true, currentHandle));")
            .And.Contain("TraverseReference(tr, nested, transform, source, currentHandle,")
            .And.NotContain("InstanceKey: AppendChain(")
            .And.Contain("record.SourceLayer, labelLayerPatterns, record.SourceInstanceKey)")
            .And.Contain("profile.Sections.Cl.Numbering.LabelLayerPatterns");
    }
}
