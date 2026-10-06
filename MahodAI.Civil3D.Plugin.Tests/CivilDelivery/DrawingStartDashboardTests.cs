using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Actual summary + production WPF markup, not native database enumeration or SaveAs.
public sealed class DrawingStartDashboardTests
{
    private static ProjectDashboardService.Dashboard Sample(bool unreadable = false) => new(
        "Drawing1.dwg", unreadable ? -1 : 0, Array.Empty<string>(), unreadable ? -1 : 0,
        unreadable ? -1 : 0, 0, null, unreadable ? -1 : 0, Array.Empty<string>());

    [Fact]
    public void ModelClDoesNotDemandAnExternalFileOrInventACount()
    {
        var text = (Sample() with { ClInModel = true }).Summary();
        Assert.Contains("בשכבות המודל", text);
        Assert.Contains("ספירה ובדיקת מקורות בתכנון", text);
        Assert.Contains("אין צורך בקובץ CL נפרד", text);
        Assert.DoesNotContain("לא מוגדר", text);
        Assert.DoesNotContain("0 קווי חתך", text);
    }

    [Fact]
    public void UnconfiguredClOffersBothModelAndExternalFile()
    {
        var text = Sample().Summary();
        Assert.Contains("טרם הוגדרו", text);
        Assert.Contains("שכבה במודל או קובץ CL", text);
    }

    [Fact]
    public void UnreadableSourcesDoNotTurnIntoEmptyDrawingClaims()
    {
        var text = Sample(true).Summary();
        Assert.Contains("תוואים: לא ניתן לקרוא", text);
        Assert.Contains("משטחים: לא ניתן לקרוא", text);
        Assert.Contains("רשתות צנרת: לא ניתן לקרוא", text);
        Assert.Contains("חתכים של הכלי: לא ניתן להשלים", text);
        Assert.DoesNotContain("0 משטחים", text);
        Assert.DoesNotContain("אין עדיין חתכים", text);
        Assert.DoesNotContain("-1", text);
    }

    [Fact]
    public void KnownEmptyDrawingStillSaysZeroRatherThanReadFailure()
    {
        var text = Sample().Summary();
        Assert.Contains("0 משטחים", text);
        Assert.Contains("אין עדיין חתכים", text);
        Assert.DoesNotContain("לא ניתן לקרוא", text);
    }

    [Fact]
    public void OwnedSampleLineCount_DoesNotClaimPlanningRecognisesEveryOne()
    {
        // SEC-m2 (review of 1.3.9): the card said "4 חתכים של הכלי כבר קיימים —
        // תכנון יזהה אותם" while PLAN matched only 2 of the 4 owned sample lines.
        var text = (Sample() with { ExistingToolSections = 4 }).Summary();
        Assert.Contains("4 קווי דגימה של הכלי קיימים בשרטוט", text);
        Assert.Contains("טבלת התכנון מראה אילו מהם תואמים לקווי CL", text);
        Assert.DoesNotContain("תכנון יזהה אותם", text);
    }

    [Theory]
    [InlineData(28, "28 קווי חתך")]
    [InlineData(-1, "לא נמצא או לא ניתן לקרוא")]
    public void ExternalSourcePreviewKeepsNameAndReadState(int count, string expected)
    {
        var text = (Sample() with { ClSourceName = "Survey-Cuts.dwg", ClLines = count }).Summary();
        Assert.Contains("Survey-Cuts.dwg", text);
        Assert.Contains(expected, text);
    }

