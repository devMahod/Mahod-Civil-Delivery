using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Typical sections (2026-10): the palette → pick command → palette handshake runs through Document events and a
/// queued command, which need Civil. These contracts pin the production wiring and its order without a drawing, the
/// way <see cref="WorkflowSaveGuidanceSourceContractTests"/> pins the save-and-resume handshake.
/// </summary>
public sealed class TypicalSectionsSourceContractTests
{
    private static string PluginSourceDir =>
        typeof(TypicalSectionsSourceContractTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "MahodPluginSourceDir").Value!;

    private static string Read(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(PluginSourceDir, Path.Combine))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Ui(string name) => Read("CivilDelivery", "UI", name);

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the production method {0} must exist", signature);
        var rest = source[(start + signature.Length)..];
        var next = Regex.Match(rest, @"(?m)^[ \t]+(?:private|internal|public)\s");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private static int At(string source, string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, "the production code must contain {0}", token);
        return index;
    }

    [Fact]
    public void PickCommand_IsLaunchedWithAUniqueTokenAndReportsBackThroughThePalette()
    {
        var panel = Ui("CivilDeliveryControl.TypicalSections.cs");
        var start = Method(panel, "private void OnTypicalSections(");
        start.Should().Contain("Guid.NewGuid().ToString(\"N\")")
            .And.Contain("doc!.CommandInProgress")
            .And.Contain("TryClearPreview(")
            .And.Contain("TypicalSectionPickRequests.Register(")
            .And.Contain("doc.CommandCancelled += OnTypicalPickCommandEnded;")
            .And.Contain("doc.SendStringToExecute(CivilDeliveryCommandNames.TypicalSections + \"\\n\" + token + \"\\n\", true, false, false);");
        At(start, "TypicalSectionPickRequests.Register(").Should().BeLessThan(At(start, "doc.SendStringToExecute("));
        At(start, "_pendingTypicalPickToken = token;").Should().BeLessThan(At(start, "doc.SendStringToExecute("));

        var resume = Method(panel, "internal void ResumeTypicalSectionPicks(");
        resume.Should().Contain("SavedDrawingContinuationPolicy.CanConsume(")
            .And.Contain("doc!.Database.UnmanagedObject != expectedDatabase");
        At(resume, "SavedDrawingContinuationPolicy.CanConsume(").Should().BeLessThan(At(resume, "_typicalPicks.AddRange("));

        var command = Read("CivilDelivery", "Commands", "MhdTypicalSectionsCommand.cs");
        command.Should().Contain("[assembly: CommandClass(typeof(")
            .And.Contain("CommandMethod(CivilDeliveryCommandNames.TypicalSections")
            .And.Contain("CommandFlags.Modal | CommandFlags.NoHistory")
            .And.Contain("ed.Drag(jig)")
            .And.Contain("PromptStatus.Cancel")
            .And.Contain("jig.Current ??")
            .And.NotContain("MHD_TYPICAL_SECTIONS\"");

        // The live preview: ASCII global keywords with a Hebrew display, and the same resolver the click commits.
        var jig = Read("CivilDelivery", "Sections", "Services", "TypicalPickJig.cs");
        jig.Should().Contain(": DrawJig")
            .And.Contain("options.Keywords.Add(UndoKeyword, UndoKeyword, \"בטל אחרון\")")
            .And.Contain("options.Keywords.Add(WidthKeyword, WidthKeyword, \"רוחב\")")
            .And.Contain("_picker.Resolve(")
            .And.Contain("current.IsReady ? ReadyColor : RefusedColor");
        var run = Method(command, "public void Run(");
        At(run, "TypicalSectionPickRequests.TryTake(").Should().BeLessThan(At(run, "finally"));
        At(run, "finally").Should().BeLessThan(At(run, "CivilDeliveryPalette.ResumeTypicalSectionPicks(result);"));

        var palette = Method(Ui("CivilDeliveryPalette.cs"), "internal static void ResumeTypicalSectionPicks(");
        palette.Should().Contain("Dispatcher.BeginInvoke(").And.Contain("control.ResumeTypicalSectionPicks(result)");
    }

    [Fact]
    public void ClosingTheDrawing_CancelsAPendingPickLikeAPendingSave()
    {
        var destroying = Method(Ui("CivilDeliveryControl.DrawingSaveContext.cs"), "private void OnObservedDocumentDestroying(");
        destroying.Should().Contain("CancelWorkflowSave(e.Document").And.Contain("CancelTypicalPick(e.Document");

        var guidance = Ui("CivilDeliveryControl.SectionGuidance.cs");
        guidance.Should().Contain("PickingTypicalSections = _pendingTypicalPickDocument != null,")
            .And.Contain("TypicalPicksPending = _typicalPicks.Count,")
            .And.Contain("BtnSectionNext.IsEnabled &= _pendingTypicalPickDocument == null;")
            .And.Contain("case SectionGuidedActionKind.ConfirmTypicalSections: OnConfirmTypicalSections(sender, e); break;");
    }

    [Fact]
    public void BothCommandNameBranches_DeclareTheTypicalSectionsCommands()
    {
        var names = Read("CivilDelivery", "Commands", "CivilDeliveryCommandNames.cs");
        names.Should().Contain("TypicalSections = \"MCD_TYPICAL_SECTIONS\"")
            .And.Contain("TypicalSections = \"MHD_TYPICAL_SECTIONS\"")
            .And.Contain("SmokeTypical = \"MCD_SMOKE_TYPICAL\"")
            .And.Contain("SmokeTypical = \"MHD_SMOKE_TYPICAL\"");
    }

    [Fact]
    public void Confirm_ReviewsInThePopup_AndTheResultStaysOpenBesideTheDrawing()
    {
        var panel = Ui("CivilDeliveryControl.TypicalSections.cs");
        var confirm = Method(panel, "private void OnConfirmTypicalSections(");
        At(confirm, "TypicalSectionsDialog.ForReview(").Should().BeLessThan(At(confirm, "CreateTypicalWithChoices(doc,"));
        confirm.Should().Contain("CivilModalHost.ShowFromPalette(review)")
            .And.Contain("EraseTypicalMarkers(doc, review.RemovedLineHandles)")
            .And.Contain("TypicalSectionsDialog.Outcome.AddMore")
            .And.NotContain("RtlMessageBox.Show(");
        var show = Method(panel, "private void ShowTypicalSectionsResult(");
        show.Should().Contain("TypicalSectionsDialog.ForBlocked(")
            .And.Contain("TypicalSectionsDialog.ForResult(")
            .And.Contain("ShowModelessWindow(")
            .And.Contain("window.ShowSectionRequested += handle => ZoomToTypicalSection(handle);");
    }

    [Fact]
    public void TheButtonGoesStraightToTheDrawing_TheWidthIsAskedOnlyThroughItsKeyword()
    {
        // The engineer's ask (2026-10-28): after the button, the next click is on the road, not Enter at a prompt.
        var command = Read("CivilDelivery", "Commands", "MhdTypicalSectionsCommand.cs");
        var pick = Method(command, "internal static void Pick(");
        pick.Should().Contain("TypicalSectionPickLogic.DefaultHalfWidth(request.DefaultHalfWidthM)");
        At(pick, "ed.Drag(jig)").Should().BeLessThan(At(pick, "PromptHalfWidth("),
            "the half-width prompt belongs to the רוחב keyword inside the pick loop");
    }

    [Fact]
    public void ShowAndTheAutomaticZoom_UseAMarginTheZoomAccepts_AndSayWhenItFails()
    {
        // Live 2026-10-28: "הצג" did nothing — margins of 0.15 / 0.1, which the zoom refuses below 1 without a word.
        var panel = Ui("CivilDeliveryControl.TypicalSections.cs");
        panel.Should().Contain("TypicalSectionZoomMargin = 1.25, TypicalAllSectionsZoomMargin = 1.10")
            .And.NotContain("ext, 0.15)").And.NotContain(")), 0.1)");
        var show = Method(panel, "private void ZoomToTypicalSection(");
        show.Should().Contain("NativeViewZoomService.TryZoom(doc, ext, TypicalSectionZoomMargin)")
            .And.Contain("NativeViewZoomService.LastDiagnostic")
            .And.Contain("SetStatus(");
        var zoom = Read("CivilDelivery", "Estimate", "ViewZoomPlan.cs");
        zoom.Should().Contain("margin < 1) return false", "the contract these margins must respect");
    }

    [Fact]
    public void AnUnbuiltCorridorOrOneWithoutSurface_IsMendedOnTheEngineersClick_ThenTheRunRepeats()
    {
        var service = Read("CivilDelivery", "Sections", "Services", "TypicalSectionsService.cs");
        service.Should().Contain("result.CorridorsToRebuild.Add(chosen.Name)")
            .And.Contain("result.CorridorsWithoutSurface.Add(chosen.Name)");
        var fix = Method(service, "public static List<string> FixCorridors(");
        fix.Should().Contain("doc.LockDocument()")
            .And.Contain("corridor.CorridorSurfaces.Add(corridor.Name + \" - Top\")")
            .And.Contain("surface.AddLinkCode(\"Top\", true)")
            .And.Contain("corridor.Rebuild()");
        At(fix, "corridor.CorridorSurfaces.Add(").Should().BeLessThan(At(fix, "corridor.Rebuild()"),
            "the new surface is built by the rebuild");

        var panel = Ui("CivilDeliveryControl.TypicalSections.cs");
        var show = Method(panel, "private void ShowTypicalSectionsResult(");
        show.Should().Contain("result.CanFixCorridors ? TypicalFixLabel : null")
            .And.Contain("blocked.Choice == TypicalSectionsDialog.Outcome.Fix");
        var retry = Method(panel, "private void FixCorridorsAndRetryTypical(");
        At(retry, "TypicalSectionsService.FixCorridors(").Should().BeLessThan(At(retry, "CreateTypicalWithChoices(doc,"));
    }

    [Fact]
    public void TheMarkerIsAWideBand_AndOlderLineMarkersStillCount()
    {
        var picker = Read("CivilDelivery", "Sections", "Services", "TypicalSectionPicker.cs");
        picker.Should().Contain("TypicalSectionContracts.MarkerWidth(halfWidth)")
            .And.Contain("new Polyline(2)")
            .And.Contain("TypicalSectionContracts.MarkerTextHeight(halfWidth)")
            .And.Contain("\"LWPOLYLINE\"").And.Contain("\"LINE\"");
        var jig = Read("CivilDelivery", "Sections", "Services", "TypicalPickJig.cs");
        jig.Should().Contain("TypicalSectionContracts.MarkerWidth(_halfWidth)").And.Contain("geometry.Draw(band)");
    }

    [Fact]
    public void AmbiguousChoices_AreAskedInAWindow_AndTheBlockedListHasNoRepeats()
    {
        var service = Read("CivilDelivery", "Sections", "Services", "TypicalSectionsService.cs");
        service.Should().Contain("TypicalCorridorChoiceLogic.ExistingGround(")
            .And.Contain("result.ExistingSurfaceChoicesNeeded.AddRange(")
            .And.NotContain("existing_surface_name).", "the engineer chooses in the window, not by editing the profile")
            .And.Contain("if (!result.Blockers.Contains(reason, StringComparer.Ordinal)) result.Blockers.Add(reason);");
        // The checks that can meet the same reason twice (two alignments, one drawing) all go through Block.
        var precheck = service[At(service, "void Block(string reason)")..At(service, "tr.Abort();\n            }\n            log?.End(\"typical.precheck\"")];
        precheck.Should().NotContain("result.Blockers.Add(\"").And.NotContain("result.Blockers.Add($");
        var panel = Ui("CivilDeliveryControl.TypicalSections.cs");
        panel.Should().Contain("TypicalChoiceDialog.ForExistingGround(result.ExistingSurfaceChoicesNeeded)")
            .And.Contain("TypicalChoiceDialog.ForCorridor(alignment, corridors)");
    }

    [Fact]
    public void TheSmokeCommand_RunsOnlyOnAFixtureCopy_AndProvesTheRefusals()
    {
        var smoke = Read("CivilDelivery", "Commands", "MhdSmokeTypicalSectionsCommand.cs");
        smoke.Should().Contain("CommandMethod(CivilDeliveryCommandNames.SmokeTypical")
            .And.Contain("SmokeApplyGuard.Refusal(")
            .And.Contain("refuses a second click at a marked station")
            .And.Contain("refuses a click far from the road")
            .And.Contain("one firm section per pick")
            .And.Contain("smoke_typical_latest.json");
        At(smoke, "SmokeApplyGuard.Refusal(").Should().BeLessThan(At(smoke, "picker.Commit("));
    }
}
