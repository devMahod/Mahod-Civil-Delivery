using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using Button = System.Windows.Controls.Button;
using Size = System.Windows.Size;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The typical-sections popup (2026-10), unshown: the review keeps its count, title and button in step as marks are
/// removed; the result and blocked states show exactly their own parts. The production XAML and code-behind are
/// compiled into this test assembly (see the csproj), so this is the real window, not a copy.
/// </summary>
public sealed class TypicalSectionsDialogInteractionTests
{
    private static TypicalSectionsDialog.PickRow Pick(int number, double station) => new()
    {
        Number = number, Station = station.ToString("0.00"), Alignment = "Road34", LineHandle = "A" + number,
    };

    [Fact]
    public void Review_RemovingAMark_UpdatesTitleCountAndCreateButton_AndRemembersTheMarker() => RunSta(() =>
    {
        var dialog = TypicalSectionsDialog.ForReview(new[] { Pick(1, 120.5), Pick(2, 240), Pick(3, 402.8) },
            "Road34 · חצי רוחב 20 מ'", 20);
        var title = (TextBlock)dialog.FindName("HeadTitle");
        var create = (Button)dialog.FindName("BtnCreate");
        var count = (TextBlock)dialog.FindName("StatValue1");
        title.Text.Should().Be("3 חתכים מוכנים ליצירה");
        create.Content.Should().Be("צור 3 חתכים");
        ((TextBlock)dialog.FindName("StatValue3")).Text.Should().Be("40 מ'");

        Layout(dialog);
        var remove = Descendants((DependencyObject)dialog.FindName("PickList")).OfType<Button>().First();
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        dialog.RemovedLineHandles.Should().Equal("A1");
        dialog.RemainingPicks.Select(p => p.LineHandle).Should().Equal("A2", "A3");
        title.Text.Should().Be("2 חתכים מוכנים ליצירה");
        create.Content.Should().Be("צור 2 חתכים");
        count.Text.Should().Be("2");
        create.IsEnabled.Should().BeTrue();

        foreach (var button in Descendants((DependencyObject)dialog.FindName("PickList")).OfType<Button>().ToList())
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        title.Text.Should().Be("אין חתכים מסומנים");
        create.IsEnabled.Should().BeFalse("nothing is left to create");
        dialog.Close();
    });

    [Fact]
    public void Result_ShowsTheSectionsAndTheirNumbers_AndRaisesShowForACreatedSection() => RunSta(() =>
    {
        var rows = new[]
        {
            new TypicalSectionsDialog.SectionRow { Title = "Road34 - 7 (120.50)", Detail = "6 מתוך 6 רוחבים עם שם", Good = true, BlockHandle = "3F2" },
            new TypicalSectionsDialog.SectionRow { Title = "Road34 (470.00)", Detail = "תחנה מחוץ לאזורי הקורידור", Good = false },
        };
        var dialog = TypicalSectionsDialog.ForResult(1, 6, 0, rows, "Road34", new[] { "120.50: הערה" }, stoppedEarly: true);
        ((TextBlock)dialog.FindName("HeadTitle")).Text.Should().Be("נוצר חתך אחד");
        ((UIElement)dialog.FindName("PickList")).Visibility.Should().Be(Visibility.Collapsed);
        ((UIElement)dialog.FindName("SectionList")).Visibility.Should().Be(Visibility.Visible);
        ((UIElement)dialog.FindName("BtnCreate")).Visibility.Should().Be(Visibility.Collapsed);
        ((UIElement)dialog.FindName("NotesExpander")).Visibility.Should().Be(Visibility.Visible);
        ((TextBlock)dialog.FindName("StatLabel2")).Text.Should().Be("רצועות עם שם");

        Layout(dialog);
        var shows = Descendants((DependencyObject)dialog.FindName("SectionList")).OfType<Button>()
            .Where(b => b.Visibility == Visibility.Visible).ToList();
        shows.Should().ContainSingle("only a created section can be shown");
        string? asked = null;
        dialog.ShowSectionRequested += handle => asked = handle;
        shows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        asked.Should().Be("3F2");
        dialog.Close();
    });

    [Fact]
    public void Blocked_ListsWhatIsMissing_WithoutNumbersOrCreate() => RunSta(() =>
    {
        var dialog = TypicalSectionsDialog.ForBlocked(new[] { "לתוואי Road34 אין קורידור" }, "Road34");
        ((TextBlock)dialog.FindName("HeadTitle")).Text.Should().Be("החתכים לא נוצרו");
        ((UIElement)dialog.FindName("Stats")).Visibility.Should().Be(Visibility.Collapsed);
        ((UIElement)dialog.FindName("BlockerList")).Visibility.Should().Be(Visibility.Visible);
        ((UIElement)dialog.FindName("BtnCreate")).Visibility.Should().Be(Visibility.Collapsed);
        ((UIElement)dialog.FindName("BtnAddMore")).Visibility.Should().Be(Visibility.Collapsed);
        ((Button)dialog.FindName("BtnClose")).Content.Should().Be("סגור");
        dialog.Close();
    });

    [Fact]
    public void Blocked_WithAMendableCorridor_OffersFixAndRetry_AndClosesWithFix() => RunSta(() =>
    {
        // Live 2026-10-28, 6403: RD-40 not rebuilt, RAMPA-15 without a corridor surface — the tool mends both.
        var dialog = TypicalSectionsDialog.ForBlocked(
            new[] { "הקורידור RD-40 אינו מעובד (Out of date) — צריך לבנות אותו מחדש (Rebuild)." },
            "ROAD-40, RAMP-15", "תקן ונסה שוב");
        var fix = (Button)dialog.FindName("BtnCreate");
        fix.Visibility.Should().Be(Visibility.Visible);
        fix.Content.Should().Be("תקן ונסה שוב");
        ((Button)dialog.FindName("BtnClose")).Content.Should().Be("סגור");

        Layout(dialog);
        fix.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.Choice.Should().Be(TypicalSectionsDialog.Outcome.Fix);
    });

    private static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var size = new Size(window.Width, 640);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
        if (failure != null) throw new InvalidOperationException("Unshown typical-sections popup test failed.", failure);
    }
}
