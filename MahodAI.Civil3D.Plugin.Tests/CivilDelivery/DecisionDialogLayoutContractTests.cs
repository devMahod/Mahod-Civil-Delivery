using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Source contracts complement, but do not replace, offscreen visual evidence.</summary>
public sealed class DecisionDialogLayoutContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Source(string name)
    {
        var root = typeof(DecisionDialogLayoutContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        return File.ReadAllText(Path.Combine(root, "CivilDelivery", "UI", name));
    }
    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("SectionSpanLabelDecisionDialog")]
    [InlineData("TrafficDirectionDecisionDialog")]
    [InlineData("CatalogPickerDialog")]
    [InlineData("ProvenMappingBatchDecisionDialog")]
    public void FixedColumnsCannotCollapseAndDisabledButtonsKeepTheDarkTheme(string name)
    {
        var document = XDocument.Parse(Source(name + ".xaml"));
        var columns = document.Descendants().Where(e => e.Name.LocalName is "DataGridTextColumn" or "DataGridTemplateColumn");
        foreach (var column in columns)
        {
            var width = (string?)column.Attribute("Width") ?? "";
            var minimum = double.Parse((string)column.Attribute("MinWidth")!, CultureInfo.InvariantCulture);
            minimum.Should().BeGreaterThan(0);
            if (double.TryParse(width, NumberStyles.Number, CultureInfo.InvariantCulture, out var fixedWidth))
                minimum.Should().BeGreaterThanOrEqualTo(fixedWidth);
        }
        var buttonStyle = document.Descendants().Single(e => e.Name.LocalName == "Style" &&
            (string?)e.Attribute("TargetType") == "Button" && e.Attribute(X + "Key") == null);
        buttonStyle.Descendants().Should().Contain(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "Button");
        buttonStyle.Descendants().Should().Contain(e => e.Name.LocalName == "Trigger" &&
            (string?)e.Attribute("Property") == "IsEnabled" && (string?)e.Attribute("Value") == "False");
        document.Descendants().Single(e => e.Name.LocalName == "DataGrid")
            .Attribute("ScrollViewer.HorizontalScrollBarVisibility")!.Value.Should().Be("Auto");
    }

    [Fact]
    public void TrafficChoicesAreReadableWithoutProvidingADefaultOrRemovingInputHandlers()
    {
        var document = XDocument.Parse(Source("TrafficDirectionDecisionDialog.xaml"));
        foreach (var type in new[] { "ComboBox", "ComboBoxItem" })
        {
            var style = document.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == type);
            style.Elements().Should().Contain(e => (string?)e.Attribute("Property") == "Foreground" && (string?)e.Attribute("Value") == "#FF141821");
        }
        var choices = document.Descendants().Where(e => e.Name.LocalName == "ComboBox").ToArray();
        choices.Should().OnlyContain(e => e.Attribute("SelectedIndex") == null);
        choices.Single(e => e.Attribute("SelectedItem") != null).Attribute("SelectedItem")!.Value
            .Should().Be("{Binding SelectedChoice, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}");
        Named(document, "BtnSave").Attribute("Click")!.Value.Should().Be("OnSave");
        Named(document, "ApproverBox").Attribute("TextChanged")!.Value.Should().Be("OnInputChanged");
    }

    [Fact]
    public void ProvenMappingRetainsExplicitReadableConfirmationAndBothChangeHandlers()
    {
        var document = XDocument.Parse(Source("ProvenMappingBatchDecisionDialog.xaml"));
        var confirmation = Named(document, "ConfirmBox");
        confirmation.Attribute("Foreground")!.Value.Should().Be("#FFE6E9EF");
        confirmation.Attribute("IsChecked").Should().BeNull();
        confirmation.Attribute("Checked")!.Value.Should().Be("OnInputChanged");
        confirmation.Attribute("Unchecked")!.Value.Should().Be("OnInputChanged");
        confirmation.Descendants().Single().Attribute("Text")!.Value.Should().Contain("בדקתי את כל השורות").And.Contain("מאשר במפורש");
        Named(document, "BtnSave").Attribute("Click")!.Value.Should().Be("OnSave");
    }

    [Fact]
    public void CatalogDescriptionsAndValidationWrapWithoutEnablingApproval()
    {
        var document = XDocument.Parse(Source("CatalogPickerDialog.xaml"));
        foreach (var name in new[] { "SubjectTitle", "Hint", "UnitVerdict" })
            Named(document, name).Attribute("TextWrapping")!.Value.Should().Be("Wrap");
        document.Descendants().Should().NotContain(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "RowHeight");
        document.Descendants().Single(e => (string?)e.Attribute("Binding") == "{Binding Description}")
            .Attribute("ElementStyle")!.Value.Should().Be("{StaticResource WrappedCellText}");
        Named(document, "BtnOk").Attribute("IsEnabled")!.Value.Should().Be("False");
        Named(document, "BtnOk").Attribute("Click")!.Value.Should().Be("OnOk");
        Source("CatalogPickerDialog.xaml.cs").Should().Contain("Bidi.Ltr(layer)").And.Contain("Bidi.Ltr(ruleKey)");
    }

    [Fact]
    public void SpanBatchControlsWrapAndApprovalBindingsRemainExplicit()
    {
        var document = XDocument.Parse(Source("SectionSpanLabelDecisionDialog.xaml"));
        document.Descendants().Should().Contain(e => e.Name.LocalName == "WrapPanel");
        document.Descendants().Single(e => e.Name.LocalName == "CheckBox").Attribute("IsChecked")!.Value
            .Should().Be("{Binding IsApproved, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}");
        Named(document, "BtnSave").Attribute("Click")!.Value.Should().Be("OnSave");
        Named(document, "ValidationText").Attribute("TextWrapping")!.Value.Should().Be("Wrap");
        double.Parse(document.Root!.Attribute("MinHeight")!.Value, CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(580);
    }
}
