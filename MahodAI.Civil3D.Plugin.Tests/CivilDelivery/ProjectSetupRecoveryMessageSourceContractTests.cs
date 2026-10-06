using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Pins the no-CL recovery guidance and its real palette button labels without
/// loading AutoCAD. This is a source/message contract, not a native setup test.
/// </summary>
public sealed class ProjectSetupRecoveryMessageSourceContractTests
{
    private static string PluginSourceDir =>
        typeof(ProjectSetupRecoveryMessageSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string RecoveryBranch()
    {
        var source = File.ReadAllText(Path.Combine(PluginSourceDir,
            "CivilDelivery", "Commands", "MhdSetupCommand.cs"));
        var start = source.IndexOf("if (scan.ClLayerCandidates.Count == 0)",
            StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = source.IndexOf("return false;", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return source[start..(end + "return false;".Length)];
    }

    [Fact]
    public void NoClGuidance_KeepsCivilHostAndOffersExternalPickerOrExistingHostXref()
    {
        var recovery = RecoveryBranch();
        recovery.Should().Contain("פתח את מודל Civil שמכיל את התוואים ומקורות החתך")
            .And.Contain("או הישאר בו אם הוא כבר פתוח")
            .And.Contain("בחר את הקובץ ואשר את שכבת ה-CL")
            .And.Contain("אין צורך לפתוח את קובץ ה-CL לבדו או לצרף אותו כ-XREF")
            .And.Contain("במודל עצמו או ב-XREF שכבר מצורף אליו")
            .And.Contain("ודא שהם זמינים וחוצים תוואי")
            .And.Contain("(\" + CivilDeliveryCommandNames.Setup + \") באותו מודל")
            .And.Contain("ed.WriteMessage(")
            .And.Contain("return false;")
            .And.NotContain("פתח את שרטוט ה-CL (או צרף אותו כ-XREF)")
            .And.NotContain("Save(")
            .And.NotContain("SendStringToExecute(");
    }

    [Theory]
    [InlineData("BtnPickCl", "OnPickCl")]
    [InlineData("BtnPlan", "OnPlan")]
    [InlineData("BtnSetup", "OnSetup")]
    public void NoClGuidance_UsesExistingPaletteButtonLabels(string name, string handler)
    {
        var xaml = XDocument.Load(Path.Combine(PluginSourceDir,
            "CivilDelivery", "UI", "CivilDeliveryControl.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = xaml.Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute(x + "Name") == name);
        var label = (string?)button.Attribute("Content");
        label.Should().NotBeNullOrWhiteSpace();
        ((string?)button.Attribute("Click")).Should().Be(handler);
        RecoveryBranch().Should().Contain("'" + label + "'");
    }
}
