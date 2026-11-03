using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Typical sections (2026-10): the panel side of the pick handshake. The panel registers a request, sends the pick
/// command with a token and waits; the command reports back through <see cref="CivilDeliveryPalette"/>. The pending
/// triple is separate from the save-and-resume triple on purpose: <c>EnsureSavedForAction</c> refuses to run while a
/// save is pending, and its cancel handlers only know QSAVE and the after-save command.
/// </summary>
public partial class CivilDeliveryControl
{
    private Document? _pendingTypicalPickDocument;
    private IntPtr _pendingTypicalPickDatabase;
    private string? _pendingTypicalPickToken;
    private readonly List<TypicalSectionPick> _typicalPicks = new();
    private IntPtr _typicalPicksDatabase;
    private double? _typicalPickHalfWidthM;

    // No CL setup is required: the firm's section program cuts at the picked stations, so CL layers and sampled
    // sources (what ProjectSetupService.NeedsSetup checks) play no part. A usable profile is still needed for the
    // allowed alignments and the plan-mark rules.
    private bool CanStartTypicalSections(Document? doc, bool profileUsable) =>
        doc != null && profileUsable && _profile != null &&
        _pendingWorkflowSaveDocument == null && _pendingTypicalPickDocument == null &&
        _busyProgress == null && !_sectionDrawingOpenPending;

    private void RefreshTypicalSectionGates(bool profileUsable)
    {
        var doc = Doc();
        // Picks belong to the drawing they were marked in; another drawing shows none of them.
        if (_typicalPicks.Count > 0 && (doc?.Database.UnmanagedObject ?? IntPtr.Zero) != _typicalPicksDatabase)
            _typicalPicks.Clear();
        BtnTypicalSections.IsEnabled = CanStartTypicalSections(doc, profileUsable);
        // The sheet needs only typical sections already in the drawing: no profile, no CL.
        BtnTypicalSheet.IsEnabled = doc != null && _pendingTypicalPickDocument == null && _busyProgress == null;
        BtnTypicalCancel.Visibility = _typicalPicks.Count > 0 && _pendingTypicalPickDocument == null
            ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void OnTypicalSections(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        var profileUsable = _profile != null && !string.IsNullOrWhiteSpace(_profileWriteTarget);
        if (!CanStartTypicalSections(doc, profileUsable) || _profile == null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(doc!.CommandInProgress))
            {
                SetStatus("פקודה אחרת פעילה בשרטוט — יש לסיים אותה וללחוץ שוב על 'חתכים טיפוסיים'");
                return;
            }
            if (!TryClearPreview("סימון חתכים טיפוסיים")) return;

            var token = Guid.NewGuid().ToString("N");
            var expectedDatabase = doc.Database.UnmanagedObject;
            TypicalSectionPickRequests.Register(new TypicalSectionPickRequest(
                token, expectedDatabase,
                TypicalSectionPickLogic.DefaultHalfWidth(_typicalPickHalfWidthM ?? _profile.Sections.Typical?.HalfWidthM),
                _profile.Sections.Alignments.AllowedNames.ToList()));
            _pendingTypicalPickDocument = doc;
            _pendingTypicalPickDatabase = expectedDatabase;
            _pendingTypicalPickToken = token;
            doc.CommandCancelled += OnTypicalPickCommandEnded;
            doc.CommandFailed += OnTypicalPickCommandEnded;
            SetStatus("סימון חתכים: לחיצה על הכביש לכל חתך · Enter לסיום · Esc לביטול");
            Log("סימון חתכים טיפוסיים הופעל בשרטוט");
            doc.SendStringToExecute(CivilDeliveryCommandNames.TypicalSections + "\n" + token + "\n", true, false, false);
        }
        catch (Exception ex)
        {
            CancelTypicalPick(doc, "הפעלת סימון החתכים נכשלה");
            ShowError("סימון חתכים טיפוסיים", ex);
        }
        finally { RefreshGates(); }
    }

