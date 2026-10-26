using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Rubik, the Mahod house font (2026-10), ships as files in a Fonts folder beside the plugin DLL, so every engineer sees
/// it without installing anything. These pin the three halves of that: the files are in the source tree with their
/// licence, every window names the font, and the built plugin carries the folder where CivilDeliveryTheme.Font looks.
/// </summary>
public sealed class ThemeFontTests
{
    private static readonly string FontsDir = Path.Combine(TestXaml.UiDirectory, "Fonts");

    [Fact]
    public void TheFourWeightsAndTheLicenceAreInTheSourceTree()
    {
        foreach (var file in new[] { "Rubik-Regular.ttf", "Rubik-Medium.ttf", "Rubik-SemiBold.ttf", "Rubik-Bold.ttf", "OFL.txt" })
            File.Exists(Path.Combine(FontsDir, file)).Should().BeTrue(file);
        File.ReadAllText(Path.Combine(FontsDir, "OFL.txt")).Should().Contain("SIL Open Font License");
        var families = Fonts.GetFontFamilies(new Uri(FontsDir + Path.DirectorySeparatorChar));
        var rubik = families.Single(f => f.Source.EndsWith("#Rubik", System.StringComparison.Ordinal));
        rubik.FamilyTypefaces.Select(t => t.Weight).Should().Contain(new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold });
    }

    [Fact]
    public void EveryWindowNamesTheFontInMarkupAndTakesTheRuntimeFamilyInCode()
    {
        foreach (var path in Directory.EnumerateFiles(TestXaml.UiDirectory, "*.xaml"))
        {
            var markup = File.ReadAllText(path);
            var isWindow = File.ReadLines(path).Take(3).Any(line => line.TrimStart().StartsWith("<Window", System.StringComparison.Ordinal) ||
                                                                      line.TrimStart().StartsWith("<UserControl", System.StringComparison.Ordinal));
            if (!isWindow) continue;
            markup.Should().Contain("FontFamily=\"./Fonts/#Rubik, Segoe UI, Arial\"", Path.GetFileName(path));
            var code = File.ReadAllText(path + ".cs");
            code.IndexOf("FontFamily = CivilDeliveryTheme.Font;", System.StringComparison.Ordinal)
                .Should().BeGreaterThan(code.IndexOf("InitializeComponent();", System.StringComparison.Ordinal), Path.GetFileName(path) + ".cs");
        }
        File.ReadAllText(Path.Combine(TestXaml.UiDirectory, "CivilDeliveryTheme.xaml"))
            .Should().Contain("<FontFamily x:Key=\"Font\">./Fonts/#Rubik, Segoe UI, Arial</FontFamily>");
    }

    [Fact]
    public void TheBuiltPluginShipsTheFontsFolderAndTheCodeResolvesRubikFromIt()
    {
        var beside = Path.Combine(Path.GetDirectoryName(typeof(CivilDeliveryTheme).Assembly.Location)!, "Fonts");
        foreach (var file in new[] { "Rubik-Regular.ttf", "Rubik-Medium.ttf", "Rubik-SemiBold.ttf", "Rubik-Bold.ttf", "OFL.txt" })
            File.Exists(Path.Combine(beside, file)).Should().BeTrue(file + " must be copied beside the plugin (Content items in the .csproj)");
        CivilDeliveryTheme.FontsFolder.Should().Be(beside);
        CivilDeliveryTheme.Font.Source.Should().Be("./#Rubik, Segoe UI, Arial");
        foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
        {
            var typeface = new Typeface(CivilDeliveryTheme.Font, FontStyles.Normal, weight, FontStretches.Normal);
            typeface.TryGetGlyphTypeface(out var glyphs).Should().BeTrue(weight.ToString());
            glyphs.FamilyNames.Values.Should().Contain("Rubik", weight.ToString());
            glyphs.Weight.Should().Be(weight);
            glyphs.CharacterToGlyphMap.ContainsKey('א').Should().BeTrue("the shipped Rubik carries Hebrew");
        }
    }
}
