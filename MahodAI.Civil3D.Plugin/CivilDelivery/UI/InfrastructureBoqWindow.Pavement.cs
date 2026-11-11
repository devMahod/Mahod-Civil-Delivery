using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using NetTopologySuite.Geometries.Prepared;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;
using Visibility = System.Windows.Visibility;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The pavement-structure report in the bill of quantities: read the report (MahodAI), find its segments on the plan
/// (centre lines + section numbers), split every road surface between the segments, and price each part by its own
/// structure. Without a report (or for areas the report does not cover) the engineer's hand-typed structure applies, else
/// the default structure of the road type chosen for the drawing or marked area — always labelled as a default on the
/// screen, on every BOQ row and in the Excel (coworkers' call, 2026-11-09: the report is optional, defaults must be visible).
/// </summary>
internal sealed partial class InfrastructureBoqWindow
{
    private PavementReportSet _reports = PavementReportSet.Empty;
    private string _pavementPath = "";
    private IReadOnlyList<string>? _pendingReports;
    private bool _readingReport;
    private readonly CancellationTokenSource _closing = new();
    private PavementCentrelineReader.Result? _centrelines;
    private IReadOnlyList<Estimate.InfrastructureDrawingService.SurfaceShape>? _shapes;
    private readonly List<string> _shapeFailures = new();
    private PavementSegmentBands.Result? _bands;
    /// <summary>m² of each measured group (Source, Layer, GroupPart) in each zone; null without a report.</summary>
    private Dictionary<(string Source, string Layer, string Part), Dictionary<PavementReportPlan.Zone, double>>? _zoneAreas;
    private PavementReportPlan.Result? _plan;
    private HashSet<(string, string, string)>? _zoneKeys;
    private readonly StackPanel _reportCard = new();
    private readonly Border _pavementBanner = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 0) };
    private readonly System.Windows.Controls.DataGrid _zoneGrid = Table();
    /// <summary>The road type whose default structures price what neither the report nor the engineer covers; null = off.</summary>
    private PavementDefaultClass? _defaultClass;
    private string _defaultClassPath = "";

    private static (string, string, string) GroupKey(InfrastructureLine l) => (l.Source, l.Layer, l.GroupPart);
    private static (string, string, string) GroupKey(InfrastructureGroup g) => (g.Source, g.Layer, g.GroupPart);

    /// <summary>The drawing's attached reports live beside its review (per drawing, not per marked area).</summary>
    internal static string PavementPathFor(string drawing) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodCivilDelivery", "infrastructure-reviews",
        Path.ChangeExtension(InfrastructureReviewStore.FileName(drawing), ".pavement.json"));

    /// <summary>The road type chosen for default structures: one for the whole drawing, one for its marked area.</summary>
    internal static string DefaultChoicePathFor(string drawing, bool area) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodCivilDelivery", "infrastructure-reviews",
        Path.ChangeExtension(InfrastructureReviewStore.FileName(drawing), area ? ".area.pavement-default.json" : ".pavement-default.json"));

    /// <summary>The choice a run starts from: the scope's own, else (for an area) the drawing's.</summary>
    internal static string? SavedDefaultChoice(string drawing, bool area) =>
        PavementDefaults.LoadChoice(DefaultChoicePathFor(drawing, area)) ?? (area ? PavementDefaults.LoadChoice(DefaultChoicePathFor(drawing, false)) : null);

    /// <summary>Notes for the roads sheet: unpriced areas, unlocated segments, the report's own doubts.</summary>
    private IReadOnlyList<string> PavementNotes()
    {
        var notes = new List<string>();
        var segments = _reports.Reports.Sum(r => r.Content.Segments.Count);
        if (segments > 0 && _bands is { Bands.Count: 0 })
        {
            // Nothing located at all: one reason, not one line per segment, and not "attach the second report".
            var area = (_plan?.Zones ?? Array.Empty<PavementReportPlan.ZoneRow>()).Where(z => !z.Priced).Sum(z => z.AreaSquareMetres);
            notes.Add($"אף אחד מ־{segments} מקטעי הדו״ח לא אותר — אין בשרטוט קווי ציר עם מספרי חתכים" +
                      (_reports.FallbackFamily != null ? $"; לכל הכביש נבחר מבנה {_reports.FallbackFamily}" : area > 0 ? $"; {area:N0} מ״ר לא תומחרו לפי מבנה" : ""));
            notes.AddRange((_plan?.Notes ?? Array.Empty<string>()).Where(n => n.StartsWith(PavementReportPlan.DefaultsNote, StringComparison.Ordinal)));
        }
        else
        {
            notes.AddRange(_plan?.Notes ?? Array.Empty<string>());
            notes.AddRange(_bands?.Missing.Select(m => "מקטע שלא אותר בשרטוט: " + m) ?? Array.Empty<string>());
        }
        notes.AddRange(_reports.Reports.SelectMany(r => r.Content.Problems.Select(p => "הערת קריאת הדו״ח: " + p)));
        if (!_reports.HasReport && _plan?.Layers.Count is not > 0)
            notes.Add("לא צורף דו״ח מבנה מיסעה — שכבות האספלט והמצעים לא חושבו ולא תומחרו.");
        return notes;
    }

    /// <summary>Where the structures came from, for the checks sheet.</summary>
    private IEnumerable<string> PavementProvenance() => (_plan?.Defaults is { } d
        ? new[] { $"ערכי ברירת מחדל למבנה המיסעה: {d.Name} — {d.Traffic} — על {_plan.DefaultArea:N0} מ״ר. מבנים טיפוסיים, לא תכנון הפרויקט. מקור: {d.Source}" }
        : Array.Empty<string>()).Concat(_reports.Reports.Select((r, i) =>
        $"דו״ח מבנה מיסעה: {r.FileName} (SHA-256 {r.FileSha256[..Math.Min(16, r.FileSha256.Length)]}…, {r.Pages} עמ׳) — נקרא על ידי MahodAI {r.Model} / {r.PromptVersion} ב־{r.ReadUtc:yyyy-MM-dd HH:mm} UTC; " +
        $"{r.Content.Structures.Count} מבנים, {r.Content.Segments.Count} מקטעים; " +
        (r.Content.Segments.Count > 0 && _bands != null ? $"{_bands.Bands.Count(b => b.Report == i)} מקטעים אותרו על קווי הציר, רוחב רצועה ±{_reports.BandHalfWidthMetres:0.#} מ׳" : "ללא מקטעים — חל על כל השרטוט")));

    private void InitPavement(IReadOnlyList<string>? pendingReports, string? defaultClass)
    {
        _pavementPath = PavementPathFor(_scan.Drawing);
        _reports = PavementReportSet.Load(_pavementPath);
        // The road type for defaults is chosen per scope: the whole drawing, or the marked area (which starts from the
        // drawing's choice) — a marked stretch of a main road may need another type than the drawing's local streets.
        _defaultClassPath = DefaultChoicePathFor(_scan.Drawing, _scan.Area != null);
        _defaultClass = PavementDefaults.Resolve(defaultClass ?? SavedDefaultChoice(_scan.Drawing, _scan.Area != null));
        if (defaultClass != null) PavementDefaults.SaveChoice(_defaultClassPath, defaultClass);
        _pendingReports = pendingReports;
        DockPanel.SetDock(_pavementBanner, Dock.Top);
        _pavementBanner.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-banner");
        Column(_zoneGrid, "מקטע", "Zone", 135); Column(_zoneGrid, "דרך", "Road", 95); Column(_zoneGrid, "משטח", "SurfaceName", 85);
        Column(_zoneGrid, "שטח מ״ר", "AreaSquareMetres", 80, "N0"); Column(_zoneGrid, "מבנה", "Structure", 165); Column(_zoneGrid, "מקור", "OriginLabel", 90);
        Column(_zoneGrid, "שכבות", "Layers", new DataGridLength(1, DataGridLengthUnitType.Star));
        _zoneGrid.RowHeight = 30; PassWheelToPage(_zoneGrid);
        _zoneGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-zones");
        Closed += (_, _) => _closing.Cancel();
        Loaded += async (_, _) =>
        {
            if (_pendingReports is { Count: > 0 } files) { _pendingReports = null; await ReadReports(files, replace: true); }
            else if (_reports.HasReport) UiGuard.Run("דו״ח מבנה מיסעה", () => { ComputeZones(popups: true); RebuildRoads(); Refresh(); });
        };
        if (_reports.HasReport) { try { ComputeZones(popups: false); } catch (System.Exception ex) when (UiGuard.IsRecoverable(ex)) { _scan.Findings.Add("דו״ח מבנה: " + ex.Message); } }
    }

    // ───────────────────────────── reading ─────────────────────────────

    private async Task AttachReports(bool replace)
    {
        var files = PavementReportPrompt.PickFiles(this, _scan.Drawing);
        if (files.Count > 0) await ReadReports(files, replace);
    }

    private async Task ReadReports(IReadOnlyList<string> files, bool replace)
    {
        if (_readingReport) return;
        _readingReport = true;
        var previous = Cursor;
        var read = new List<AttachedPavementReport>();
        var errors = new List<string>();
        try
        {
            Cursor = Cursors.AppStarting;
            foreach (var file in files)
            {
                _message.Text = $"קורא את דו״ח המבנה ב־MahodAI: {Path.GetFileName(file)}… (עד כדקה)";
                UpdatePavementUi();
                try { read.Add(await PavementReportClient.ReadAsync(file, _closing.Token)); }
                catch (PavementReportException ex) { errors.Add($"{Path.GetFileName(file)}:\n{ex.Message}"); }
                if (_closed) return;
            }
        }
        catch (OperationCanceledException) { return; }
        finally { _readingReport = false; if (!_closed) Cursor = previous; }

        if (read.Count > 0)
        {
            var kept = replace ? Array.Empty<AttachedPavementReport>() : _reports.Reports.Where(r => read.All(n => n.FileSha256 != r.FileSha256)).ToArray();
            _reports = (replace ? PavementReportSet.Empty : _reports) with { Reports = kept.Concat(read).ToArray() };
            PavementReportSet.Save(_pavementPath, _reports);
            InvalidateZones();
        }
        if (errors.Count > 0)
            RtlMessageBox.Show("לא ניתן היה לקרוא את דו״ח המבנה:\n\n" + string.Join("\n\n", errors) +
                               "\n\n" + Fallback("שכבות המבנה") + " אפשר לצרף דו״ח אחר (PDF), או להזין מבנה ידנית בלוח \"כבישים\".",
                "דו״ח מבנה מיסעה", MessageBoxButton.OK, MessageBoxImage.Warning);
        UiGuard.Run("דו״ח מבנה מיסעה", () =>
        {
            if (read.Count > 0) ComputeZones(popups: true);
            RebuildRoads(); Refresh();
            if (read.Count > 0)
                _message.Text = $"נקרא דו״ח המבנה: {read.Sum(r => r.Content.Structures.Count)} מבנים, {read.Sum(r => r.Content.Segments.Count)} מקטעים · {_derived.Count} שורות מבנה בכתב הכמויות";
        });
    }

    private void RemoveReports()
    {
        if (RtlMessageBox.Show("להסיר את דו״חות המבנה מהשרטוט? " + Fallback("שכבות המבנה"), "דו״ח מבנה מיסעה",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _reports = PavementReportSet.Empty;
        PavementReportSet.Save(_pavementPath, _reports);
        InvalidateZones(); RebuildRoads(); Refresh();
    }

    private void InvalidateZones() { _zoneAreas = null; _bands = null; _plan = null; }

    /// <summary>What happens to an area without a report structure, in one sentence for a popup.</summary>
    private string Fallback(string what) => _defaultClass != null
        ? $"{what} יחושבו לפי ערכי ברירת מחדל של \"{_defaultClass.Name}\" — מסומנים כברירת מחדל בכל שורה."
        : $"{what} לא יתומחרו (ערכי ברירת מחדל כבויים).";

    // ───────────────────────────── zones ─────────────────────────────

    /// <summary>Splits every road surface between the report's zones. Popups (when asked) for what cannot be located.</summary>
    private void ComputeZones(bool popups)
    {
        InvalidateZones();
        if (!_reports.HasReport) return;
        var metres = _scan.MetresPerHostUnit;
        // Shapes of every surface layer (any disposition: the engineer may still switch one to new work).
        var surfaceKeys = SurfaceKeys();
        _zoneKeys = surfaceKeys;
        if (_shapes == null || _shapes.Any(s => !surfaceKeys.Contains(GroupKey(s.Line))) || surfaceKeys.Any(k => _shapes.All(s => GroupKey(s.Line) != k)))
        {
            _shapeFailures.Clear();
            _shapes = _scan.PlanShapes(l => surfaceKeys.Contains(GroupKey(l)), _shapeFailures);
        }

        var segmented = _reports.Reports.Select((r, i) => (Index: i, r.Content)).Where(r => r.Content.Segments.Count > 0).ToArray();
        var missingNote = new List<string>();
        var located = new HashSet<int>();
        if (segmented.Length > 0)
        {
            _centrelines ??= ReadCentrelines();
            _bands = PavementSegmentBands.Build(_centrelines.Lines, _centrelines.Labels,
                segmented.SelectMany(r => r.Content.Segments.Select(s => (r.Index, s))),
                _reports.BandHalfWidthMetres / metres, 15 / metres);
            foreach (var b in _bands.Bands) located.Add(b.Report);
            var total = segmented.Sum(r => r.Content.Segments.Count);
            if (popups && located.Count == 0)
            {
                RtlMessageBox.Show(
                    $"בדו״ח המבנה מוגדרים {total} מקטעים לפי מספרי חתכים (למשל {Sample(segmented)}), אך בשרטוט לא אותרו קווי ציר עם מספרי החתכים האלה.\n\n" +
                    $"נמצאו {_centrelines.Lines.Count} קווי ציר ו־{_centrelines.Labels.Count} מספרי חתך (מהם {_bands.LabelsOnLines} על קווי ציר).\n" +
                    "קווי ציר הם Alignments של Civil 3D, או קווים בשכבת ציר (למשל HW-ALGN, AX-…) עם מספרי החתכים לידם (למשל HW-ALGN-SEC-NAME). " +
                    "ודא ששרטוט קווי הציר (XREF) טעון.\n\n" +
                    "בלי קווי ציר לא ניתן לדעת איזה מבנה מהדו״ח חל על כל קטע כביש. " + Fallback("הכביש") + " אפשר לבחור בלוח \"כבישים\" מבנה אחד מהדו״ח לכל השרטוט.",
                    "חסרים קווי ציר", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (popups && _bands.Missing.Count > 0)
            {
                RtlMessageBox.Show($"{_bands.Missing.Count} מתוך {total} מקטעי הדו״ח לא אותרו בשרטוט:\n\n" + string.Join("\n", _bands.Missing.Take(12)) +
                                   (_bands.Missing.Count > 12 ? $"\n… ועוד {_bands.Missing.Count - 12}" : "") +
                                   "\n\nהשטח שלהם מסומן \"מחוץ למקטעי הדו״ח\". " + Fallback("שטחים אלה") + " אם השרטוט מחולק בין שני דו״חות — צרף גם את הדו״ח השני.",
                    "מקטעים שלא אותרו", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // Zones: located segment bands; a report without segments (or whose segments cannot be located and has a chosen
        // structure for the whole drawing) covers everything no band covers.
        var bands = (_bands?.Bands ?? Array.Empty<PavementSegmentBands.Band>())
            .Select(b => (Zone: new PavementReportPlan.Zone(b.Report, b.Segment), Prepared: PreparedGeometryFactory.Prepare(b.Shape), Geometry: b.Shape)).ToArray();
        var wholeReport = Enumerable.Range(0, _reports.Reports.Count)
            .FirstOrDefault(i => _reports.Reports[i].Content.Segments.Count == 0 || (!located.Contains(i) && _reports.FallbackFamily != null), -1);
        var rest = wholeReport >= 0 ? new PavementReportPlan.Zone(wholeReport, null) : PavementReportPlan.Zone.Outside;
        var areas = new Dictionary<(string, string, string), Dictionary<PavementReportPlan.Zone, double>>();
        foreach (var s in _shapes)
        {
            if (!areas.TryGetValue(GroupKey(s.Line), out var byZone)) areas[GroupKey(s.Line)] = byZone = new();
            var whole = s.Shape.Area; var inside = 0d;
            foreach (var band in bands)
            {
                if (!band.Prepared.Intersects(s.Shape)) continue;
                var a = band.Prepared.Contains(s.Shape) ? whole : s.Shape.Intersection(band.Geometry).Area;
                if (a <= 0) continue;
                byZone[band.Zone] = byZone.GetValueOrDefault(band.Zone) + a * metres * metres;
                inside += a;
            }
            var left = (whole - inside) * metres * metres;
            if (left > 0.01) byZone[rest] = byZone.GetValueOrDefault(rest) + left;
        }
        _zoneAreas = areas;
    }

    private HashSet<(string, string, string)> SurfaceKeys() =>
        _groups.Where(g => g.Surface != RoadSurfaceKind.None && g.Source != RoadPavement.DerivedSource && g.GroupPart != RoadCorridorVolumes.Part).Select(GroupKey).ToHashSet();

    /// <summary>The zones were split for another set of surface layers (the engineer changed a layer's surface kind).</summary>
    private bool ZonesStale() => _reports.HasReport && (_zoneAreas == null || _zoneKeys == null || !_zoneKeys.SetEquals(SurfaceKeys()));

    private static string Sample((int Index, PavementReportContent Content)[] segmented) =>
        segmented.SelectMany(r => r.Content.Segments).Select(s => $"{s.FromSection}–{s.ToSection}").FirstOrDefault() ?? "";

    private PavementCentrelineReader.Result ReadCentrelines()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את כתב הכמויות בשרטוט הנכון");
        using (doc.LockDocument()) return PavementCentrelineReader.Read(doc.Database);
    }

    /// <summary>The structure layers over the zones' NEW areas (excluded and existing areas do not count): the report's,
    /// else the hand-typed, else the road type's defaults.</summary>
    private PavementReportPlan.Result PlanFromReport()
    {
        var areas = new Dictionary<(PavementReportPlan.Zone, RoadSurfaceKind), double>();
        var rest = _reports.HasReport ? PavementReportPlan.Zone.Outside : PavementReportPlan.Zone.NoReport;
        foreach (var g in _groups.Where(g => g.Source != RoadPavement.DerivedSource && g.Surface != RoadSurfaceKind.None &&
                                             g.Disposition == InfrastructureDisposition.New && g.AreaSquareMetres > 0 && !_excluded.Contains(g)))
        {
            if (_reports.HasReport && _zoneAreas != null && _zoneAreas.TryGetValue(GroupKey(g), out var byZone))
                foreach (var (zone, a) in byZone) areas[(zone, g.Surface)] = areas.GetValueOrDefault((zone, g.Surface)) + a;
            else
                areas[(rest, g.Surface)] = areas.GetValueOrDefault((rest, g.Surface)) + g.AreaSquareMetres;
        }
        return PavementReportPlan.Quantities(_reports, areas.Select(a => new PavementReportPlan.ZoneArea(a.Key.Item1, a.Key.Item2, a.Value)), _structures, _defaultClass);
    }

    // ───────────────────────────── screen ─────────────────────────────

    private UIElement ReportCard()
    {
        _reportCard.Margin = new Thickness(0, 0, 0, 10);
        return _reportCard;
    }

    /// <summary>The banner under the tiles and the report card on the roads screen.</summary>
    private void UpdatePavementUi()
    {
        var awaiting = _groups.Where(g => g.AwaitingPavementReport && !_excluded.Contains(g)).Sum(g => g.AreaSquareMetres);
        var banner = new StackPanel();
        System.Windows.Media.Brush tint, frame;
        if (_readingReport)
        {
            banner.Children.Add(Text("קורא את דו״ח מבנה המיסעה ב־MahodAI… (עד כדקה)"));
            (tint, frame) = (Theme.ReviewTint, Theme.Review);
        }
        else if (!_reports.HasReport)
        {
            var row = new DockPanel();
            var attach = ActionButton("צרף דו״ח מבנה…", "GoButton", "sheet");
            attach.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-attach");
            attach.Click += async (_, _) => await AttachReports(replace: true);
            DockPanel.SetDock(attach, Dock.Left); row.Children.Add(attach);
            var byDefault = _plan?.DefaultArea ?? 0;
            // One short line; the full sentence is the tooltip (owner 2026-11-11).
            var t = Text(byDefault > 0 && _defaultClass != null
                ? $"⚠ ללא דו״ח מבנה — מבנה לפי ברירת מחדל: {_defaultClass.Name} ({byDefault:N0} מ״ר)"
                : awaiting > 0 ? $"ללא דו״ח מבנה — {awaiting:N0} מ״ר מיסעות לא תומחרו"
                : "ללא דו״ח מבנה — שכבות האספלט והמצעים אינן מחושבות");
            t.ToolTip = byDefault > 0 && _defaultClass != null
                ? $"שכבות האספלט והמצעים חושבו לפי ערכי ברירת מחדל ל\"{_defaultClass.Name}\". אלה מבנים טיפוסיים ולא תכנון הפרויקט — לחישוב מחייב צרף את דו״ח היועץ. מסומן \"ברירת מחדל\" בכל שורה ובייצוא."
                : "כבישים, נת״צ, מדרכות ושבילים אינם מתומחרים עד שיצורף הדו״ח או ייבחר סוג דרך לערכי ברירת מחדל (לוח \"כבישים\").";
            t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis;
            var defaults = byDefault > 0;
            t.FontWeight = FontWeights.SemiBold; t.Foreground = defaults ? Theme.Review : Theme.Blocked; t.Margin = new Thickness(12, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center;
            t.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-banner-text");
            row.Children.Add(t);
            banner.Children.Add(row);
            (tint, frame) = defaults ? (Theme.ReviewTint, Theme.Review) : (Theme.BlockedTint, Theme.Blocked);
        }
        else
        {
            var structures = _reports.Reports.Sum(r => r.Content.Structures.Count);
            var notes = (_plan?.Notes ?? Array.Empty<string>()).Concat(_bands?.Missing.Count > 0 ? new[] { $"{_bands.Missing.Count} מקטעים לא אותרו בשרטוט" } : Array.Empty<string>()).ToArray();
            // Every unpriced square metre once: the zones table already holds the awaiting areas (they sit in a zone with no structure).
            var unpriced = _plan != null ? _plan.UnpricedArea : awaiting;
            var byDefault = _plan?.DefaultArea ?? 0;
            var line = Text($"דו״ח מבנה: {string.Join(" · ", _reports.Reports.Select(r => Path.GetFileNameWithoutExtension(r.FileName)))} · {structures} מבנים · " +
                            $"{_reports.Reports.Sum(r => r.Content.Segments.Count)} מקטעים" +
                            (byDefault > 0 && _defaultClass != null ? $" · ⚠ {byDefault:N0} מ״ר ברירת מחדל" : "") +
                            (unpriced > 0 ? $" · {unpriced:N0} מ״ר לא תומחרו" : ""));
            line.TextWrapping = TextWrapping.NoWrap; line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.ToolTip = string.Join("\n", new[] { $"{_plan?.Layers.Count ?? 0} שכבות מבנה בכתב הכמויות" }
                .Concat(byDefault > 0 && _defaultClass != null ? new[] { $"{byDefault:N0} מ״ר שאין להם מבנה בדו״ח — לפי ערכי ברירת מחדל ל\"{_defaultClass.Name}\"" } : Array.Empty<string>())
                .Concat(notes));
            line.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-banner-text");
            banner.Children.Add(line);
            (tint, frame) = notes.Length > 0 || awaiting > 0 || byDefault > 0 ? (Theme.ReviewTint, Theme.Review) : (Theme.AccentTint, Theme.Accent);
        }
        _pavementBanner.Child = banner; _pavementBanner.Background = tint; _pavementBanner.BorderBrush = frame;
        var surfaces = _groups.Any(g => g.Surface != RoadSurfaceKind.None && g.AreaSquareMetres > 0 && g.Source != RoadPavement.DerivedSource);
        _pavementBanner.Visibility = surfaces || _reports.HasReport || _readingReport ? Visibility.Visible : Visibility.Collapsed;
        FillReportCard();
    }

    /// <summary>
    /// The report card on the roads screen. Owner 2026-11-11: too much text, details hidden by default — so the card shows
    /// the buttons, one summary line, the choices that change the price, and the zones table; the report's structures, its
    /// variants, the band width and the notes are closed sections under it.
    /// </summary>
    private void FillReportCard()
    {
        var c = _reportCard; c.Children.Clear();
        c.Children.Add(Heading("2  דו״ח מבנה מיסעה"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        var attach = ActionButton(_reports.HasReport ? "החלף דו״ח…" : "צרף דו״ח מבנה…", _reports.HasReport ? "PillButton" : "GoButton");
        attach.ToolTip = "דו״ח יועץ המיסעות (PDF). MahodAI קורא את המבנים והמקטעים, והמבנים נפרסים על שטחי הכבישים לפי קווי הציר.";
        attach.Click += async (_, _) => await AttachReports(replace: true);
        attach.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-replace");
        buttons.Children.Add(attach);
        if (_reports.HasReport)
        {
            var add = ActionButton("הוסף דו״ח נוסף…", "QuietButton"); add.Margin = new Thickness(6, 0, 0, 0);
            add.ToolTip = "שרטוט גדול המחולק בין שני דו״חות: כל דו״ח חל על המקטעים שלו.";
            add.Click += async (_, _) => await AttachReports(replace: false); buttons.Children.Add(add);
            var place = ActionButton("סמן מבנים בשרטוט", "PillPrimaryButton", "scan"); place.Margin = new Thickness(6, 0, 0, 0);
            place.ToolTip = "מצייר בשרטוט את שטחי כל מקטע בצבע המבנה שלו (שכבות MHD-BOQ-PAVE-*) ותווית מבנה לכל מקטע. סימון חוזר מחליף את הקודם; U מבטל.";
            place.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-place");
            place.Click += (_, _) => UiGuard.Run("סימון מבנים בשרטוט", PlaceOnDrawing); buttons.Children.Add(place);
            var remove = ActionButton("הסר", "QuietButton"); remove.Margin = new Thickness(6, 0, 0, 0);
            remove.Click += (_, _) => UiGuard.Run("הסרת דו״ח", RemoveReports); buttons.Children.Add(remove);
        }
        buttons.IsEnabled = !_readingReport;
        c.Children.Add(buttons);
        if (!_reports.HasReport)
        {
            DefaultsChooser(c);
            ZonesTable(c);
            return;
        }

        // One line per report; what it says (structures, variants, its own doubts) is closed under it.
        for (var i = 0; i < _reports.Reports.Count; i++)
        {
            var r = _reports.Reports[i]; var d = r.Content.Document;
            var details = new StackPanel { Margin = new Thickness(10, 0, 0, 4) };
            var head = Text(string.Join(" · ", new[] { d.Title ?? r.FileName, d.Consultant, d.Date }.Where(x => !string.IsNullOrWhiteSpace(x))) +
                            $"\nנקרא על ידי MahodAI ({r.Model}) {r.ReadUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {r.FileName}");
            head.FontSize = 11.5; head.Foreground = Theme.Muted; details.Children.Add(head);
            foreach (var s in r.Content.Structures)
            {
                var line = Text($"{s.ShortName} — {UseLabel(s.Use)}: {s.LayersDisplay}" + (s.TotalMatches ? "" : $" ⚠ בדו״ח: {s.StatedTotalCm:0.#} ס״מ") + (s.Page is { } p ? $" (עמ׳ {p})" : ""));
                line.Margin = new Thickness(0, 1, 0, 1); if (!s.TotalMatches) line.Foreground = Theme.Review;
                details.Children.Add(line);
            }
            foreach (var family in PavementReportPlan.RoadFamilies(r.Content))
            {
                var variants = PavementReportPlan.Variants(r.Content, family);
                if (variants.Select(v => v.Variant).Distinct().Count() < 2) continue;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
                row.Children.Add(Text($"מבנה {family} חל כ־"));
                var pick = new ComboBox { MinWidth = 210, Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0), ItemsSource = variants.Select(v => VariantLabel(v.Variant)).Distinct().ToArray() };
                pick.SelectedItem = VariantLabel(_reports.VariantOf(i, family));
                var report = i;
                pick.SelectionChanged += (_, _) => UiGuard.Run("בחירת מבנה", () =>
                {
                    var chosen = variants.FirstOrDefault(v => VariantLabel(v.Variant) == pick.SelectedItem as string)?.Variant ?? "full";
                    var map = new Dictionary<string, string>(_reports.Variants) { [PavementReportSet.Key(report, family)] = chosen };
                    _reports = _reports with { Variants = map }; PavementReportSet.Save(_pavementPath, _reports);
                    RebuildRoads(); Refresh();
                });
                row.Children.Add(pick);
                details.Children.Add(row);
            }
            var doubts = r.Content.Problems.Concat(r.Warnings).ToArray();
            foreach (var p in doubts) { var t = Text("⚠ " + p); t.Foreground = Theme.Review; t.Margin = new Thickness(0, 1, 0, 1); details.Children.Add(t); }
            var title = (_reports.Reports.Count > 1 ? $"דו״ח {i + 1}: " : "") + $"{Path.GetFileNameWithoutExtension(r.FileName)} · {r.Content.Structures.Count} מבנים · {r.Content.Segments.Count} מקטעים" +
                        (doubts.Length > 0 ? $" · ⚠ {doubts.Length}" : "");
            c.Children.Add(Details("report-" + i, title, details, doubts.Length > 0 ? Theme.Review : Theme.Ink));
        }
        // Where the report's segments could not be found, the engineer may still say which structure holds everywhere: a
        // choice that prices the whole road stays on screen.
        var families = _reports.Reports.SelectMany(r => PavementReportPlan.RoadFamilies(r.Content)).Distinct().ToArray();
        var unlocated = _reports.Reports.Select((r, i) => (r, i)).Any(x => x.r.Content.Segments.Count > 0 && (_bands == null || _bands.Bands.All(b => b.Report != x.i)));
        if (unlocated || _reports.FallbackFamily != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            row.Children.Add(Text("המקטעים לא אותרו בשרטוט. מבנה לכל הכביש:"));
            var pick = new ComboBox { MinWidth = 120, Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0),
                ItemsSource = new[] { "— אל תתמחר" }.Concat(families.Select(f => "מבנה " + f)).ToArray() };
            pick.SelectedItem = _reports.FallbackFamily == null ? "— אל תתמחר" : "מבנה " + _reports.FallbackFamily;
            pick.SelectionChanged += (_, _) => UiGuard.Run("מבנה לכל השרטוט", () =>
            {
                var chosen = pick.SelectedItem is string s && s.StartsWith("מבנה ", StringComparison.Ordinal) ? s["מבנה ".Length..] : null;
                _reports = _reports with { FallbackFamily = chosen }; PavementReportSet.Save(_pavementPath, _reports);
                ComputeZones(popups: false); RebuildRoads(); Refresh();
            });
            row.Children.Add(pick);
            c.Children.Add(row);
        }
        DefaultsChooser(c);
        var notes = PavementNotes().Where(n => !n.StartsWith("הערת קריאת", StringComparison.Ordinal)).ToArray();
        if (_reports.Reports.Any(r => r.Content.Segments.Count > 0) || notes.Length > 0)
        {
            var more = new StackPanel { Margin = new Thickness(10, 0, 0, 4) };
            if (_reports.Reports.Any(r => r.Content.Segments.Count > 0))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
                row.Children.Add(Text("רוחב רצועת המקטע מכל צד של קו הציר (מ׳):"));
                var width = new TextBox { Text = _reports.BandHalfWidthMetres.ToString("0.#", CultureInfo.InvariantCulture), Width = 60, Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0) };
                width.ToolTip = "כל שטח כביש עד מרחק זה מקו הציר, בין חתך ההתחלה לחתך הסוף, שייך למקטע. ברירת מחדל 30 מ׳.";
                width.LostFocus += (_, _) => UiGuard.Run("רוחב רצועה", () =>
                {
                    if (!double.TryParse(width.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) || w < 2 || w > 200) { width.Text = _reports.BandHalfWidthMetres.ToString("0.#", CultureInfo.InvariantCulture); return; }
                    if (Math.Abs(w - _reports.BandHalfWidthMetres) < 1e-9) return;
                    _reports = _reports with { BandHalfWidthMetres = w }; PavementReportSet.Save(_pavementPath, _reports);
                    ComputeZones(popups: false); RebuildRoads(); Refresh();
                });
                row.Children.Add(width);
                more.Children.Add(row);
            }
            foreach (var note in notes) { var t = Text("• " + note); t.Foreground = Theme.Review; t.FontSize = 12; more.Children.Add(t); }
            var located = _bands != null ? $"{_bands.Bands.Count} מקטעים אותרו על {_centrelines?.Lines.Count ?? 0} קווי ציר" : "מיקום המקטעים";
            c.Children.Add(Details("report-placement", located + (notes.Length > 0 ? $" · ⚠ {notes.Length} הערות" : ""), more, notes.Length > 0 ? Theme.Review : Theme.Ink));
        }
        ZonesTable(c);
    }

    private void ZonesTable(StackPanel c)
    {
        if (_plan is not { Zones.Count: > 0 }) return;
        c.Children.Add(Heading(_reports.HasReport ? "שטחים לפי מקטע ומבנה" : "שטחים ומבנה", 14));
        // Without a report every row is "the whole area" and no road is named: those columns only take the layers' room.
        _zoneGrid.Columns[0].Visibility = _plan.Zones.Select(z => z.Zone).Distinct().Count() > 1 ? Visibility.Visible : Visibility.Collapsed;
        _zoneGrid.Columns[1].Visibility = _plan.Zones.Any(z => z.Road.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        _zoneGrid.ItemsSource = _plan.Zones;
        c.Children.Add(_zoneGrid);
    }

    /// <summary>The road type whose default structures price what the report and the engineer leave without one. The
    /// picker is always on screen; what the type's structures are is a closed section under it.</summary>
    private void DefaultsChooser(StackPanel c)
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        var row = new WrapPanel();
        var label = Text(_reports.HasReport ? "ברירת מחדל לשטחים בלי מבנה בדו״ח:" : "בלי דו״ח — סוג דרך לברירת מחדל:");
        label.VerticalAlignment = VerticalAlignment.Center;
        label.ToolTip = "מבנים טיפוסיים, לא תכנון הפרויקט — מסומנים \"ברירת מחדל\" בכל שורה ובייצוא." +
                        (_scan.Area != null ? "\nהבחירה חלה על האזור המסומן בלבד; כתב הכמויות לכל השרטוט שומר את הבחירה שלו." : "");
        row.Children.Add(label);
        const string none = "— ללא ברירת מחדל (לא לתמחר)";
        var pick = new ComboBox { MinWidth = 260, Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0),
            ItemsSource = PavementDefaults.Classes.Select(k => k.Name).Prepend(none).ToArray() };
        pick.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-pavement-default-class");
        pick.SelectedItem = _defaultClass?.Name ?? none;
        pick.SelectionChanged += (_, _) => UiGuard.Run("ערכי ברירת מחדל", () =>
        {
            var chosen = PavementDefaults.Classes.FirstOrDefault(k => k.Name == pick.SelectedItem as string);
            _defaultClass = chosen;
            PavementDefaults.SaveChoice(_defaultClassPath, chosen?.Id ?? PavementDefaults.NoneId);
            RebuildRoads(); Refresh();
            _message.Text = chosen != null ? $"ערכי ברירת מחדל: {chosen.Name} — {chosen.Traffic}" : "ערכי ברירת מחדל כבויים — שטחים ללא מבנה אינם מתומחרים";
        });
        row.Children.Add(pick);
        box.Children.Add(row);
        if (_defaultClass != null)
        {
            var detail = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            var head = Text($"{_defaultClass.Traffic}. ⚠ ערכים טיפוסיים, לא תכנון הפרויקט — מסומנים \"ברירת מחדל\" בכל שורה ובייצוא.");
            head.Foreground = Theme.Review; head.FontSize = 12; detail.Children.Add(head);
            foreach (var s in _defaultClass.Structures)
            { var t = Text($"{UseLabel(s.Use)}: {s.LayersDisplay} — {string.Join(" + ", s.Layers.Select(l => l.Material))}"); t.FontSize = 11.5; detail.Children.Add(t); }
            var source = Text("מקור: " + _defaultClass.Source); source.FontSize = 11; source.Foreground = Theme.Muted; detail.Children.Add(source);
            var road = _defaultClass.StructureFor(RoadSurfaceKind.Road)?.LayersDisplay;
            box.Children.Add(Details("defaults-structures", "מה כולל המבנה" + (road != null ? $" · מיסעה: {road}" : ""), detail, Theme.TextSoft));
        }
        c.Children.Add(box);
    }

    private static string UseLabel(string use) => use switch
    {
        "road" => "כביש", "bus_lane" => "נת״צ", "road_and_bus_lane" => "כביש ונת״צ", "sidewalk" => "מדרכה", "bike_path" => "שביל אופניים",
        "shoulder" => "שול", "island" => "אי", "parking" => "חניה", "existing_road_overlay" => "כביש קיים — קרצוף וריבוד", _ => "אחר",
    };
    private static string VariantLabel(string variant) => variant switch
    {
        "full" => "מבנה מלא / החלפת מבנה", "widening" => "הרחבת נתיב קיים", "overlay" => "קרצוף וריבוד", "minimum" => "הרכב מינימלי", _ => "אחר",
    };

    // ───────────────────────────── drawing ─────────────────────────────

    /// <summary>Draws each zone's priced surface parts in its structure's colour, with a label per segment.</summary>
    private void PlaceOnDrawing()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !string.Equals(doc.Name, _scan.Drawing, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("שרטוט המקור אינו פעיל — פתח מחדש את כתב הכמויות בשרטוט הנכון");
        if (_shapes == null || _zoneAreas == null) ComputeZones(popups: true);
        var newSurface = _groups.Where(g => g.Source != RoadPavement.DerivedSource && g.Surface != RoadSurfaceKind.None && g.Disposition == InfrastructureDisposition.New && !_excluded.Contains(g))
            .GroupBy(GroupKey).ToDictionary(g => g.Key, g => g.First().Surface);
        var colours = new Dictionary<string, short>();
        short Colour(string structure) => colours.TryGetValue(structure, out var c) ? c : colours[structure] = PavementPlacement.ColourFor(colours.Count);
        var pieces = new List<PavementPlacement.Piece>(); var labels = new List<PavementPlacement.Label>();
        var bands = _bands?.Bands ?? Array.Empty<PavementSegmentBands.Band>();
        var metres = _scan.MetresPerHostUnit;
        foreach (var band in bands)
        {
            var zone = new PavementReportPlan.Zone(band.Report, band.Segment);
            var prepared = PreparedGeometryFactory.Prepare(band.Shape);
            var lines = new List<string> { "מקטע " + band.Segment.Segment + (band.Segment.RoadName is { Length: > 0 } road ? " · " + road : "") + $" ({band.Segment.FromSection}–{band.Segment.ToSection})" };
            foreach (var group in _shapes!.Where(s => newSurface.ContainsKey(GroupKey(s.Line)) && prepared.Intersects(s.Shape)).GroupBy(s => newSurface[GroupKey(s.Line)]))
            {
                var structure = PavementReportPlan.StructureFor(_reports, zone, group.Key);
                if (structure == null) continue;
                var name = structure.ShortName;
                lines.Add($"{FirmLayerConventions.SurfaceName(group.Key)}: {name} — {structure.LayersDisplay}");
                foreach (var s in group)
                {
                    var part = s.Shape.Intersection(band.Shape);
                    if (!part.IsEmpty && part.Area * metres * metres > 0.05) pieces.Add(new(part, name, Colour(name)));
                }
            }
            var at = band.Shape.InteriorPoint;
            labels.Add(new(string.Join("\n", lines), at.X, at.Y));
        }
        if (bands.Count == 0)
        {
            // A report without segments: every new surface gets its structure for the whole drawing.
            var zone = new PavementReportPlan.Zone(Math.Max(0, Enumerable.Range(0, _reports.Reports.Count).FirstOrDefault(i => _reports.Reports[i].Content.Segments.Count == 0, 0)), null);
            foreach (var s in _shapes!.Where(s => newSurface.ContainsKey(GroupKey(s.Line))))
                if (PavementReportPlan.StructureFor(_reports, zone, newSurface[GroupKey(s.Line)]) is { } structure)
                    pieces.Add(new(s.Shape, structure.ShortName, Colour(structure.ShortName)));
        }
        if (pieces.Count == 0) throw new InvalidOperationException("אין שטחים עם מבנה לסימון — בדוק שהדו״ח נקרא ושהמקטעים אותרו");
        var drawn = PavementPlacement.Place(doc, pieces, labels, 2.5 / metres);
        _message.Text = $"סומנו {drawn} שטחים ו־{labels.Count} תוויות בשכבות {PavementPlacement.LayerPrefix}* (סימון חוזר מחליף; U בשרטוט מבטל)";
    }
}
