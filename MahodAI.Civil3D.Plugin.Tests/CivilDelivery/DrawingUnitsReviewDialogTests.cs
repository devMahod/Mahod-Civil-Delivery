using System.Collections.Generic;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>Unshown synthetic WPF review only; no CAD, live profile or DWG is opened.</summary>
public sealed class DrawingUnitsReviewDialogTests
{
    private const string Fingerprint = "10101010-2020-3030-4040-505050505050";
    private const string Path = @"C:\SYNTHETIC-ONLY\פרויקט לבדיקת ממשק בלבד\מודל חתכים וכתב כמויות.dwg";
    private static DrawingUnitsReviewContext Context(int raw = 0, bool existing = false) => new(
        Path, Fingerprint, raw, new string('a', 64),
        "ראיה סינתטית בלבד — אין כאן אישור יחידות של שרטוט אמיתי.", existing);
    private static void Fill(DrawingUnitsReviewDialog dialog)
    {
        dialog.Reason.Text = "SYNTHETIC ONLY: reviewed a known 10 metre reference.";
        dialog.Source.Text = "SYNTHETIC TEST DOCUMENT / example 1";
        dialog.Approver.Text = "SYNTHETIC TEST REVIEWER";
    }
    private static void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void OpeningNeverPreselectsOrSavesEvenIfCurrentDeclarationExists() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(existing: true));
        try
        {
            dialog.Metres.IsChecked.Should().BeFalse(); dialog.Save.IsEnabled.Should().BeFalse();
            dialog.Save.Opacity.Should().BeLessThan(1.0, "an unavailable approval must not look active");
            dialog.Revoke.IsEnabled.Should().BeFalse(); dialog.ApprovedDeclaration.Should().BeNull();
            dialog.RevocationRequested.Should().BeFalse(); dialog.IsVisible.Should().BeFalse();
            Fill(dialog); dialog.Save.IsEnabled.Should().BeFalse();
            dialog.Metres.IsChecked = true; dialog.Save.IsEnabled.Should().BeTrue();
            dialog.ApprovedDeclaration.Should().BeNull("editing is not confirmation");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ApproverStartsEmptyAndOnlyATypedNameIsSaved() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        // b24 (b23 E4 live): the Windows account name was prefilled as the approver.
        var dialog = new DrawingUnitsReviewDialog(Context());
        try
        {
            dialog.Approver.Text.Should().BeEmpty();
            dialog.Reason.Text = "SYNTHETIC ONLY: reviewed a known 10 metre reference.";
            dialog.Source.Text = "SYNTHETIC TEST DOCUMENT / example 1";
            dialog.Metres.IsChecked = true;
            dialog.Save.IsEnabled.Should().BeFalse("nobody has typed an approver yet");
            dialog.Validation.Text.Should().Be("יש למלא את שם המאשר.");
            Click(dialog.Save); dialog.ApprovedDeclaration.Should().BeNull();
            dialog.Approver.Text = "SYNTHETIC TEST REVIEWER";
            Click(dialog.Save);
            dialog.ApprovedDeclaration.Should().NotBeNull();
            dialog.ApprovedDeclaration!.ApprovedBy.Should().Be("SYNTHETIC TEST REVIEWER");
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void CancelLeavesAnExistingDeclarationUntouchedWithAnEmptyApprover() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(existing: true));
        try
        {
            dialog.Approver.Text.Should().BeEmpty();
            dialog.Revoke.IsEnabled.Should().BeFalse("a revocation also needs a typed approver");
            Click(dialog.Cancel);
            dialog.ApprovedDeclaration.Should().BeNull(); dialog.RevocationRequested.Should().BeFalse();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("reason")]
    [InlineData("source")]
    [InlineData("approver")]
    public void EveryAuditFieldIsRequired(string missing) => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context());
        try
        {
            Fill(dialog); dialog.Metres.IsChecked = true;
            (missing == "reason" ? dialog.Reason : missing == "source" ? dialog.Source : dialog.Approver).Text = "   ";
            dialog.Save.IsEnabled.Should().BeFalse(); Click(dialog.Save);
            dialog.ApprovedDeclaration.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(6)]
    [InlineData(4)]
    [InlineData(2)]
    [InlineData(-1)]
    // b24: an explicit unit is reviewable only through its own two decisions, never the unitless metres box.
    public void ExplicitUnitsAreNotDeclaredThroughTheUnitlessMetresBox(int raw) => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(raw));
        try
        {
            Fill(dialog); dialog.Metres.IsChecked = true; Click(dialog.Save);
            dialog.Save.IsEnabled.Should().BeFalse(); dialog.Metres.IsEnabled.Should().BeFalse();
            dialog.ApprovedDeclaration.Should().BeNull(); dialog.Revoke.Visibility.Should().Be(Visibility.Collapsed);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void NoSavedHashOrNoGuidCannotAuthorizeADeclaration() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        foreach (var context in new[] { Context() with { DrawingHash = "" }, Context() with { DrawingFingerprint = "" } })
        {
            var dialog = new DrawingUnitsReviewDialog(context);
            try
            {
                Fill(dialog); dialog.Metres.IsChecked = true; Click(dialog.Save);
                dialog.Save.IsEnabled.Should().BeFalse(); dialog.ApprovedDeclaration.Should().BeNull();
            }
            finally { dialog.Close(); }
        }
    });

    [Fact]
    public void ConfirmReturnsOnlyTheReviewedHostMetreDeclaration() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context());
        Fill(dialog); dialog.Metres.IsChecked = true; Click(dialog.Save);
        var approved = dialog.ApprovedDeclaration!;
        approved.Should().NotBeNull(); approved.DrawingFingerprint.Should().Be(Fingerprint);
        approved.DrawingPath.Should().Be(PhysicalDrawingUnitPolicy.CanonicalDrawingPath(Path));
        approved.OriginalInsunitsCode.Should().Be(0); approved.PhysicalUnitCode.Should().Be(6);
        approved.MetresPerUnit.Should().Be(1); approved.Approved.Should().BeTrue();
        approved.ApprovedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        approved.ApprovedBy.Should().Be("SYNTHETIC TEST REVIEWER");
        // b25 (Codex 15:12): the reviewed unitless kind, bound to the Civil evidence of the context (none here).
        approved.DecisionKind.Should().Be(PhysicalDrawingUnitPolicy.UnitlessMetres);
        approved.CivilDrawingUnitStatus.Should().Be(PhysicalDrawingUnitPolicy.CivilUnitEvidence.NotApplicable);
        approved.CivilDrawingUnitCode.Should().BeNull();
        PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Path, new[] { approved }, isHostDrawing: false)
            .IsSupported.Should().BeFalse("host approval cannot approve an XREF");
        dialog.RevocationRequested.Should().BeFalse(); dialog.IsVisible.Should().BeFalse();
    });

    [Fact]
    public void CancelAfterEditingReturnsNoDecision() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(existing: true));
        Fill(dialog); dialog.Metres.IsChecked = true; Click(dialog.Cancel);
        dialog.ApprovedDeclaration.Should().BeNull(); dialog.RevocationRequested.Should().BeFalse();
    });

    [Fact]
    public void StaleDeclarationCanBeExplicitlyRevokedWithoutOverridingNativeMetres() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(6, existing: true));
        try
        {
            dialog.Revoke.IsEnabled.Should().BeFalse(); Fill(dialog);
            dialog.Save.IsEnabled.Should().BeFalse(); dialog.Revoke.IsEnabled.Should().BeTrue();
            Click(dialog.Revoke); dialog.RevocationRequested.Should().BeTrue(); dialog.ApprovedDeclaration.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ReplaceAndRevokePreserveOtherDrawingDeclarations()
    {
        ProjectProfile.DrawingUnitDeclaration Decision(string path, string reason) =>
            PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, path, 0,
                "SYNTHETIC TEST ONLY", DateTime.UtcNow, reason, "synthetic reference");
        var profile = new ProjectProfile();
        var other = Decision(@"C:\SYNTHETIC-ONLY\other.dwg", "other drawing");
        profile.DrawingUnitDeclarations.Add(Decision(Path, "old")); profile.DrawingUnitDeclarations.Add(other);
        DrawingUnitDeclarationReview.ApplyToClone(profile, Decision(Path, "replacement"));
        profile.DrawingUnitDeclarations.Should().HaveCount(2).And.Contain(other);
        profile.DrawingUnitDeclarations.Single(d => d != other).Reason.Should().Be("replacement");
        DrawingUnitDeclarationReview.RemoveFromClone(profile, Fingerprint, Path);
        profile.DrawingUnitDeclarations.Should().ContainSingle().Which.Should().BeSameAs(other);
    }

    // ---- b24 (E4, la-038-ew): an explicit INSUNITS is reviewable — Codex contract 10:57 / 11:14 ----

    private const string Suspicion = "SYNTHETIC: coordinates fit the ITM metre range — a suspicion only.";

    [Fact]
    public void AnExplicitUnitNeedsOneOfTwoExplicitDecisions_NoneIsPreselected() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(2) with { Suspicion = Suspicion });
        try
        {
            dialog.KeepRecorded.IsChecked.Should().BeFalse(); dialog.OtherUnit.IsChecked.Should().BeFalse();
            dialog.Metres.IsEnabled.Should().BeFalse("the unitless metres box is not the route for a recorded unit");
            Fill(dialog);
            dialog.Save.IsEnabled.Should().BeFalse("nothing was chosen");
            dialog.Validation.Text.Should().Contain("אישור היחידה הרשומה");
            dialog.OtherUnit.IsChecked = true;
            dialog.OtherUnitChoice.IsEnabled.Should().BeTrue();
            dialog.Save.IsEnabled.Should().BeFalse("no unit was picked from the list");
            Click(dialog.Save); dialog.ApprovedDeclaration.Should().BeNull();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ConfirmingTheRecordedFeetReturnsTheStandardFeetFactor() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(2) with { Suspicion = Suspicion });
        Fill(dialog); dialog.KeepRecorded.IsChecked = true;
        dialog.Validation.Text.Should().Contain("רגל (2)").And.Contain("×0.3048").And.Contain("לא ל-XREF");
        Click(dialog.Save);
        var approved = dialog.ApprovedDeclaration!;
        approved.DecisionKind.Should().Be(PhysicalDrawingUnitPolicy.ConfirmRecordedUnit);
        approved.OriginalInsunitsCode.Should().Be(2); approved.PhysicalUnitCode.Should().Be(2);
        approved.MetresPerUnit.Should().Be(0.3048); approved.ApprovedBy.Should().Be("SYNTHETIC TEST REVIEWER");
    });

    [Fact]
    public void ADifferentUnitIsPickedFromTheListWithItsStandardFactor_NeverTyped() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(2) with { Suspicion = Suspicion });
        dialog.OtherUnitChoice.Items.Cast<ComboBoxItem>().Select(i => (int)i.Tag)
            .Should().BeEquivalentTo(PhysicalDrawingUnitPolicy.DeclarableUnits.Where(code => code != 2));
        Fill(dialog); dialog.OtherUnit.IsChecked = true;
        dialog.OtherUnitChoice.SelectedItem = dialog.OtherUnitChoice.Items.Cast<ComboBoxItem>().Single(i => (int)i.Tag == 6);
        dialog.Validation.Text.Should().Contain("יחידה לחישוב: מטר").And.Contain("×1 ");
        Click(dialog.Save);
        var approved = dialog.ApprovedDeclaration!;
        approved.DecisionKind.Should().Be(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit);
        approved.OriginalInsunitsCode.Should().Be(2); approved.PhysicalUnitCode.Should().Be(6);
        approved.MetresPerUnit.Should().Be(1.0);
        ResolveLinked(2, Fingerprint, Path, new[] { approved }).AllowsMetricSections.Should().BeTrue();
    });

    [Fact]
    public void AnUnsupportedRecordedCodeCanOnlyBeReviewedAsADifferentUnit() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(-1));
        try
        {
            dialog.KeepRecorded.IsEnabled.Should().BeFalse(); dialog.OtherUnit.IsEnabled.Should().BeTrue();
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void CancellingAnExplicitUnitReviewWritesNothing() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context(2) with { Suspicion = Suspicion });
        Fill(dialog); dialog.KeepRecorded.IsChecked = true; Click(dialog.Cancel);
        dialog.ApprovedDeclaration.Should().BeNull(); dialog.RevocationRequested.Should().BeFalse();
    });

    [Fact]
    public void AReviewedExplicitUnitReplacesOnlyItsOwnHostAndKeepsItsKind()
    {
        var profile = new ProjectProfile();
        var other = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, @"C:\SYNTHETIC-ONLY\other.dwg", 0,
            "SYNTHETIC TEST ONLY", DateTime.UtcNow, "other drawing", "synthetic reference");
        profile.DrawingUnitDeclarations.Add(other);
        var decision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Path, 2,
            PhysicalDrawingUnitPolicy.DifferentPhysicalUnit, 6, "SYNTHETIC TEST ONLY", DateTime.UtcNow, "reviewed", "synthetic");
        DrawingUnitDeclarationReview.ApplyToClone(profile, decision);
        profile.DrawingUnitDeclarations.Should().HaveCount(2).And.Contain(other);
        var stored = profile.DrawingUnitDeclarations.Single(d => d != other);
        stored.DecisionKind.Should().Be(PhysicalDrawingUnitPolicy.DifferentPhysicalUnit);
        stored.PhysicalUnitCode.Should().Be(6); stored.MetresPerUnit.Should().Be(1.0);
    }

    [Fact]
    public void TheFixedUnitsLineStatesRecordedEffectiveFactorSourceAndState()
    {
        var feet = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Path, null);
        DrawingUnitDeclarationReview.Summary(feet).Should().Be(
            "הגדרת השרטוט: רגל (2); יחידה לחישוב: רגל; המרה: ×0.3048 למטר; מקור: הגדרת השרטוט; מצב: לפי מטא-דאטה.");
        var suspect = PhysicalDrawingUnitPolicy.Resolve(2, Fingerprint, Path, null,
            hostExtents: new PhysicalDrawingUnitPolicy.HostExtents(195_496, 628_759, 200_124, 630_468));
        DrawingUnitDeclarationReview.Summary(suspect).Should().Contain("לא הוכרעה").And.Contain("ללא תמחור")
            .And.Contain("נדרשת בדיקת יחידות").And.NotContain("×0.3048");
        DrawingUnitDeclarationReview.Describe(suspect).Should().Contain("ITM").And.Contain("ביחידות שרטוט");
        var metres = ResolveLinked(2, Fingerprint, Path, new[] {
            PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Path, 2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit,
                6, "SYNTHETIC", DateTime.UtcNow, "r", "s") });
        DrawingUnitDeclarationReview.Summary(metres).Should().Be(
            "הגדרת השרטוט: רגל (2); יחידה לחישוב: מטר; המרה: ×1 למטר; מקור: הכרעה מפורשת על יחידה שונה; מצב: הוכרע לשרטוט הזה.");
    }

    [Fact]
    public void AFailedRefreshAfterASuccessfulOneLeavesNoTooltipOfTheEarlierDecision()
    {
        // Codex 11:48: the catch branch replaced the text but kept the tooltip of the previous decision.
        var metres = ResolveLinked(2, Fingerprint, Path, new[] {
            PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Path, 2, PhysicalDrawingUnitPolicy.DifferentPhysicalUnit,
                6, "SYNTHETIC", DateTime.UtcNow, "r", "s") });
        var ok = DrawingUnitDeclarationReview.UnitsLine(metres, null);
        ok.ToolTip.Should().Contain("×1 למטר").And.Contain("הכרעה מפורשת");
        var failed = DrawingUnitDeclarationReview.UnitsLine(null, "SYNTHETIC READ FAILURE");
        failed.Text.Should().Contain("לא ניתן לקרוא").And.Contain("SYNTHETIC READ FAILURE");
        failed.ToolTip.Should().BeNull();
        DrawingUnitDeclarationReview.UnitsLine(null, null).Should().Be(("", (string?)null));
    }

    [Theory]
    [InlineData(430, 390)]
    [InlineData(670, 670)]
    public void ANarrowExplicitUnitReviewKeepsBothChoicesTheListAndALongValidationReachable(int width, int height) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var dialog = new DrawingUnitsReviewDialog(Context(2) with { Suspicion = Suspicion + " " + new string('ק', 120) });
            try
            {
                Fill(dialog); dialog.OtherUnit.IsChecked = true;
                dialog.OtherUnitChoice.SelectedItem = dialog.OtherUnitChoice.Items.Cast<ComboBoxItem>().Single(i => (int)i.Tag == 6);
                dialog.Validation.Text.Length.Should().BeGreaterThan(100, "the pre-approval line names raw, unit, factor and scope");
                var frame = UnshownDialogRender.Attach(dialog, width, height);
                foreach (var action in new FrameworkElement[] { dialog.Save, dialog.Cancel, dialog.Validation })
                    UnshownDialogRender.AssertWithin(action, frame);
                foreach (var choice in new FrameworkElement[] { dialog.KeepRecorded, dialog.OtherUnit, dialog.OtherUnitChoice })
                {
                    choice.BringIntoView(); frame.UpdateLayout();
                    UnshownDialogRender.AssertWithin(choice, dialog.BodyScroll);
                }
                dialog.BodyScroll.ExtentWidth.Should().BeLessThanOrEqualTo(dialog.BodyScroll.ViewportWidth + 1);
                UnshownDialogRender.Save(frame, "MHD_UNITS_UI_CAPTURE_DIR", $"units-explicit-{width}");
                dialog.Save.IsEnabled.Should().BeTrue(); dialog.ApprovedDeclaration.Should().BeNull();
            }
            finally { dialog.Close(); }
        });


    // b24 (Codex 13:02 §2): a reviewed explicit-unit decision is valid only with the review record naming its digest —
    // written together by the units review, as LinkReviews does here.
    private static PhysicalDrawingUnitPolicy.Resolution ResolveLinked(int raw, string fingerprint, string path,
        IEnumerable<ProjectProfile.DrawingUnitDeclaration> decisions, PhysicalDrawingUnitPolicy.HostExtents? extents = null)
    {
        var profile = new ProjectProfile();
        foreach (var decision in decisions)
        {
            profile.DrawingUnitDeclarations.Add(decision);
            if (decision.DecisionKind != null) PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, DateTime.UtcNow);
        }
        return PhysicalDrawingUnitPolicy.Resolve(raw, fingerprint, path, profile.DrawingUnitDeclarations,
            hostExtents: extents, reviews: profile.DrawingUnitReviews);
    }

    [Fact]
    public void MultilineReasonCannotEscapeTheWritersAuditComment() =>
        DrawingUnitDeclarationReview.AuditText("  checked\r\nsource: measured\nunit: metres\t ")
            .Should().Be("checked source: measured unit: metres");

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void PendingInformationAndRevocationStatesRemainLegible(int raw, bool existing) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var dialog = new DrawingUnitsReviewDialog(Context(raw, existing));
            try
            {
                if (existing) Fill(dialog);
                var frame = UnshownDialogRender.Attach(dialog, 430, 390);
                UnshownDialogRender.AssertWithin(dialog.Cancel, frame);
                UnshownDialogRender.AssertWithin(dialog.Validation, frame);
                if (existing) UnshownDialogRender.AssertWithin(dialog.Revoke, frame);
                dialog.Save.IsEnabled.Should().BeFalse(); dialog.Metres.IsChecked.Should().BeFalse();
                UnshownDialogRender.Save(frame, "MHD_UNITS_UI_CAPTURE_DIR", $"units-state-{raw}-existing-{existing}");
                if (dialog.BodyScroll.ScrollableHeight > 0)
                {
                    dialog.BodyScroll.ScrollToBottom(); frame.UpdateLayout();
                    UnshownDialogRender.Save(frame, "MHD_UNITS_UI_CAPTURE_DIR", $"units-state-bottom-{raw}-existing-{existing}");
                }
                dialog.ApprovedDeclaration.Should().BeNull(); dialog.RevocationRequested.Should().BeFalse();
            }
            finally { dialog.Close(); }
        });

    [Theory]
    [InlineData(670, 670, false)]
    [InlineData(430, 390, false)]
    [InlineData(430, 390, true)]
    public void NarrowReviewKeepsActionsVisibleAndFieldsReachable(int width, int height, bool existing) =>
        ManualMappingBatchDialogTests.RunSta(() =>
        {
            var dialog = new DrawingUnitsReviewDialog(Context(existing: existing));
            try
            {
                Fill(dialog); dialog.Metres.IsChecked = true;
                var frame = UnshownDialogRender.Attach(dialog, width, height);
                UnshownDialogRender.Save(frame, "MHD_UNITS_UI_CAPTURE_DIR", $"units-top-{width}-existing-{existing}");
                foreach (var action in new FrameworkElement[] { dialog.Save, dialog.Cancel, dialog.Validation })
                    UnshownDialogRender.AssertWithin(action, frame);
                if (existing) UnshownDialogRender.AssertWithin(dialog.Revoke, frame);
                dialog.BodyScroll.ScrollToBottom(); frame.UpdateLayout();
                UnshownDialogRender.AssertWithin(dialog.Approver, dialog.BodyScroll);
                dialog.BodyScroll.ExtentWidth.Should().BeLessThanOrEqualTo(dialog.BodyScroll.ViewportWidth + 1);
                UnshownDialogRender.Save(frame, "MHD_UNITS_UI_CAPTURE_DIR", $"units-bottom-{width}-existing-{existing}");
                dialog.Save.IsEnabled.Should().BeTrue(); dialog.ApprovedDeclaration.Should().BeNull();
                dialog.IsVisible.Should().BeFalse();
            }
            finally { dialog.Close(); }
        });

    // ---- b25 (Codex 15:12, LA-40: INSUNITS 0 with Civil drawing units in inches) ----

    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence CivilInches = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 1);
    private static readonly PhysicalDrawingUnitPolicy.CivilUnitEvidence CivilMeters = new(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed, 6);

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock block) yield return block.Text;
            foreach (var text in Texts(child)) yield return text;
        }
    }

    [Fact]
    public void AUnitlessHostShowsTheCivilUnitsAndContradictingThemTakesAnExplicitAcknowledgement() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context() with { CivilUnits = CivilInches });
        try
        {
            Texts(dialog).Should().Contain(t => t.StartsWith("יחידות השרטוט ב-Civil: אינץ'", StringComparison.Ordinal));
            Texts(dialog).Should().Contain(t => t.Contains("לא מטר", StringComparison.Ordinal));
            dialog.CivilConflict.Visibility.Should().Be(Visibility.Visible);
            dialog.CivilConflict.IsChecked.Should().BeFalse("nothing is preselected");
            Fill(dialog); dialog.Metres.IsChecked = true;
            dialog.Save.IsEnabled.Should().BeFalse("metres against Civil inches needs the explicit acknowledgement");
            dialog.Validation.Text.Should().Contain("סותרות את המטרים");
            Click(dialog.Save); dialog.ApprovedDeclaration.Should().BeNull();
            dialog.CivilConflict.IsChecked = true;
            dialog.Save.IsEnabled.Should().BeTrue();
            dialog.Metres.IsChecked = false;
            dialog.CivilConflict.IsChecked.Should().BeFalse("unchecking metres withdraws the acknowledgement too");
            dialog.Metres.IsChecked = true; dialog.Save.IsEnabled.Should().BeFalse();
            dialog.CivilConflict.IsChecked = true; Click(dialog.Save);
            var approved = dialog.ApprovedDeclaration!;
            approved.DecisionKind.Should().Be(PhysicalDrawingUnitPolicy.UnitlessMetres);
            approved.CivilDrawingUnitStatus.Should().Be(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Observed);
            approved.CivilDrawingUnitCode.Should().Be(1, "bound to the inches Civil showed when it was decided");
            approved.PhysicalUnitCode.Should().Be(6); approved.MetresPerUnit.Should().Be(1);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void CancellingAUnitlessReviewThatContradictsCivilWritesNothing() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var dialog = new DrawingUnitsReviewDialog(Context() with { CivilUnits = CivilInches });
        Fill(dialog); dialog.Metres.IsChecked = true; dialog.CivilConflict.IsChecked = true;
        Click(dialog.Cancel);
        dialog.ApprovedDeclaration.Should().BeNull(); dialog.RevocationRequested.Should().BeFalse();
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutAContradictionTheUnitlessReviewBindsWhatCivilShowed(bool readable) => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var civil = readable ? CivilMeters : PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("SYNTHETIC unreadable");
        var dialog = new DrawingUnitsReviewDialog(Context() with { CivilUnits = civil });
        try
        {
            Texts(dialog).Should().Contain(t => t.StartsWith("יחידות השרטוט ב-Civil: " + (readable ? "מטר" : "לא קריא"), StringComparison.Ordinal));
            dialog.CivilConflict.Visibility.Should().Be(Visibility.Collapsed);
            Fill(dialog); dialog.Metres.IsChecked = true;
            dialog.Save.IsEnabled.Should().BeTrue(); Click(dialog.Save);
            var approved = dialog.ApprovedDeclaration!;
            approved.CivilDrawingUnitStatus.Should().Be(civil.Status);
            approved.CivilDrawingUnitCode.Should().Be(civil.UnitCode);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void TheTextsNameTheUnitlessDecisionAndAContradictedOldDeclaration()
    {
        var decision = PhysicalDrawingUnitPolicy.CreateApprovedDeclaration(Fingerprint, Path, 0, PhysicalDrawingUnitPolicy.UnitlessMetres,
            6, "SYNTHETIC", DateTime.UtcNow, "r", "s", CivilMeters);
        var profile = new ProjectProfile();
        profile.DrawingUnitDeclarations.Add(decision);
        PhysicalDrawingUnitPolicy.LinkReviews(profile, decision, DateTime.UtcNow);
        var reviewed = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Path, profile.DrawingUnitDeclarations,
            civilUnits: CivilMeters, reviews: profile.DrawingUnitReviews);
        DrawingUnitDeclarationReview.Describe(reviewed).Should().Contain("מטר לפי הצהרה מאושרת").And.Contain("Civil");
        DrawingUnitDeclarationReview.Summary(reviewed).Should().Contain("הצהרה מאושרת לשרטוט Unitless");

        var legacy = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, Path, 0, "SYNTHETIC", DateTime.UtcNow, "r", "s");
        var contradicted = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Path, new[] { legacy }, civilUnits: CivilInches);
        DrawingUnitDeclarationReview.Compact(contradicted).Should().Contain("נדרשת בדיקה").And.Contain("ללא תמחור");
        DrawingUnitDeclarationReview.Summary(contradicted).Should().Contain("בירור מול Civil").And.NotContain("סתירה עם Civil");
        DrawingUnitDeclarationReview.Describe(contradicted).Should().Contain("הצהרה מחודשת").And.NotContain("אישור היחידה הרשומה");
    }

    // ---- b26 (LA-40 live 17:39): Civil 2027 names only Feet and Meters; inches came back as "unreadable" ----

    [Fact]
    public void AUnitlessHostWithAnUnnamedCivilUnitShowsItAsNeitherMetresNorFeetAndNeedsTheAcknowledgement() =>
        ManualMappingBatchDialogTests.RunSta(() =>
    {
        var other = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6");
        var dialog = new DrawingUnitsReviewDialog(Context() with { CivilUnits = other });
        try
        {
            Texts(dialog).Should().Contain(t => t.StartsWith("יחידות השרטוט ב-Civil: ערך שה-API של Civil אינו ממפה ליחידה (6)", StringComparison.Ordinal));
            // Codex 17:57: an unmapped value is "cannot confirm", never a claimed other unit.
            Texts(dialog).Should().Contain(t => t.Contains("אי אפשר לאשר מהן שהשרטוט במטרים", StringComparison.Ordinal));
            Texts(dialog).Should().NotContain(t => t.Contains("לא מטר. היא אינה מכריעה", StringComparison.Ordinal));
            Texts(dialog).Should().NotContain(t => t.Contains("לא קריא", StringComparison.Ordinal));
            dialog.CivilConflict.Visibility.Should().Be(Visibility.Visible);
            Fill(dialog); dialog.Metres.IsChecked = true;
            dialog.Save.IsEnabled.Should().BeFalse();
            // Codex 18:18: before the acknowledgement the validation states uncertainty, never a proven contradiction.
            dialog.Validation.Text.Should().Contain("אינן מאשרות מטרים").And.NotContain("סותרות");
            dialog.CivilConflict.IsChecked = true; Click(dialog.Save);
            var approved = dialog.ApprovedDeclaration!;
            approved.CivilDrawingUnitStatus.Should().Be(PhysicalDrawingUnitPolicy.CivilUnitEvidence.ObservedOther);
            approved.CivilDrawingUnitCode.Should().Be(6);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void TheSummaryOfAnUnmappedReadingIsANeutralReviewNotAContradiction()
    {
        // Codex 18:18: raw-known + unmapped and legacy raw0 + unmapped both carry ReviewConflict; the label is neutral.
        var other = PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6");
        var rawKnown = PhysicalDrawingUnitPolicy.Resolve(4, Fingerprint, Path, null, civilUnits: other);
        DrawingUnitDeclarationReview.Summary(rawKnown).Should().Contain("בירור מול Civil").And.NotContain("סתירה");
        DrawingUnitDeclarationReview.Compact(rawKnown).Should().Contain("ללא תמחור");
        var legacy = PhysicalDrawingUnitPolicy.CreateApprovedMetresDeclaration(Fingerprint, Path, 0, "SYNTHETIC", DateTime.UtcNow, "r", "s");
        var legacyUnmapped = PhysicalDrawingUnitPolicy.Resolve(0, Fingerprint, Path, new[] { legacy }, civilUnits: other);
        DrawingUnitDeclarationReview.Summary(legacyUnmapped).Should().Contain("בירור מול Civil").And.NotContain("סתירה");
        DrawingUnitDeclarationReview.Describe(legacyUnmapped).Should().Contain("אינן מאפשרות לאשר").And.NotContain("סותרות");
    }

    [Fact]
    public void TheCivilNameNeverGuessesAUnitForAnUnnamedReading()
    {
        DrawingUnitDeclarationReview.CivilName(PhysicalDrawingUnitPolicy.CivilUnitsFromApi(2, "Meters")).Should().Be("מטר");
        DrawingUnitDeclarationReview.CivilName(PhysicalDrawingUnitPolicy.CivilUnitsFromApi(30, "Feet")).Should().Be("רגל");
        DrawingUnitDeclarationReview.CivilName(PhysicalDrawingUnitPolicy.CivilUnitsFromApi(6, "6"))
            .Should().Be("ערך שה-API של Civil אינו ממפה ליחידה (6)");
        DrawingUnitDeclarationReview.CivilName(PhysicalDrawingUnitPolicy.CivilUnitEvidence.Failed("x")).Should().Be("לא קריא");
        DrawingUnitDeclarationReview.CivilName(null).Should().Be("לא רלוונטי");
    }
}
