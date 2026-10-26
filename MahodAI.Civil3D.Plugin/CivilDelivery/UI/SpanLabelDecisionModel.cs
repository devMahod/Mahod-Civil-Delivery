using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// XAML-free state of the strip-label dialog: the rows, the approval rules and the
    /// batch actions. The dialog only binds and forwards clicks, so the exact gesture
    /// that crashed Civil on 2026-09-03 (un-check a row, then change its name) is
    /// exercised by a plain unit test on every build, without AutoCAD or WPF.
    /// </summary>
    internal sealed class SpanLabelDecisionModel
    {
        public const int MaxLabelLength = 80;

        public static readonly IReadOnlyList<string> LabelChoices = new[]
        {
            "נתיב נסיעה", "נת\"צ", "נתיב אופניים", "שביל אופניים", "מדרכה", "אי תנועה",
            "מפרדה", "חניה", "רצועת חניה", "גינון", "רצועת גינון", "שול", "רצועת בטיחות",
            "דרך שירות", "רצועת דרך",
        };

        // Immutable display copies: viewing/selecting history cannot mutate the profile.
        public sealed record PreviousName(string Label, double FromOffsetM, double ToOffsetM,
            string ApprovedBy, DateTime? ApprovedAtUtc)
        {
            public string Summary => $"{Label} · רוחב קודם {ToOffsetM - FromOffsetM:F3} מ׳ · " +
                $"גבולות {Bidi.Ltr($"{FromOffsetM:F3} → {ToOffsetM:F3}")} · " +
                $"{ApprovedBy} · {ApprovedAtUtc:yyyy-MM-dd}";
        }

        public sealed class SpanRow : INotifyPropertyChanged
        {
            private string _label = string.Empty;
            private bool _isApproved;

            public required SectionPlanRecord Record { get; init; }
            public required SectionUnresolvedSpanPlan Span { get; init; }
            public string CurrentLabel { get; init; } = string.Empty;
            public bool IsCurrentNamed => Span.Reason == SectionSpanEditTargets.ResolvedEditReason;
            public string ReviewState => IsCurrentNamed ? "שם נוכחי — ניתן לעריכה" : "נדרש אישור חדש";
            public IReadOnlyList<PreviousName> PreviousNames { get; init; } = Array.Empty<PreviousName>();
            public PreviousName? SelectedPreviousName { get; set; }
            public string PreviousSummary => PreviousNames.Count == 0 ? "אין החלטה קודמת חופפת" :
                string.Join("; ", PreviousNames.Select(item => $"{item.Label} · {item.ToOffsetM - item.FromOffsetM:F3} מ׳"));
            public string CurrentSummary => $"{Section} · גבולות נוכחיים " +
                $"{Bidi.Ltr($"{Span.FromOffsetM:F3} → {Span.ToOffsetM:F3}")} · רוחב {Span.WidthM:F3} מ׳";
            public string Section => Record.SectionId ?? Record.Cl.SourceHandle;
            public string Alignment => Bidi.Ltr(Record.SelectedAlignment ?? "—");
            public string Station => Record.Station?.ToString("F2", CultureInfo.CurrentCulture) ?? "—";
            public string From => Span.FromOffsetM.ToString("F2", CultureInfo.CurrentCulture);
            public string To => Span.ToOffsetM.ToString("F2", CultureInfo.CurrentCulture);
            public string Width => Span.WidthM.ToString("F2", CultureInfo.CurrentCulture) + " מ׳";
            public string Bounds => $"{KindText(Span.LeftKind)} ↔ {KindText(Span.RightKind)}";
            public string Reason => Span.Reason switch
            {
                "conflicting-strip-label-evidence" => "ראיות סותרות",
                // SEC-M5 (review of 1.3.9): never named automatically — the engineer
                // decides whether this is the bus lane.
                SectionProjectionLogic.BusLaneLineEdgeReason => "צמוד לקו נת\"צ — נת\"צ או נתיב נסיעה? לאישור",
                _ => "אין זיהוי חד־משמעי",
            };
            public bool HasLocalConflict => Span.Reason == "conflicting-strip-label-evidence";
            public bool HasUsableSuggestion => !HasLocalConflict &&
                !string.IsNullOrWhiteSpace(Span.SuggestedLabel) &&
                SectionSpanSuggestionLogic.IsCredibleLabel(Span.SuggestedLabel, Span.WidthM);
            public IReadOnlyList<SectionSpanLabelOverridePlan> LocalEvidence =>
                Record.PresentationCoverage.ExplicitSpanOverrides.Where(item =>
                    item.OffsetM > Span.FromOffsetM && item.OffsetM < Span.ToOffsetM).ToArray();
            public string Homology => Span.HomologousSpanCount > 1
                ? $"{Span.HomologousSpanCount} חתכים דומים"
                : "ייחודי";
            public string Suggestion => !HasUsableSuggestion
                ? HasLocalConflict || Span.SuggestionConfidence == "conflict"
                    ? "סתירה — אין הצעה"
                    : "—"
                : $"{Span.SuggestedLabel} · {ConfidenceText(Span.SuggestionConfidence)}";
            public string Evidence => HasLocalConflict
                ? string.Join("; ", LocalEvidence.Select(item => $"{item.Label} ({item.Source})").Distinct())
                : Span.SuggestionSupportCount > 0
                ? $"{Span.SuggestionSupportCount} חתכי מקור · " +
                  string.Join(", ", Span.SuggestionSources)
                : Span.SuggestionConfidence == "conflict"
                    ? $"{Span.SuggestionConflictCount} שמות סותרים"
                    : "אין ראיית מקור הומולוגית";
            public IReadOnlyList<string> Choices => LabelChoices;

            /// <summary>Choosing or typing a name is the approval gesture: it marks the row.</summary>
            public string Label
            {
                get => _label;
                set
                {
                    var next = value ?? string.Empty;
                    if (string.Equals(_label, next, StringComparison.Ordinal)) return;
                    _label = next;
                    OnPropertyChanged(nameof(Label));
                    if (!string.IsNullOrWhiteSpace(next) && !_isApproved)
                        IsApproved = true;
                }
            }

            public bool IsApproved
            {
                get => _isApproved;
                set
                {
                    if (_isApproved == value) return;
                    _isApproved = value;
                    OnPropertyChanged(nameof(IsApproved));
                }
            }

            public bool HasValidLabel =>
                !string.IsNullOrWhiteSpace(_label) && _label.Trim().Length <= MaxLabelLength;

            /// <summary>
            /// The same credibility rule the profile writer enforces, shown in Hebrew
            /// before the window closes (live 2026-09-03: a 2.01 m strip named נת"צ was
            /// refused only after the dialog had closed, in English). Null = credible.
            /// </summary>
            public string? LabelProblem
            {
                get
                {
                    if (!HasValidLabel) return null;
                    var label = _label.Trim();
                    var vehicle = SectionFurnitureLogic.VehicleForStrip(label);
                    if (vehicle == null) return null;
                    var width = Span.WidthM;
                    var widthText = width.ToString("F2", CultureInfo.CurrentCulture);
                    if (!SectionFurnitureLogic.FitsStrip(vehicle, width))
                    {
                        var needed = SectionFurnitureLogic.MinimumStripWidthM(vehicle)
                            .ToString("F2", CultureInfo.CurrentCulture);
                        return $"«{label}» לא מתאים לרוחב {widthText} מ׳ — {vehicle.Label} צריך לפחות " +
                               $"{needed} מ׳; בחר שם אחר (למשל מדרכה / אי תנועה)";
                    }
                    if (width > SectionFurnitureLogic.MaxSingleVehicleStripWidthM)
                        return $"«{label}» ברוחב {widthText} מ׳ הוא יותר מנתיב אחד — " +
                               "יש להוסיף גבולות במקור במקום שם אחד לכל הרוחב";
                    return null;
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            private static string KindText(string? kind) => kind switch
            {
                "curb" => "אבן שפה",
                "island" => "אי תנועה",
                "lane" => "שפת מיסעה",
                "sidewalk" => "מדרכה",
                "bike" => "שביל אופניים",
                "garden" => "גינון",
                "row" => "זכות דרך",
                null or "" => "?",
                _ => kind,
            };

            internal static string ConfidenceText(string? value) => value switch
            {
                "high" => "ביטחון גבוה",
                "medium" => "ביטחון בינוני",
                "low" => "ביטחון נמוך",
                "conflict" => "סתירה",
                _ => "ללא הצעה",
            };
        }

        public IReadOnlyList<SpanRow> Rows { get; }
        public int SectionCount { get; }
        public sealed record ReviewGroup(string Title, string Detail, IReadOnlyList<SpanRow> Rows);
        public IReadOnlyList<ReviewGroup> ReviewGroups { get; }
        private readonly IReadOnlyList<(SectionPlanRecord Record, string Snapshot)> _sourceSnapshots;

        /// <summary>Raised after any row changed; the dialog refreshes its buttons from it.</summary>
        public event Action? Changed;

        public SpanLabelDecisionModel(
            IReadOnlyList<SectionPlanRecord> records, bool initiallyApproveStrongSuggestions = true,
            IReadOnlyList<ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision>? previousDecisions = null,
            bool includeResolvedSpans = false)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (records.Count == 0)
                throw new ArgumentException("At least one section record is required.", nameof(records));
            var unresolved = records
                .Where(record => record.PresentationCoverage.UnresolvedSpans.Count > 0 ||
                    (includeResolvedSpans && record.PresentationCoverage.ResolvedSpans.Count > 0))
                .ToList();
            SectionCount = unresolved.Count;
            _sourceSnapshots = records.Select(record => (record, JsonSerializer.Serialize(record))).ToArray();
            Rows = unresolved
                .OrderBy(record => record.Station ?? double.MaxValue)
                .ThenBy(record => record.RecordId, StringComparer.Ordinal)
                .SelectMany(record => SectionSpanEditTargets.ForRecord(record, includeResolvedSpans)
                    .OrderBy(span => span.FromOffsetM)
                    .ThenBy(span => span.ToOffsetM)
                    .Select(span => new SpanRow
                    {
                        Record = record,
                        Span = span,
                        CurrentLabel = span.Reason == SectionSpanEditTargets.ResolvedEditReason ? span.SuggestedLabel ?? "" : "",
                        PreviousNames = PreviousNamesFor(record, span, previousDecisions),
                        Label = span.Reason == SectionSpanEditTargets.ResolvedEditReason ? span.SuggestedLabel ?? "" :
                            span.Reason != "conflicting-strip-label-evidence" &&
                            SectionSpanSuggestionLogic.IsCredibleLabel(span.SuggestedLabel ?? string.Empty, span.WidthM)
                                ? span.SuggestedLabel! : string.Empty,
                        IsApproved = span.Reason != SectionSpanEditTargets.ResolvedEditReason &&
                            initiallyApproveStrongSuggestions && span.StrongReviewCandidate &&
                            span.Reason != "conflicting-strip-label-evidence" &&
                            SectionSpanSuggestionLogic.IsCredibleLabel(span.SuggestedLabel ?? string.Empty, span.WidthM),
                    }))
                .ToList();
            foreach (var row in Rows)
                row.PropertyChanged += (_, _) => Changed?.Invoke();
            ReviewGroups = BuildReviewGroups();
        }

        private static IReadOnlyList<PreviousName> PreviousNamesFor(SectionPlanRecord record,
            SectionUnresolvedSpanPlan span,
            IReadOnlyList<ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision>? decisions) =>
            (decisions ?? Array.Empty<ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision>())
                .Where(item => SectionSpanPhysicalIdentity.SameSourceScope(record, item) &&
                    string.Equals(item.SourceHandle, record.Cl.SourceHandle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.AlignmentName, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Label) &&
                    item.FromOffsetM is { } from && item.ToOffsetM is { } to &&
                    double.IsFinite(from) && double.IsFinite(to) && to > from &&
                    Math.Min(to, span.ToOffsetM) > Math.Max(from, span.FromOffsetM))
                .OrderBy(item => item.FromOffsetM).ThenBy(item => item.ToOffsetM)
                .Select(item => new PreviousName(item.Label!, item.FromOffsetM!.Value, item.ToOffsetM!.Value,
                    item.ApprovedBy ?? "—", item.ApprovedAtUtc)).ToArray();

        public string? ReassignPreviousName(SpanRow row, PreviousName? previous)
        {
            RequireUnchangedEvidence();
            if (!Rows.Contains(row) || previous == null || !row.PreviousNames.Any(item => ReferenceEquals(item, previous)))
                return "יש לבחור החלטה קודמת של הרצועה הנוכחית; לא הועבר אישור";
            // Explicit new approval at CURRENT measured bounds; no old profile entry is touched here.
            return ApplyLabelToSelected(new[] { row }, previous.Label);
        }

        private IReadOnlyList<ReviewGroup> BuildReviewGroups()
        {
            // Groups reduce navigation; neither a shared boundary type nor a
            // repeated conflict is authority to assign the same material name.
            var groups = Rows.GroupBy(row =>
            {
                var alignment = row.Record.SelectedAlignment ?? "—";
                if (row.IsCurrentNamed)
                    return $"שמות נוכחיים לעריכה · תוואי {alignment}";
                if (row.HasLocalConflict)
                    return $"סתירת מקור · תוואי {alignment} · " + string.Join(" ↔ ",
                        row.LocalEvidence.Select(item => item.Label).Distinct().OrderBy(value => value, StringComparer.Ordinal));
                if (row.HasUsableSuggestion)
                    return $"הצעה לבדיקה · תוואי {alignment} · {row.Span.SuggestedLabel} · {SpanRow.ConfidenceText(row.Span.SuggestionConfidence)}";
                return $"שם חסר · תוואי {alignment} · {row.Bounds}";
            }).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
            {
                var rows = (IReadOnlyList<SpanRow>)group.ToArray();
                var sections = rows.Select(row => row.Section).Distinct().ToArray();
                var sources = rows.SelectMany(row => row.LocalEvidence)
                    .Where(item => !string.IsNullOrWhiteSpace(item.Evidence))
                    .Select(item => item.Evidence!).Distinct().ToArray();
                var detail = $"{rows.Count} רצועות ב־{sections.Length} חתכים · רוחב " +
                    $"{rows.Min(row => row.Span.WidthM):F2}–{rows.Max(row => row.Span.WidthM):F2} מ׳. " +
                    $"חתכים: {string.Join(", ", sections)}. " +
                    (sources.Length > 0 ? $"{sources.Length} ראיות מקור נפרדות. " : string.Empty) +
                    "הקבוצה מיועדת לסקירה בלבד. בדוק כל רצועה; בחירת קבוצה אינה אישור שם.";
                return new ReviewGroup($"{group.Key} ({rows.Count})", detail, rows);
            }).ToList();
            groups.Insert(0, new ReviewGroup($"כל הרצועות ({Rows.Count})",
                $"{groups.Count} קבוצות לסקירה. אפשר לבחור קבוצה, לבדוק את השורות ולתת שם משותף רק לשורות שבחרת.", Rows));
            return groups;
        }

        public IReadOnlyList<SpanRow> RowsForReview(ReviewGroup group)
        {
            if (!ReviewGroups.Any(item => ReferenceEquals(item, group)))
                throw new InvalidOperationException("קבוצת הסקירה אינה שייכת לתכנון שהוצג.");
            RequireUnchangedEvidence();
            return group.Rows;
        }

        private void RequireUnchangedEvidence()
        {
            if (_sourceSnapshots.Any(item => !string.Equals(item.Snapshot,
                    JsonSerializer.Serialize(item.Record), StringComparison.Ordinal)))
                throw new InvalidOperationException("מקור או ראיית תכנון השתנו — פתח את סקירת הרצועות מחדש. לא אושרו שמות.");
        }

        public int ApprovedCount => Rows.Count(row => row.IsApproved);
        public int InvalidApprovedCount => Rows.Count(row => row.IsApproved && !row.HasValidLabel);
        public int ProblemApprovedCount => Rows.Count(row => row.IsApproved && row.LabelProblem != null);

        public bool CanSave(string? approver) =>
            ApprovedCount > 0 && InvalidApprovedCount == 0 && ProblemApprovedCount == 0 &&
            !string.IsNullOrWhiteSpace(approver);

        public string Validation(string? approver)
        {
            if (CanSave(approver))
                return $"יישמרו {ApprovedCount} שמות מסומנים; יתר הרצועות יישארו לבדיקה";
            if (ApprovedCount == 0)
                return "בחר שם בעמודה «שם הרצועה» לפחות בשורה אחת";
            if (InvalidApprovedCount > 0)
                return $"{InvalidApprovedCount} שורות מסומנות בלי שם — בחר שם או בטל את הסימון";
            for (var i = 0; i < Rows.Count; i++)
            {
                var problem = Rows[i].IsApproved ? Rows[i].LabelProblem : null;
                if (problem != null) return $"שורה {i + 1}: {problem}";
            }
            return "יש לציין מאשר/ת";
        }

        /// <summary>Applies one name to the given rows; returns an error message or null.</summary>
        public string? ApplyLabelToSelected(IReadOnlyList<SpanRow> selected, string? label)
        {
            RequireUnchangedEvidence();
            var text = (label ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > MaxLabelLength)
                return "יש לבחור שם חוקי לפני החלה על השורות המסומנות";
            if (selected.Count == 0)
                return "יש לסמן לפחות שורה אחת בטבלה (לחיצה על השורה)";
            if (selected.Distinct().Count() != selected.Count ||
                selected.Any(row => !Rows.Contains(row)))
                return "הבחירה אינה שייכת לשורות התכנון שהוצגו";
            var unsuitable = selected.FirstOrDefault(row =>
                !SectionSpanSuggestionLogic.IsCredibleLabel(text, row.Span.WidthM));
            if (unsuitable != null)
                return $"השם «{text}» אינו מתאים לרוחב {unsuitable.Width} בחתך {unsuitable.Section}; לא שונו שמות בקבוצה";
            foreach (var row in selected)
            {
                row.Label = text;
                row.IsApproved = true;
            }
            return null;
        }

        public void MarkStrongSuggestions()
            => MarkStrongSuggestions(Rows);

        public string? MarkStrongSuggestions(IReadOnlyList<SpanRow> selected)
        {
            RequireUnchangedEvidence();
            var error = ValidateSelection(selected);
            if (error != null) return error;
            foreach (var row in selected.Where(row => !row.IsCurrentNamed && row.Span.StrongReviewCandidate &&
                         row.HasUsableSuggestion && row.HasValidLabel && row.LabelProblem == null &&
                         string.Equals(row.Label, row.Span.SuggestedLabel, StringComparison.Ordinal)))
                row.IsApproved = true;
            return null;
        }

        public void ClearApprovals()
        {
            foreach (var row in Rows) row.IsApproved = false;
        }

        public string? ClearApprovals(IReadOnlyList<SpanRow> selected)
        {
            RequireUnchangedEvidence();
            var error = ValidateSelection(selected);
            if (error != null) return error;
            foreach (var row in selected) row.IsApproved = false;
            return null;
        }

        private string? ValidateSelection(IReadOnlyList<SpanRow> selected) => selected.Count == 0
            ? "יש לבחור לפחות שורה אחת בטבלה; לא שונו סימונים"
            : selected.Distinct().Count() != selected.Count || selected.Any(row => !Rows.Contains(row))
                ? "הבחירה אינה שייכת לשורות התכנון שהוצגו" : null;

        public IReadOnlyList<SectionDecisionProfileService.SpanLabelBatchApproval> GetValidatedApprovals()
        {
            RequireUnchangedEvidence();
            return Approvals;
        }

        public IReadOnlyList<SectionDecisionProfileService.SpanLabelBatchApproval> Approvals =>
            Rows.Where(row => row.IsApproved)
                .Select(row => new SectionDecisionProfileService.SpanLabelBatchApproval(
                    row.Record, row.Span, row.Label.Trim())).ToList();
    }
}
