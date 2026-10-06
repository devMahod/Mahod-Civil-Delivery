using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using TextBlock = System.Windows.Controls.TextBlock;
using Binding = System.Windows.Data.Binding;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantityProposalDescriptionTests
{
    private static QuantityRowViewModel Row() => new()
    {
        RuleKey = "TEST-ONLY", Layer = "TEST-SIMUN", EntityType = "POLYLINE", Method = "length",
        ObjectCount = 2, Quantity = 206.7, Unit = "מטר", MappingState = "דרוש שיוך", ProposedCode = "TEST.2",
    };

    private static CatalogSnapshot Book(string description, string id = "TEST-ONLY") => new()
    {
        SnapshotId = id, FileHash = new string(id == "TEST-ONLY" ? 'a' : 'b', 64),
        Items = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TEST.1"] = new() { Code = "TEST.1", Description = "תיאור אחר — לא ההצעה המוצגת", UnitRaw = "מטר" },
            ["TEST.2"] = new() { Code = "TEST.2", Description = description, UnitRaw = "מטר" },
        },
    };

    [Fact]
    public void VisibleDescriptionUsesExactProposedCodeFromActiveBookWithoutApproval()
    {
        var row = Row();
        row.ProposalSearchText = "TEST.1 · תיאור אחר\nTEST.2 · תיאור ישן";
        row.RefreshProposalDescription(Book("סימון לדוגמה — תיאור בדיקה בלבד"));
        Assert.Equal("סימון לדוגמה — תיאור בדיקה בלבד", row.CatalogDescriptionDisplay);
        Assert.StartsWith("הצעה:", row.CatalogCodeDisplay);
        Assert.Contains("לא שיוך מאושר", row.CatalogCodeDetail);
        Assert.Null(row.CatalogCode); Assert.Null(row.CatalogDescription); Assert.Null(row.Price);
        Assert.Equal("דרוש שיוך", row.MappingState); Assert.Equal(206.7, row.Quantity); Assert.Equal(2, row.ObjectCount);
    }

    [Fact]
    public void SwitchingBookOrClearingItNeverRetainsAnOldProposalDescription()
    {
        var row = Row(); var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.RefreshProposalDescription(Book("ישן"));
        row.RefreshProposalDescription(Book("חדש", "TEST-SECOND"));
        Assert.Equal("חדש", row.CatalogDescriptionDisplay);
        row.RefreshProposalDescription(null);
        Assert.Equal("חסר תיאור להצעה במחירון הפעיל", row.CatalogDescriptionDisplay);
        Assert.Equal(3, changed.Count(n => n == nameof(row.CatalogDescriptionDisplay)));
        row.RefreshProposalDescription(null);
        Assert.Equal(3, changed.Count(n => n == nameof(row.CatalogDescriptionDisplay)));
        Assert.Null(row.CatalogCode);
    }

    [Fact]
    public void MissingOrChangedCodeAndGovernedRowsDoNotBorrowAnotherItemsDescription()
    {
        var row = Row(); row.RefreshProposalDescription(Book("הצעה מקורית"));
        row.ProposedCode = "TEST.MISSING";
        Assert.Equal("חסר תיאור להצעה במחירון הפעיל", row.CatalogDescriptionDisplay);
        row.RefreshProposalDescription(Book("לא לשאול את התיאור הזה"));
        Assert.Equal("חסר תיאור להצעה במחירון הפעיל", row.CatalogDescriptionDisplay);
        row.ProjectRuleGoverned = true;
        Assert.Equal("טרם אושר סעיף", row.CatalogDescriptionDisplay);
        row.ProjectRuleGoverned = false; row.ProposedCode = null;
        Assert.Equal("טרם אושר סעיף", row.CatalogDescriptionDisplay);
        row.CatalogCode = "TEST.APPROVED"; row.CatalogDescription = "תיאור השיוך הקיים";
        Assert.Equal("תיאור השיוך הקיים", row.CatalogDescriptionDisplay);
    }

    [Fact]
    public void BoundWpfDescriptionRefreshesWhileProposalRemainsUnapproved()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var row = Row(); var text = new TextBlock { DataContext = row };
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(row.CatalogDescriptionDisplay)));
                row.RefreshProposalDescription(Book("תיאור מחובר"));
                Assert.Equal("תיאור מחובר", text.Text);
                row.RefreshProposalDescription(Book("תיאור לאחר החלפת ספר", "TEST-SECOND"));
                Assert.Equal("תיאור לאחר החלפת ספר", text.Text);
                Assert.Null(row.CatalogCode); Assert.Equal("דרוש שיוך", row.MappingState);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error != null) throw new InvalidOperationException("WPF proposal description binding failed", error);
    }

    [Fact]
    public void ActualPaletteRefreshWiresCurrentCatalogAndKeepsFullTooltipWithVisibleEllipsis()
    {
        var dir = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI");
        var code = File.ReadAllText(Path.Combine(dir, "CivilDeliveryControl.EstimateWorklist.cs"));
        Assert.Contains("row.RefreshProposalDescription(_catalog);", code);
        var xaml = File.ReadAllText(Path.Combine(dir, "CivilDeliveryControl.xaml"));
        Assert.Contains("ToolTip=\"{Binding CatalogCodeDetail}\"", xaml);
        Assert.Contains("Text=\"{Binding CatalogDescriptionDisplay}\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\"", xaml);
    }
}
