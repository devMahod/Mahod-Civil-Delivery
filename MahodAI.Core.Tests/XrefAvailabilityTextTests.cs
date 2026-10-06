using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

/// <summary>
/// 1.4.1 D1 (984 live b37): six XREFs saved as Unloaded blocked every section with one line per insertion and no way
/// out. The policy is unchanged (still blocking); the wording separates unloaded from unresolved and groups by name.
/// </summary>
public class XrefAvailabilityTextTests
{
    private static DeliveryFinding F(string name, bool unloaded, string consequence = "לא ניתן להוכיח חצי כיוון נסיעה") => new()
    {
        Code = SectionFindingCodes.XrefTraversalUnresolved,
        Domain = "sections",
        Severity = FindingSeverity.Error,
        Title = XrefAvailabilityText.Title(name, unloaded, consequence),
        Message = XrefAvailabilityText.Message(name, unloaded, @"C:\p\" + name + ".dwg"),
        RecommendedAction = XrefAvailabilityText.Action(unloaded),
    };

    [Fact]
    public void Unloaded_and_unresolved_are_told_apart_with_their_own_way_out()
    {
        F("TR-Rampa-M30", unloaded: true).Title.Should().Contain("לא טעון בשרטוט (Unloaded)");
        F("TR-Rampa-M30", unloaded: true).RecommendedAction.Should().Contain("Reload");
        F("X", unloaded: false).Title.Should().Contain("לא פתור/לא נטען מהנתיב השמור")
            .And.NotContain("לא נמצא", "an unresolved XREF is not proven missing from disk");
        F("X", unloaded: false).RecommendedAction.Should().Contain("Found At").And.Contain("הטעינה");
    }

    [Fact]
    public void The_palette_line_counts_names_not_insertions_or_collectors()
    {
        var findings = new List<DeliveryFinding>
        {
            F("TR-Rampa-M30", true), F("TR-Rampa-M30", true, "לא ניתן להוכיח כיסוי הקרנה מלא"), F("TR-Rampa-M30", true),
            F("SR-Eilat_TH-MHD", true), F("HW-MAAR-EV-CD", true), F("MISSING-BG", false),
        };
        var line = XrefAvailabilityText.Summarize(findings)!;
        line.Should().Contain("3 XREF לא טעונים").And.Contain("Reload")
            .And.Contain("1 XREF לא פתורים/לא נטענו מהנתיב השמור").And.Contain("XREF Manager").And.NotContain("לא נמצאו");
        line.IndexOf("לא טעונים").Should().BeLessThan(line.IndexOf("לא פתורים"), "the common case leads");
        // b38/b39 live (b38_05z, b39_02z): a comma list of long hyphenated names broke inside a name across RTL
        // line wraps. Each name is its own LTR line, spelled exactly (no hyphen substitution), one header per kind.
        var lines = line.Split('\n');
        lines.Should().Contain("3 XREF לא טעונים בשרטוט:").And.Contain("1 XREF לא פתורים/לא נטענו מהנתיב השמור:");
        foreach (var name in new[] { "HW-MAAR-EV-CD", "SR-Eilat_TH-MHD", "TR-Rampa-M30", "MISSING-BG" })
            lines.Should().ContainSingle(l => l == "• " + Bidi.Ltr(name));
        lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)).Should().OnlyContain(l => !l.Contains(", "));
        line.Should().NotContain("‑", "U+2011 would change the name the engineer copies");
    }

    [Fact]
    public void The_table_summary_counts_without_repeating_the_name_list()
    {
        var blockers = new List<DeliveryFinding> { F("TR-Rampa-M30", true), F("HW-MAAR-EV-CD", true), F("MISSING-BG", false) };
        var text = SectionPlanBlockerSummaryLogic.Describe(blockers, id => id, anyRecordReady: false,
            xrefNamesShownElsewhere: true);
        text.Should().Contain("2 XREF לא טעונים בשרטוט ו-1 XREF לא פתורים/לא נטענו מהנתיב השמור")
            .And.Contain("בכרטיס הפעולה ובפרטי החסימה")
            .And.NotContain("TR-Rampa-M30").And.NotContain("\n", "one short line above the table");
        SectionPlanBlockerSummaryLogic.Describe(blockers, id => id, anyRecordReady: false)
            .Should().Contain("• " + Bidi.Ltr("TR-Rampa-M30"), "without the flag the full list is kept");
    }

    [Fact]
    public void A_long_list_names_the_first_eight_and_counts_the_rest()
    {
        var findings = Enumerable.Range(1, 11).Select(i => F($"BG-{i:00}", true)).ToList();
        var lines = XrefAvailabilityText.Summarize(findings)!.Split('\n');
        lines[0].Should().Be("11 XREF לא טעונים בשרטוט:");
        lines.Count(l => l.StartsWith("• ", StringComparison.Ordinal) && l.Contains("BG-")).Should().Be(8);
        lines.Should().Contain("• ועוד 3");
    }

    [Fact]
    public void A_multi_line_reason_keeps_the_policy_sentence_on_its_own_line()
    {
        var reason = XrefAvailabilityText.Summarize(new[] { F("TR-Rampa-M30", true) })!;
        var action = SectionGuidedActionPolicy.Evaluate(new SectionGuidedActionSnapshot
        {
            HasDrawing = true, ProfileUsable = true, HasPlan = true, PlanRecordCount = 28,
            GlobalPlanningBlockReason = reason,
        });
        action.Detail.Should().StartWith(reason + "\nאין כרגע אפשרות ליצור או לאמת חתכים.");
    }

    [Fact]
    public void Other_findings_are_not_parsed_as_XREF_availability()
    {
        XrefAvailabilityText.TryParse(new DeliveryFinding
        {
            Code = SectionFindingCodes.XrefTraversalUnresolved, Domain = "sections", Severity = FindingSeverity.Error,
            Title = "ישות בתוך Block/XREF אינה ניתנת לקריאה", Message = "definition=A; handle_path=1/2",
        }, out _).Should().BeFalse("a read failure is not an unloaded XREF and keeps its own title");
        XrefAvailabilityText.Summarize(new[] { new DeliveryFinding
        {
            Code = "SEC-OTHER", Domain = "sections", Severity = FindingSeverity.Error, Title = "x",
            Message = "xref=A; state=unloaded; path=p",
        } }).Should().BeNull();
    }

    [Fact]
    public void The_blocker_summary_uses_the_grouped_line_once()
    {
        var blockers = new List<DeliveryFinding> { F("TR-Rampa-M30", true), F("TR-Rampa-M30-new", true), F("TR-Rampa-M30", true) };
        var text = SectionPlanBlockerSummaryLogic.Describe(blockers, id => id, anyRecordReady: false);
        text.Should().Contain("2 XREF לא טעונים").And.NotContain("לא ניתן להוכיח חצי כיוון נסיעה",
            "per-insertion titles are replaced by the grouped line");
    }
}