    private void OnTypicalPickCommandEnded(object sender, CommandEventArgs e)
    {
        if (string.Equals(e.GlobalCommandName, CivilDeliveryCommandNames.TypicalSections, StringComparison.OrdinalIgnoreCase))
            CancelTypicalPick(sender as Document, "סימון החתכים הופסק — הפאנל לא קיבל סימונים");
    }

    private void CancelTypicalPick(Document? document, string reason)
    {
        if (document == null || !ReferenceEquals(document, _pendingTypicalPickDocument)) return;
        try
        {
            document.CommandCancelled -= OnTypicalPickCommandEnded;
            document.CommandFailed -= OnTypicalPickCommandEnded;
        }
        catch { /* A destroying document can no longer expose events. */ }
        TypicalSectionPickRequests.Forget(_pendingTypicalPickToken);
        _pendingTypicalPickDocument = null;
        _pendingTypicalPickDatabase = IntPtr.Zero;
        _pendingTypicalPickToken = null;
        if (!string.IsNullOrEmpty(reason)) SetStatus(reason);
        Dispatcher.BeginInvoke(new Action(RefreshGates));
    }

    /// <summary>The pick command's report, posted by the palette after the command returned.</summary>
    internal void ResumeTypicalSectionPicks(TypicalSectionPickResult result)
    {
        // An old command cannot report into a newer request, and a request that was cancelled takes nothing.
        if (!SavedDrawingContinuationPolicy.CanConsume(
                _pendingTypicalPickToken, result.Token, _pendingTypicalPickDocument != null)) return;
        var expected = _pendingTypicalPickDocument;
        var expectedDatabase = _pendingTypicalPickDatabase;
        CancelTypicalPick(expected, string.Empty);
        try
        {
            var doc = Doc();
            if (result.Error != null)
            {
                SetStatus("סימון החתכים נכשל: " + result.Error);
                return;
            }
            if (result.Cancelled)
            {
                SetStatus("סימון החתכים בוטל — לא נוסף דבר");
                return;
            }
            if (!ReferenceEquals(doc, expected) || doc!.Database.UnmanagedObject != expectedDatabase)
            {
                SetStatus("השרטוט הוחלף בזמן הסימון — הסימון לא נקלט בפאנל");
                return;
            }
            if (result.Picks.Count == 0)
            {
                SetStatus("לא סומן אף חתך");
                return;
            }
            if (_typicalPicksDatabase != expectedDatabase) _typicalPicks.Clear();
            _typicalPicksDatabase = expectedDatabase;
            var known = new HashSet<string>(_typicalPicks.Select(pick => pick.LineHandle), StringComparer.OrdinalIgnoreCase);
            _typicalPicks.AddRange(result.Picks.Where(pick => known.Add(pick.LineHandle)));
            _typicalPickHalfWidthM = result.HalfWidthM ?? _typicalPickHalfWidthM;
            SetStatus($"{_typicalPicks.Count} חתכים סומנו בשרטוט");
            Log($"סומנו {result.Picks.Count} חתכים טיפוסיים (סה\"כ {_typicalPicks.Count})");
        }
        finally { RefreshGates(); }
    }

