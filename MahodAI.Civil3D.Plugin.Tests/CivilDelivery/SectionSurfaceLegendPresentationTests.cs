using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class SectionSurfaceLegendPresentationTests
{
    private static SectionSourcePlan Source(string name, bool required = true, string state = "sampled", string type = "surface") => new()
    {
        SourceName = name, Required = required, PlannedState = state, SourceType = type
    };

    [Fact]
    public void Actual12145PairExplainsDesignAndExisting_NotDatumOrStatus()
    {
        var text = SectionSurfaceLegendPresentation.DescribePlannedSources(new[] { Source("MK"), Source("600-DESIGN-FINAL") });
        Assert.Contains("מצב קיים (ירוק מקווקו): MK", text);
        Assert.Contains("משטח תכנון (אדום רציף): 600-DESIGN-FINAL", text);
        Assert.Contains("זהו שיוך בתכנון", text);
        Assert.Contains("האדום אינו רום הייחוס", SectionSurfaceLegendPresentation.Legend);
        Assert.Contains("לא לצבעי סטטוס", SectionSurfaceLegendPresentation.Help);
    }

    [Fact]
    public void KeepsExplicitSourceOrderInsteadOfGuessingFromNames()
    {
        var text = SectionSurfaceLegendPresentation.DescribePlannedSources(new[] {
            Source("NOT-SELECTED", false), Source("EG-reviewed"), Source("FG-reviewed"), Source("CORRIDOR", type: "corridor") });
        Assert.Contains("מקווקו): EG-reviewed", text);
        Assert.Contains("רציף): FG-reviewed", text);
        Assert.DoesNotContain("NOT-SELECTED", text);
        Assert.DoesNotContain("CORRIDOR", text);
    }

    [Fact]
    public void MissingDuplicateOrUnsupportedPairDoesNotInventLegendSources()
    {
        foreach (var sources in new[] {
            Array.Empty<SectionSourcePlan>(), new[] { Source("MK") },
            new[] { Source("MK"), Source("mk") }, new[] { Source("MK"), Source("FG", state: "unsupported") },
            new[] { Source("MK"), Source("FG"), Source("THIRD") } })
            Assert.Contains("טרם נקבע זוג תקין", SectionSurfaceLegendPresentation.DescribePlannedSources(sources));
    }

    [Fact]
    public void LegendIsVisibleOutsideCollapsedDiagnosticsAndUsesWrapping()
    {
        var dir = typeof(SectionSurfaceLegendPresentationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "MahodPluginSourceDir").Value!;
        var xaml = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        var code = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
        Assert.Contains("x:Name=\"SectionSurfaceLegend\" TextWrapping=\"Wrap\"", xaml);
        Assert.True(xaml.IndexOf("x:Name=\"SectionSurfaceLegend\"", StringComparison.Ordinal) <
                    xaml.IndexOf("x:Name=\"SectionDiagnostics\"", StringComparison.Ordinal));
        Assert.Contains("SectionSurfaceLegend.Text = SectionSurfaceLegendPresentation.Legend", code);
        Assert.Contains("DescribePlannedSources(row.Record.PlannedSources)", code);
    }
}