    private static string PluginRoot => typeof(DrawingStartDashboardTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MahodPluginSourceDir").Value!;

    [Fact]
    public void ProfilePickerRequiresSavedDocumentBeforeOpeningAndRechecksAfterReview()
    {
        var text = File.ReadAllText(Path.Combine(PluginRoot, "CivilDelivery/UI/CivilDeliveryControl.ProfileSelection.cs"));
        Assert.True(text.IndexOf("if (!identity.IsSaved)", StringComparison.Ordinal) < text.IndexOf("var picker =", StringComparison.Ordinal));
        Assert.Contains("EnsureSavedForAction(\"בחירת פרופיל קיים\", OnSelectProjectProfile,", text);
        Assert.Contains("requiresLoadedProfile: false", text);
        Assert.Contains("if (identity.Failure != null) throw", text);
        Assert.Contains("!currentIdentity.IsSaved", text);
        Assert.Contains("currentIdentity.DrawingPath", text);
        Assert.DoesNotContain("document.Database.Filename", text);
    }

    [Fact]
    public void ProfileRecoveryAfterSaveDoesNotRequireTheBrokenProfileButOtherActionsDo()
    {
        var text = File.ReadAllText(Path.Combine(PluginRoot, "CivilDelivery/UI/CivilDeliveryControl.SaveGuidance.cs"));
        Assert.Contains("bool requiresLoadedProfile = true", text);
        var start = text.IndexOf("_pendingWorkflowSaveContinuation = () =>", StringComparison.Ordinal);
        var end = text.IndexOf("continuation(this, new RoutedEventArgs());", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var body = text[start..end];
        Assert.Matches(@"if\s*\(requiresLoadedProfile\s*\|\|\s*replan\)\s*\{\s*ReloadProfile\(\);\s*if\s*\(_profile == null\) return;\s*\}", body);
        // No unconditional profile guard may follow the isolated reload branch.
        Assert.Equal(1, body.Split("if (_profile == null)", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (replan)", body);
        Assert.Contains("SavedDrawingContinuationPolicy.CanConsume(", text);
        Assert.Contains("decision != SavedDrawingContinuationDecision.Resume", text);
    }

    [Fact]
    public void CaptionsUseDocumentNameAndClearStaleTitleBeforeEarlyReturn()
    {
        var text = File.ReadAllText(Path.Combine(PluginRoot, "CivilDelivery/UI/CivilDeliveryControl.xaml.cs"));
        var start = text.IndexOf("private static string? CurrentDrawing()", StringComparison.Ordinal);
        var end = text.IndexOf("private void OnDrawingChanged()", start, StringComparison.Ordinal);
        Assert.Contains("MdiActiveDocument?.Name", text[start..end]);
        Assert.DoesNotContain("Database?.Filename", text[start..end]);
        start = text.IndexOf("private void RefreshDashboard()", StringComparison.Ordinal);
        end = text.IndexOf("private void RefreshDrawingLabel()", start, StringComparison.Ordinal);
        var dashboard = text[start..end];
        Assert.Contains("with { DrawingName = Path.GetFileName(doc.Name) }", dashboard);
        Assert.True(dashboard.IndexOf("DashboardTitle.Text =", StringComparison.Ordinal) < dashboard.IndexOf("if (doc == null || _profile == null)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(440, false)]
    [InlineData(440, true)]
    [InlineData(720, false)]
    [InlineData(720, true)]
    public void ActualDashboardTextWrapsInRealMarkup(int width, bool unreadable)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xml = XDocument.Load(Path.Combine(PluginRoot, "CivilDelivery/UI/CivilDeliveryControl.xaml"));
                foreach (var attribute in xml.Root!.DescendantsAndSelf().Attributes()
                    .Where(a => a.Name.LocalName == "Class" || a.Value.StartsWith("On", StringComparison.Ordinal)).ToArray()) attribute.Remove();
                var control = (UserControl)XamlReader.Parse(xml.ToString());
                ((TextBlock)control.FindName("ProjectLine")).Text = "TEST-ONLY — בדיקת מסך אופליין, ללא שרטוט חי";
                ((TextBlock)control.FindName("DashboardTitle")).Text = "השרטוט הזה — Drawing1.dwg";
                var body = (TextBlock)control.FindName("DashboardBody");
                body.Text = (Sample(unreadable) with { ClInModel = true }).Summary();
                control.Width = width; control.Height = 900;
                control.Measure(new Size(width, 900)); control.Arrange(new Rect(0, 0, width, 900)); control.UpdateLayout();
                Assert.Equal(TextWrapping.Wrap, body.TextWrapping);
                Assert.True(body.ActualWidth > 250 && body.ActualWidth < width);
                // WPF DesiredSize includes the element's margin; ActualHeight/Width
                // describe its content box. Compare the same boxes, then check the
                // rendered body really fits inside the complete dashboard card.
                Assert.True(body.DesiredSize.Height - body.Margin.Top - body.Margin.Bottom <= body.ActualHeight + .5,
                    $"Dashboard content clipped vertically: desired={body.DesiredSize.Height}, actual={body.ActualHeight}, margin={body.Margin}");
                Assert.True(body.DesiredSize.Width - body.Margin.Left - body.Margin.Right <= body.ActualWidth + .5,
                    "Dashboard content clipped horizontally.");
                var card = (Border)control.FindName("DashboardBorder");
                var bodyBounds = body.TransformToAncestor(card).TransformBounds(new Rect(new System.Windows.Point(), body.RenderSize));
                Assert.True(bodyBounds.Left >= 0 && bodyBounds.Right <= card.ActualWidth &&
                    bodyBounds.Top >= 0 && bodyBounds.Bottom <= card.ActualHeight, "Dashboard content escaped its card.");
                var directory = Environment.GetEnvironmentVariable("MHD_DASHBOARD_RENDER_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap(width, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = new FileStream(Path.Combine(directory, $"dashboard-{width}-{(unreadable ? "unknown" : "empty")}.png"), FileMode.CreateNew);
                    encoder.Save(output);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF render timed out.");
        if (failure != null) throw new InvalidOperationException("Dashboard rendering failed.", failure);
    }
}
