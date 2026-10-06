using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// 1.4.1, project 984 (06.10, live b34 scan of the employee drawing): layer 2519-simun818 (206.70 m) was proposed
/// 08.10.0121 "תוספת מחיר לסעיפי החפירה עבור הנחת לוחות סימון מפלסטיק עבור סימון כבלי חה"ח" — an electrical cable
/// warning item — because the bare word "סימון" matched. Arthur: "SIMUN אומר שזה סימון כבישים". As a road-marking
/// subject, only marking work (קו ניתוב / צביעת שטחים / ...) may be offered. Descriptions are the real NTI 07-2026 rows.
/// </summary>
public sealed class SimunRoadMarkingProposalTests
{
    private const string CableWarningPlates =
        "תוספת מחיר לסעיפי החפירה עבור הנחת לוחות סימון מפלסטיק עבור סימון כבלי חה\"ח";
    private const string GuideLine10 =
        "קו ניתוב ברוחב 10 ס''מ בחומר סימון ארוך קיים דו רכיבי בגון לבן/צהוב במרקם \"חלק\"";
    private const string AreaPaint =
        "צביעת שטחים בחומר סימון ארוך קיים דו רכיבי בגוון לבן/צהוב (''קוביות'', קווי-עצירה, איי-תנועה, פסים למעבר חציה,";
    private const string WaterPipeTape =
        "סרט סימון פלסטי כחול ברוחב 10 ס\"מ ובעובי 1 מ\"מ עם כיתוב \"קו מים\" מונח בחפירה מעל צנרת מים";

    [Theory]
    [InlineData("2519-simun818")]
    [InlineData("HW-SIMUN")]
    [InlineData("TR-FloorGrd-GM-PD-M30|2519-simun818")]
    [InlineData("SIMUNE-KVISH")]
    [InlineData("סימון כבישים")]
    public void A_SIMUN_layer_is_road_marking_and_never_buys_cable_or_pipe_markers(string layer)
    {
        MappingProposalEngine.IsSemanticallyCompatible(layer, CableWarningPlates).Should().BeFalse();
        MappingProposalEngine.IsSemanticallyCompatible(layer, WaterPipeTape).Should().BeFalse();
        MappingProposalEngine.IsSemanticallyCompatible(layer, GuideLine10).Should().BeTrue();
        MappingProposalEngine.IsSemanticallyCompatible(layer, AreaPaint).Should().BeTrue();
    }

    [Theory]
    [InlineData("SIMUNIOT")]          // not the word SIMUN: no road-marking subject is invented
    [InlineData("HASIMUNA")]
    public void Only_the_whole_word_counts(string layer) =>
        MappingProposalEngine.IsSemanticallyCompatible(layer, CableWarningPlates).Should().BeTrue(
            "without a road-marking subject the existing (electrical) guards decide, as before");

    [Fact]
    public void The_984_group_is_offered_marking_items_only()
    {
        var group = new MappingProposalEngine.DiscoveredGroup(
            "layer:2519-simun818|length", "TR-FloorGrd-GM-PD-M30|2519-simun818", "length", "מטר", 2, 206.70);
        var catalog = new CatalogSnapshot
        {
            SnapshotId = "nti-2026-subset", FileHash = new string('b', 64),
            Items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase)
            {
                ["08.10.0121"] = new() { Code = "08.10.0121", Description = CableWarningPlates, UnitRaw = "מטר" },
                ["51.32.1850"] = new() { Code = "51.32.1850", Description = GuideLine10, UnitRaw = "מטר" },
                ["57.02.3350"] = new() { Code = "57.02.3350", Description = WaterPipeTape, UnitRaw = "מטר" },
            },
        };

        var proposals = MappingProposalEngine.Propose(new[] { group }, catalog,
            new[] { "08.10.0121", "51.32.1850", "57.02.3350" });

        proposals.Select(p => p.ProposedCode).Should().NotContain("08.10.0121").And.NotContain("57.02.3350");
        proposals.Should().Contain(p => p.ProposedCode == "51.32.1850",
            "a SIMUN layer must still get the genuine marking item, not an empty list (Codex 12:03)");
        proposals.Should().OnlyContain(p => p.Status == "PROPOSED_UNAPPROVED");
    }
}