    /// <summary>Erases the marker entities of every pending pick; the picks are forgotten with them.</summary>
    private void OnCancelTypicalPicks(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        if (doc == null || _typicalPicks.Count == 0) return;
        if (RtlMessageBox.Show($"למחוק את {_typicalPicks.Count} קווי הסימון מהשרטוט?", "ביטול סימון",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            EraseTypicalMarkers(doc, _typicalPicks.Select(pick => pick.LineHandle).ToList());
            SetStatus("קווי הסימון נמחקו");
        }
        catch (Exception ex) { ShowError("ביטול סימון חתכים", ex); }
        finally { RefreshGates(); }
    }

    /// <summary>Erases the marker line and station text of the given pending picks and forgets them.</summary>
    private void EraseTypicalMarkers(Document doc, IReadOnlyCollection<string> lineHandles)
    {
        var wanted = new HashSet<string>(lineHandles, StringComparer.OrdinalIgnoreCase);
        var picks = _typicalPicks.Where(pick => wanted.Contains(pick.LineHandle)).ToList();
        if (picks.Count == 0) return;
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (var handle in picks.SelectMany(pick => new[] { pick.LineHandle, pick.TextHandle }))
            {
                if (!doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(handle, 16)), out var id) ||
                    id.IsNull || id.IsErased) continue;
                tr.GetObject(id, OpenMode.ForWrite).Erase();
            }
            tr.Commit();
        }
        _typicalPicks.RemoveAll(pick => wanted.Contains(pick.LineHandle));
    }

    /// <summary>
    /// Confirm: the firm's section program draws one block per marked station, then the plan's strip names are
    /// written above each width. No save is required first — the firm program writes directly — and the run folder
    /// records what was asked and drawn. Picks that were created leave the pending list; the rest stay for a retry.
    /// </summary>
    private void OnConfirmTypicalSections(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        if (doc == null || _profile == null || _typicalPicks.Count == 0) return;
        if (_pendingWorkflowSaveDocument != null || _pendingTypicalPickDocument != null || _busyProgress != null) return;
        if (!string.IsNullOrWhiteSpace(doc.CommandInProgress))
        {
            SetStatus("פקודה אחרת פעילה בשרטוט — יש לסיים אותה לפני יצירת החתכים");
            return;
        }
        // The review popup: every mark, remove one, add more, or create. Nothing is drawn before "צור".
        var ordered = _typicalPicks.OrderBy(pick => pick.AlignmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pick => pick.Station).ToList();
        var review = TypicalSectionsDialog.ForReview(
            ordered.Select((pick, index) => new TypicalSectionsDialog.PickRow
            {
                Number = index + 1,
                Station = TypicalSectionContracts.MarkerLabel(pick.Station),
                Alignment = pick.AlignmentName,
                LineHandle = pick.LineHandle,
            }).ToList(),
            TypicalSubtitle(ordered),
            ordered.Select(pick => pick.HalfWidthM).DefaultIfEmpty(TypicalSectionContracts.DefaultHalfWidthM).Max());
        var reviewed = CivilModalHost.ShowFromPalette(review);
        try
        {
            if (review.RemovedLineHandles.Count > 0)
            {
                EraseTypicalMarkers(doc, review.RemovedLineHandles);
                SetStatus(review.RemovedLineHandles.Count == 1 ? "סימון אחד הוסר" : $"{review.RemovedLineHandles.Count} סימונים הוסרו");
            }
        }
        catch (Exception ex) { ShowError("הסרת סימונים", ex); }
        finally { RefreshGates(); }
        if (reviewed != true || review.Choice == TypicalSectionsDialog.Outcome.None)
        {
            if (_typicalPicks.Count > 0) SetStatus($"{_typicalPicks.Count} סימונים ממתינים ליצירה");
            return;
        }
        if (review.Choice == TypicalSectionsDialog.Outcome.AddMore)
        {
            OnTypicalSections(sender, e);
            return;
        }
        if (_typicalPicks.Count == 0) return;

        var result = CreateTypicalWithChoices(doc, _typicalPicks.ToList());
        if (result != null) ShowTypicalSectionsResult(doc, result);
    }

    /// <summary>
    /// Runs the creation; two corridors covering the marked stations, or several TINs that could be the existing
    /// ground, are the engineer's call: asked in a window, then the run repeats once with the answers. Null when the
    /// engineer cancelled a choice or the run failed (already reported).
    /// </summary>
    private TypicalSectionsService.Result? CreateTypicalWithChoices(Document doc, IReadOnlyList<TypicalSectionPick> picks)
    {
        var result = RunTypicalCreate(doc, picks);
        if (result == null || (result.CorridorChoicesNeeded.Count == 0 && result.ExistingSurfaceChoicesNeeded.Count == 0))
            return result;
        foreach (var (alignment, corridors) in result.CorridorChoicesNeeded)
        {
            var dialog = TypicalChoiceDialog.ForCorridor(alignment, corridors);
            if (CivilModalHost.ShowFromPalette(dialog) != true || string.IsNullOrWhiteSpace(dialog.Chosen))
            {
                SetStatus("היצירה בוטלה — לא נבחר קורידור. הסימונים נשמרו.");
                return null;
            }
            _typicalCorridorChoices[alignment] = dialog.Chosen!;
        }
        if (result.ExistingSurfaceChoicesNeeded.Count > 0)
        {
            var dialog = TypicalChoiceDialog.ForExistingGround(result.ExistingSurfaceChoicesNeeded);
            if (CivilModalHost.ShowFromPalette(dialog) != true || string.IsNullOrWhiteSpace(dialog.Chosen))
            {
                SetStatus("היצירה בוטלה — לא נבחר משטח קרקע קיימת. הסימונים נשמרו.");
                return null;
            }
            _typicalExistingGround = (doc.Database.UnmanagedObject, dialog.Chosen!);
        }
        return RunTypicalCreate(doc, picks);
    }

    /// <summary>The blocked window's one-click mend: rebuild the corridors, add the missing top surfaces, try again.</summary>
    internal const string TypicalFixLabel = "תקן ונסה שוב";

    /// <summary>The green button when the run is only a step short: what it does, then creates.</summary>
    internal static string TypicalGoLabel(TypicalSectionsService.Result result) =>
        result.CorridorsWithoutSurface.Count > 0 ? "השלם וצור חתכים"
            : result.CorridorsToSwitchOn.Count > 0 ? "הפעל, בנה וצור חתכים"
            : "בנה מחדש וצור חתכים";

    /// <summary>Said in place of the steps when the mend did not give the corridor what the sections need.</summary>
    internal static string TypicalFixDidNotHelp(IReadOnlyList<string> corridors) =>
        $"{(corridors.Count == 1 ? "הקורידור " + corridors[0] + " נבנה" : "הקורידורים " + string.Join(", ", corridors) + " נבנו")} מחדש, " +
        "ו-Civil 3D עדיין לא חישב בו חתכים לאורך התוואי — יש לבדוק ב-Civil 3D את ה-Baseline, האזורים וה-Assembly שלו " +
        "(Corridor Properties → Parameters) ואת ה-Event Viewer.";

    private void FixCorridorsAndRetryTypical(Document doc, TypicalSectionsService.Result blocked)
    {
        try
        {
            List<string>? lines = null;
            using (var log = new StageLog("ui_typical_fix_corridors"))
                RunBusy("מתקן קורידורים…", () => lines = TypicalSectionsService.FixCorridors(
                    doc, blocked.CorridorsToRebuild, blocked.CorridorsWithoutSurface, log, blocked.CorridorsToSwitchOn));
            foreach (var line in lines ?? new List<string>()) Log(line);
            SetStatus(string.Join(" · ", lines ?? new List<string>()));
        }
        catch (Exception ex)
        {
            ShowError("תיקון קורידורים", ex);
            return;
        }
        finally { RefreshGates(); }

        var result = CreateTypicalWithChoices(doc, _typicalPicks.ToList());
        if (result != null) ShowTypicalSectionsResult(doc, result, afterFix: true);
    }

    /// <summary>The engineer's corridor choices in this session (alignment → corridor), asked only when ambiguous.</summary>
    private readonly Dictionary<string, string> _typicalCorridorChoices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The engineer's existing-ground choice in this session, for the drawing it was made in (by the native database:
    /// AutoCAD hands out a new managed wrapper for the same drawing).
    /// </summary>
    private (IntPtr Database, string Surface)? _typicalExistingGround;

    private TypicalSectionsService.Result? RunTypicalCreate(Document doc, IReadOnlyList<TypicalSectionPick> picks)
    {
        TypicalSectionsService.Result? result = null;
        try
        {
            using var log = new StageLog("ui_typical_sections");
            var ground = _typicalExistingGround is { } chosen && chosen.Database == doc.Database.UnmanagedObject
                ? chosen.Surface
                : null;
            var request = new TypicalSectionsService.Request(
                picks, _profile!, _profileHash, new Dictionary<string, string>(_typicalCorridorChoices, StringComparer.OrdinalIgnoreCase),
                ground);
            RunBusy("יוצר חתכים טיפוסיים…", () => result = new TypicalSectionsService().Create(doc, request, log));
        }
        catch (Exception ex)
        {
            ShowError("יצירת חתכים טיפוסיים", ex);
            return null;
        }
        finally { RefreshGates(); }
        return result;
    }

    private static string TypicalSubtitle(IReadOnlyList<TypicalSectionPick> picks)
    {
        var alignments = picks.Select(pick => pick.AlignmentName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var widths = picks.Select(pick => pick.HalfWidthM).Distinct().ToList();
        var width = widths.Count == 1
            ? FormattableString.Invariant($"חצי רוחב {widths[0]:0.##} מ'")
            : "חצאי רוחב שונים";
        return (alignments.Count == 0 ? "" : string.Join(", ", alignments) + " · ") + width;
    }

    /// <summary>The result window is modeless: "הצג" moves the drawing while it stays open.</summary>
    private TypicalSectionsDialog? _typicalResultWindow;

    private void ShowTypicalSectionsResult(Document doc, TypicalSectionsService.Result result, bool afterFix = false)
    {
        try
        {
            if (result.Blockers.Count > 0)
            {
                // An unbuilt corridor or one without a surface is the tool's to mend, once the engineer says so — once:
                // a mend that did not help is not offered again.
                var offerFix = result.CanFixCorridors && !afterFix;
                var shown = afterFix && result.CanFixCorridors
                    ? result.Blockers.Where(line => !result.FixSteps.Contains(line))
                        .Append(TypicalFixDidNotHelp(result.CorridorsToRebuild.Concat(result.CorridorsWithoutSurface)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList()
                    : result.Blockers.ToList();
                var reasons = string.Join("\n• ", shown);
                var onlySteps = offerFix && result.OnlyFixSteps;
                SetStatus((onlySteps ? "עוד שלב לפני היצירה — " : "החתכים לא נוצרו — ") + shown[0]);
                SectionDetail.Text = (onlySteps ? "לפני היצירה:\n• " : "יצירת חתכים טיפוסיים נעצרה:\n• ") + reasons;
                SectionDiagnostics.IsExpanded = !onlySteps;
                // Only a step short (2026-11-03, a coworker's unbuilt R-14): the step in green, one green button that
                // takes it and creates. Anything else is an error, with the mendable lines still marked as steps.
                var blocked = onlySteps
                    ? TypicalSectionsDialog.ForSteps(shown, TypicalSubtitle(_typicalPicks), TypicalGoLabel(result))
                    : TypicalSectionsDialog.ForBlocked(shown, TypicalSubtitle(_typicalPicks), offerFix ? TypicalFixLabel : null, result.FixSteps);
                CivilModalHost.ShowFromPalette(blocked);
                if (offerFix && blocked.Choice == TypicalSectionsDialog.Outcome.Fix)
                    Dispatcher.BeginInvoke(new Action(() => FixCorridorsAndRetryTypical(doc, result)));
                return;
            }

            var createdPicks = _typicalPicks.Where(pick => result.Sections.Any(section =>
                section.Created && string.Equals(section.LineHandle, pick.LineHandle, StringComparison.OrdinalIgnoreCase))).ToList();
            var subtitle = TypicalSubtitle(createdPicks.Count > 0 ? createdPicks : _typicalPicks);
            var createdHandles = new HashSet<string>(createdPicks.Select(pick => pick.LineHandle), StringComparer.OrdinalIgnoreCase);
            _typicalPicks.RemoveAll(pick => createdHandles.Contains(pick.LineHandle));

            var summary = $"נוצרו {result.CreatedCount} חתכים · {result.NamedCount} רוחבים זוהו · {result.UnmatchedCount} ללא התאמה";
            var failed = result.Sections.FirstOrDefault(section => section.Error != null);
            if (failed != null)
                summary += FormattableString.Invariant($" · נעצר בתחנה {failed.Station:0.00}: {failed.Error}");
            SetStatus(summary);
            Log(summary + (result.ArtifactPath != null ? " · " + result.ArtifactPath : ""));

            var lines = new List<string> { summary };
            foreach (var section in result.Sections)
            {
                lines.Add(FormattableString.Invariant($"{section.AlignmentName} {section.Station:0.00}: ") +
                    (section.Created
                        ? $"{section.Title ?? section.BlockName ?? "בלוק"} · {section.NamedStrips}/{section.ChainSegments} רוחבים עם שם"
                        : section.Error ?? "לא נוצר"));
                foreach (var note in section.Notes) lines.Add("   · " + note);
            }
            foreach (var warning in result.Warnings) lines.Add("אזהרה: " + warning);
            SectionDetail.Text = string.Join("\n", lines);
            if (result.UnmatchedCount > 0 || failed != null || result.Warnings.Count > 0)
                SectionDiagnostics.IsExpanded = true;

            // The margin is a factor of the extents (1.10 = 10 % around them); below 1 the zoom refuses silently,
            // which is how 0.1 left the view where it was (live 2026-10-28).
            if (result.CreatedExtents is { } ext &&
                !NativeViewZoomService.TryZoom(doc, new Autodesk.AutoCAD.DatabaseServices.Extents3d(
                    new Autodesk.AutoCAD.Geometry.Point3d(ext.MinX, ext.MinY, 0),
                    new Autodesk.AutoCAD.Geometry.Point3d(ext.MaxX, ext.MaxY, 0)), TypicalAllSectionsZoomMargin))
                Log("מעבר לחתכים שנוצרו לא בוצע: " + NativeViewZoomService.LastDiagnostic);

            var rows = result.Sections.Select(section => new TypicalSectionsDialog.SectionRow
            {
                Title = section.Created
                    ? section.Title ?? FormattableString.Invariant($"{section.AlignmentName} ({section.Station:0.00})")
                    : FormattableString.Invariant($"{section.AlignmentName} ({section.Station:0.00})"),
                Detail = section.Created
                    ? (section.ChainSegments == 0
                        ? "שרשרת הרוחבים לא נקראה"
                        : $"{section.NamedStrips} מתוך {section.ChainSegments} רוחבים עם שם")
                    : section.Error ?? "לא נוצר",
                Good = section.Created && section.UnmatchedStrips == 0 && section.ChainSegments > 0,
                BlockHandle = section.Created ? section.BlockHandle : null,
            }).ToList();
            var notes = result.Sections
                .SelectMany(section => section.Notes.Select(note =>
                    FormattableString.Invariant($"{section.Station:0.00}: ") + note))
                .Concat(result.Warnings)
                .ToList();
            // The sections in the office's sheet format (the MTS_ block above each firm section, or the drafted one):
            // what "צור גיליון A0" places.
            var sheetHandles = result.Sections.Where(section => section.Created)
                .Select(section => section.OverlayHandle ?? section.BlockHandle)
                .OfType<string>().ToList();
            var offerSheet = result.Sections.Any(section => section.Created && section.OverlayHandle != null) ||
                             createdPicks.Any(pick => pick.FromPlan);
            _typicalResultWindow?.Close();
            var window = TypicalSectionsDialog.ForResult(
                result.CreatedCount, result.NamedCount, result.UnmatchedCount, rows, subtitle, notes, failed != null, offerSheet);
            window.RunFolder = result.ArtifactPath == null ? null : System.IO.Path.GetDirectoryName(result.ArtifactPath);
            window.ShowSectionRequested += handle => ZoomToTypicalSection(handle);
            window.Closed += (_, _) =>
            {
                if (window.Choice == TypicalSectionsDialog.Outcome.AddMore)
                    Dispatcher.BeginInvoke(new Action(() => OnTypicalSections(this, new RoutedEventArgs())));
                if (window.Choice == TypicalSectionsDialog.Outcome.Sheet)
                    Dispatcher.BeginInvoke(new Action(() => CreateTypicalSheet(doc, sheetHandles)));
                if (ReferenceEquals(_typicalResultWindow, window)) _typicalResultWindow = null;
            };
            _typicalResultWindow = window;
            Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModelessWindow(
                Autodesk.AutoCAD.ApplicationServices.Core.Application.MainWindow.Handle, window);
        }
        catch (Exception ex) { ShowError("סיכום חתכים טיפוסיים", ex); }
        finally { RefreshGates(); }
    }

    /// <summary>
    /// The panel's "גיליון חתכים" (2026-11-03: the sheet was a typed command nobody found): the engineer selects the
    /// typical sections in the drawing, Enter takes every one. Runs the sheet command without typing it.
    /// </summary>
    private void OnTypicalSheet(object sender, RoutedEventArgs e)
    {
        var doc = Doc();
        if (doc == null) return;
        CreateTypicalSheet(doc, null);
    }

    /// <summary>
    /// The A0 sheets of the given typical sections (by handle), or of the ones the engineer selects when none are given.
    /// The sheet command runs on its own, after the typical run is over: building a layout inside that run took
    /// Civil 3D down (2026-11-02).
    /// </summary>
    private void CreateTypicalSheet(Document doc, IReadOnlyList<string>? handles)
    {
        try
        {
            if (!ReferenceEquals(Doc(), doc))
            {
                SetStatus("השרטוט הוחלף — הגיליון לא נוצר");
                return;
            }
            if (!string.IsNullOrWhiteSpace(doc.CommandInProgress))
            {
                SetStatus("פקודה אחרת פעילה בשרטוט — יש לסיים אותה וללחוץ שוב על 'גיליון חתכים'");
                return;
            }
            SetStatus(handles == null
                ? "גיליון חתכים: בחר בשרטוט את החתכים הטיפוסיים, או Enter לכולם"
                : "יוצר גיליון A0 לחתכים שנוצרו…");
            MhdTypicalSheetCommand.RunInCommandContext(doc, handles, sheets => Dispatcher.BeginInvoke(new Action(() =>
            {
                var message = sheets == null
                    ? "הגיליון לא נוצר (הבחירה בוטלה או שהפעולה נכשלה — ראה שורת הפקודה)"
                    : sheets.Count == 0
                        ? "אין בשרטוט חתכים טיפוסיים בפורמט הגיליון — יש ליצור אותם קודם ('חתכים טיפוסיים')"
                        : sheets.Count == 1
                            ? $"נוצר גיליון {sheets[0]} והוא פתוח"
                            : $"נוצרו {sheets.Count} גיליונות ({string.Join(", ", sheets)}); הראשון פתוח";
                SetStatus(message);
                Log(message);
            })));
        }
        catch (Exception ex) { ShowError("גיליון חתכים", ex); }
    }

    /// <summary>Zoom margins, as a factor of the extents (the zoom refuses anything below 1).</summary>
    internal const double TypicalSectionZoomMargin = 1.25, TypicalAllSectionsZoomMargin = 1.10;

    /// <summary>Zooms to one created section block (the result window's "הצג"); says so when it cannot.</summary>
    private void ZoomToTypicalSection(string blockHandle)
    {
        var doc = Doc();
        if (doc == null) return;
        try
        {
            Autodesk.AutoCAD.DatabaseServices.Extents3d? extents = null;
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                if (doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(blockHandle, 16)), out var id) &&
                    !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is Entity entity)
                    extents = entity.GeometricExtents;
            }
            if (extents is not { } ext)
            {
                SetStatus("החתך לא נמצא בשרטוט — ייתכן שנמחק");
                return;
            }
            var shown = NativeViewZoomService.TryZoom(doc, ext, TypicalSectionZoomMargin);
            MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Info(
                $"typical section {blockHandle} shown={shown}: {NativeViewZoomService.LastDiagnostic}");
            if (shown) return;
            SetStatus("לא הצלחתי להציג את החתך — עבור לחלון השרטוט ונסה שוב");
            Log("הצגת חתך לא בוצעה: " + NativeViewZoomService.LastDiagnostic);
        }
        catch (Exception ex) { ShowError("הצגת חתך", ex); }
    }
}
