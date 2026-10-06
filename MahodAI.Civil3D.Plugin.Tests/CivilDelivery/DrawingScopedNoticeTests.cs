using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The export notice under the estimate buttons belongs to the drawing whose command wrote it (live WP4, 01.10: a 6422
/// rules-bill notice stayed in another project's palette, also after reopening; Codex 00:22: a corridor export runs without
/// a scan, so a scan-based reset missed it). The file and the saved decisions are never touched — only the notice.
/// </summary>
public sealed class DrawingScopedNoticeTests
{
    private const string A = @"C:\P\6422-CIVIL-WEST.dwg";
    private const string B = @"C:\Q\SM-generic.dwg";

    [Fact]
    public void ANoticeSurvivesInItsOwnDrawingAndIsClearedByTheNextOne_AToBToA()
    {
        var notice = new DrawingScopedNotice();
        notice.Set("נוצר כתב כמויות מקורידורים … A.xlsx", A);
        notice.ClearUnlessOwnedBy(A).Should().BeFalse("the owner is still active");
        notice.ClearUnlessOwnedBy(A.ToUpperInvariant()).Should().BeFalse("drawing identity is case-insensitive like the rest");
        notice.Text.Should().Contain("A.xlsx");

        notice.ClearUnlessOwnedBy(B).Should().BeTrue();
        notice.Text.Should().BeEmpty();
        notice.OwnerDrawing.Should().BeNull();

        notice.ClearUnlessOwnedBy(A).Should().BeFalse("nothing to clear");
        notice.Text.Should().BeEmpty("returning to A does not bring the old notice back");
    }

    [Fact]
    public void TwoDrawingsOfTheSameProfileAreStillTwoOwners()
    {
        // 6422-CIVIL-WEST and its PD copy share profile 6422; a notice of one is not a result of the other.
        var notice = new DrawingScopedNotice();
        notice.Set("נוצר כתב כמויות לפי כללים … WEST", @"C:\6422\PD\6422-CIVIL-WEST.dwg");
        notice.ClearUnlessOwnedBy(@"C:\6422\PD\6422-CIVIL-WEST.claude-live.dwg").Should().BeTrue();
        notice.Text.Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownOwnerOrActiveDrawingNeverKeepsTheNotice()
    {
        var unsaved = new DrawingScopedNotice();
        unsaved.Set("טיוטת המדידות לא פורסמה: …", null);
        unsaved.ClearUnlessOwnedBy(A).Should().BeTrue();

        var noDocument = new DrawingScopedNotice();
        noDocument.Set("נוצר …", A);
        noDocument.ClearUnlessOwnedBy(null).Should().BeTrue("no active drawing proves ownership");
    }

    [Fact]
    public void AFailureReplacesAnEarlierSuccessAndAProfileChangeClears()
    {
        var notice = new DrawingScopedNotice();
        notice.Set("נוצר כתב כמויות לפי כללים … ok.xlsx", A);
        notice.Set("כתב הכמויות לפי כללים לא נוצר: הסריקה התחלפה", A);
        notice.Text.Should().NotContain("ok.xlsx").And.StartWith("כתב הכמויות לפי כללים לא נוצר");
        notice.ClearUnlessOwnedBy(A).Should().BeFalse("the failure is this drawing's current result");

        notice.Clear(); // explicit profile selection
        notice.Text.Should().BeEmpty();
        notice.OwnerDrawing.Should().BeNull();

        notice.Set(string.Empty, A);
        notice.OwnerDrawing.Should().BeNull("an empty notice has no owner");
    }

    [Fact]
    public void ClosingTheOwnerClearsIt_ClosingAnotherDrawingDoesNot()
    {
        var notice = new DrawingScopedNotice();
        notice.Set("נוצר כתב כמויות מקורידורים … A.xlsx", A);
        notice.ClearIfOwnedBy(B).Should().BeFalse("closing another drawing never touches a valid notice");
        notice.ClearIfOwnedBy(null).Should().BeFalse();
        notice.Text.Should().Contain("A.xlsx");
        notice.ClearIfOwnedBy(A.ToLowerInvariant()).Should().BeTrue();
        notice.Text.Should().BeEmpty();
        notice.ClearUnlessOwnedBy(A).Should().BeFalse("reopening the same path shows nothing old");
    }

    [Fact]
    public void EveryProducerWritesThroughTheScopedNoticeAndEverySwitchClearsIt()
    {
        var files = Directory.GetFiles(UiDirectory(), "CivilDeliveryControl*.cs");
        var assignments = files.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"MeasurementDraftNotice\.Text\s*=\s*([^;]+);")
            .Select(m => (File: Path.GetFileName(f), Value: m.Groups[1].Value.Trim()))).ToList();
        assignments.Should().OnlyContain(a => a.Value.TrimEnd(')') == "_exportNotice.Text" || a.Value.TrimEnd(')') == "string.Empty",
            "results and failures go through SetExportNotice, which records the owning drawing");
        foreach (var producer in new[] { "BoqRules", "CorridorBoq", "MeasurementDraft", "PricedDraft" })
            File.ReadAllText(Path.Combine(UiDirectory(), $"CivilDeliveryControl.{producer}.cs"))
                .Should().Contain("SetExportNotice(", producer);

        var control = File.ReadAllText(Path.Combine(UiDirectory(), "CivilDeliveryControl.xaml.cs"));
        var drawingChanged = control.Substring(control.IndexOf("private void OnDrawingChanged()", System.StringComparison.Ordinal));
        drawingChanged = drawingChanged.Substring(0, drawingChanged.IndexOf("RefreshDrawingLabel();", System.StringComparison.Ordinal));
        // Outside the scan-based `if (estimateChanged)` block: a corridor export has no scan and no results drawing.
        var clear = drawingChanged.IndexOf("_exportNotice.ClearUnlessOwnedBy(estimateNow)", System.StringComparison.Ordinal);
        clear.Should().BeGreaterThan(0);
        var estimateBlock = drawingChanged.IndexOf("if (estimateChanged)", System.StringComparison.Ordinal);
        var estimateBlockEnd = drawingChanged.IndexOf("QuantityDetail.Text = \"השרטוט הוחלף", estimateBlock, System.StringComparison.Ordinal);
        clear.Should().BeGreaterThan(estimateBlockEnd, "the notice reset does not depend on a scan");

        // Codex 00:45: closing the owning drawing clears the notice (close-last → reopen the same path starts clean).
        var lifecycle = File.ReadAllText(Path.Combine(UiDirectory(), "CivilDeliveryControl.DrawingSaveContext.cs"));
        var destroying = lifecycle.Substring(lifecycle.IndexOf("private void OnObservedDocumentDestroying(", System.StringComparison.Ordinal));
        destroying.Substring(0, destroying.IndexOf("ClearPreviewBeforeDocumentTransition(e.Document);", System.StringComparison.Ordinal) + 120)
            .Should().Contain("ClearExportNoticeOf(e.Document);");
        lifecycle.Should().Contain("if (!_exportNotice.ClearIfOwnedBy(identity)) return;");

        var profileReset = control.Substring(control.IndexOf("private bool ResetForExplicitProjectProfileSelection()", System.StringComparison.Ordinal));
        profileReset.Substring(0, profileReset.IndexOf("return true;", System.StringComparison.Ordinal))
            .Should().Contain("_exportNotice.Clear();");
    }

    private static string UiDirectory() => Path.Combine(typeof(DrawingScopedNoticeTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!, "CivilDelivery", "UI");
}
