using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>
/// Recognises which library family a measured group is from its CAD evidence, whatever its layer is called.
/// Host-free, deterministic and without network access. Every record is decided on its own evidence and the
/// group is then partitioned: records with the same proposed family form one proposal, records abstaining for the
/// same reason form one abstention. It never approves, prices or quantifies anything, and never invents a family:
/// when the evidence is missing, generic or contradictory it abstains and says what would resolve it.
/// <para>
/// Channels, strongest first. STRONG (may propose; any two that disagree abstain): the block name against the
/// library block patterns that are specific without a layer (arrow-D*) or whose rule's layer patterns the record's
/// layer matches (or the name's own vocabulary), block attributes, dynamic block properties, the PropertySet
/// component and a legend row matched by pattern/block/linetype. CANDIDATE (may propose alone only when unanimous
/// over a complete list, may narrow, never vetoes a strong decision): nearby text. SUPPORTING (narrowing only): a
/// short or layer-bound library block pattern on another layer ("M", "813*"), a text-like hatch pattern name and a
/// legend row matched by colour only; and one-way withholding by host-proven drawn width, dash linetype, closed
/// curves and the draft's painted-area measurement of wide lines on width-classified marking layers. WEAK (reported,
/// never used to choose): colour and the 6422 source-role convention.
/// </para>
/// <para>
/// Infrastructure subjects map to the library's infrastructure domain families (a line family, or its structures
/// family for counted blocks). A text or channel that names two subjects (a roads element and an infrastructure
/// domain, or two domains) abstains with both; a subject outside the library and the domains abstains.
/// </para>
/// </summary>
public sealed class LocalFamilyClassifier : IFamilyClassifier
{
    public const string Name = "mahod-local-family-classifier/1";

    public static LocalFamilyClassifier Instance { get; } = new();

    public string Identity { get; } = Name + "+" + FamilySignalTable.ShortFingerprint;

    private const int MaxObserved = 16, MaxInferred = 8, MaxMissing = 10, MaxRecordTexts = 10, MaxShownChars = 80;
    private const string ConstantWidthKey = "cad_polyline_constant_width_raw", MinWidthKey = "cad_polyline_width_min_raw",
        MaxWidthKey = "cad_polyline_width_max_raw", UnitsKey = "cad_entity_database_insunits";

    /// <summary>A '-'-joined chain of numbers in a linetype name ("DASHED1-1" → 1-1, "TR_10_3-3" → 3-3; '_' never joins).</summary>
    private static readonly Regex LinetypeNumberChain = new(@"(?<![0-9.])[0-9]+(?:\.[0-9]+)?(?:-[0-9]+(?:\.[0-9]+)?)+(?![0-9.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Above this host width (metres) the draft builder measures a line on a width-classified marking layer as painted
    /// area (EngineerBoqDraftBuilder: open-w10 ≤ 0.105, open-w15 ≤ 0.155, else painted-width). Keep the two in step.
    /// </summary>
    private const double PaintedAreaWidthMetres = 0.155;

    private const string TruncatedNearbyNote = "רשימת הטקסטים הסמוכים נקטעה (נקראו רק הקרובים ביותר) — טקסט שלא נקרא עשוי לסתור; " +
                                               "היא אינה מספיקה להצעה לבדה ואינה משמשת לצמצום.";

    private LocalFamilyClassifier() { }

    public IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group, EngineerBoqLibrary library, CatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(library);
        var records = group.Records ?? Array.Empty<NeutralQuantityRecord>();
        if (records.Count == 0) return Array.Empty<RecognitionProposal>();
        if (records.Any(r => r == null || string.IsNullOrWhiteSpace(r.RecordId)))
            throw new ArgumentException("Every record handed to the classifier needs a record id.", nameof(group));

        var context = new GroupContext(group, library);
        var groupNotes = SourceRoleNotes(group);
        List<Outcome> outcomes;
        if (records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            outcomes = new List<Outcome> { GroupRefusal(records[0], Refusal.DuplicateIds, "מזהי רשומה כפולים בקבוצה — הקבוצה לא סווגה; יש לתקן את הקלט.") };
        else if (group.SourceRole is DraftSourceRole.Survey or DraftSourceRole.ExistingUtilities)
            outcomes = records.Select(r => GroupRefusal(r, Refusal.ExistingSource,
                $"המקור {group.Source} הוא {(group.SourceRole == DraftSourceRole.Survey ? "תכנית מדידה (מצב קיים)" : "תשתיות קיימות")} — משפחות הספרייה הן עבודה חדשה; לא הוצעה משפחה.")).ToList();
        else if (context.Possible.Count == 0)
            outcomes = records.Select(r => GroupRefusal(r, Refusal.NoFamilyForMeasurement, context.NoFamilyReason)).ToList();
        else
            outcomes = records.Select(r => Analyze(r, context)).ToList();

        var proposals = Assemble(group, records, outcomes, context, groupNotes, catalog);
        return proposals;
    }

    // ------------------------------------------------------------------ group context

    private sealed class GroupContext
    {
        public GroupContext(RecognitionGroupInput group, EngineerBoqLibrary library)
        {
            Group = group;
            Library = library;
            Kind = (group.Kind ?? string.Empty).Trim().ToLowerInvariant();
            MethodClass = (group.MethodClass ?? string.Empty).Trim();
            for (var i = 0; i < library.Rules.Count; i++)
                Order.TryAdd(library.Rules[i].Id, i);
            Rules = library.Rules.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var unitConflict = UnitConflicts(Kind, group.Unit);
            if (!unitConflict)
                foreach (var rule in library.Rules.Where(r => Accepts(r, Kind, MethodClass)))
                    Possible.Add(rule.Id);
            NoFamilyReason = unitConflict
                ? $"יחידת המדידה ({group.Unit}) אינה תואמת את סוג המדידה ({Kind}) — לא הוצעה משפחה."
                : $"אין משפחה בספרייה שנמדדת כ{MeasurementLabel(Kind, MethodClass)} — לא הוצעה משפחה.";
            foreach (var rule in library.Rules)
                foreach (var emit in rule.Emits)
                {
                    var bare = FamilySignalTable.BareCode(emit.Code);
                    if (!EmitFamilies.TryGetValue(bare, out var list)) EmitFamilies[bare] = list = new List<string>();
                    if (!list.Contains(rule.Id)) list.Add(rule.Id);
                }
        }

        public RecognitionGroupInput Group { get; }
        public EngineerBoqLibrary Library { get; }
        public string Kind { get; }
        public string MethodClass { get; }
        public Dictionary<string, int> Order { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DraftRule> Rules { get; }
        public HashSet<string> Possible { get; } = new(StringComparer.Ordinal);
        public string NoFamilyReason { get; }
        public Dictionary<string, List<string>> EmitFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TextReading> _readings = new(StringComparer.Ordinal);

        public bool IsFamily(string id) => Rules.ContainsKey(id);

        public TextReading Read(string text)
        {
            if (!_readings.TryGetValue(text, out var reading)) _readings[text] = reading = FamilySignalTable.Read(text);
            return reading;
        }

        /// <summary>Library order for families; markers last, ordinal.</summary>
        public List<string> Ordered(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal)
            .OrderBy(id => Order.TryGetValue(id, out var index) ? index : int.MaxValue).ThenBy(id => id, StringComparer.Ordinal).ToList();

        public string Display(string id) => Rules.TryGetValue(id, out var rule) ? $"'{rule.DisplayName}' ({id})" : id;
    }

    /// <summary>
    /// Mirrors EngineerBoqDraftBuilder.Accepts/BasisAccepts (private there): a family is possible only when its
    /// basis accepts the group's measurement kind and method class. Keep the two in step.
    /// </summary>
    internal static bool Accepts(DraftRule rule, string kind, string methodClass) =>
        BasisAccepts(rule.Basis, kind, methodClass) || (rule.SplitByDrawnWidth && methodClass == "painted-width");

    private static bool IsOpenLine(string methodClass) => methodClass is "open" or "open-w10" or "open-w15" or "open-width-unproven";

    private static bool BasisAccepts(DraftQuantityBasis basis, string kind, string methodClass) => basis switch
    {
        DraftQuantityBasis.HatchArea => kind == "area" && methodClass == "hatch",
        DraftQuantityBasis.LengthWithClosedPerimeters => kind == "length" && (IsOpenLine(methodClass) || methodClass == "closed-perimeter"),
        DraftQuantityBasis.OpenLength => kind == "length" && IsOpenLine(methodClass),
        DraftQuantityBasis.Count => kind == "count",
        _ => false,
    };

    /// <summary>A unit that parses to a known dimension other than the kind's is a contradiction; an unknown unit is not.</summary>
    private static bool UnitConflicts(string kind, string? unit)
    {
        var dimension = Units.Parse(unit).Dimension;
        if (dimension == UnitDimension.Unknown) return false;
        return kind switch
        {
            "length" => dimension != UnitDimension.Length,
            "area" => dimension != UnitDimension.Area,
            "count" => dimension != UnitDimension.Count,
            "volume" => dimension != UnitDimension.Volume,
            _ => false,
        };
    }

    private static string MeasurementLabel(string kind, string methodClass) => (kind, methodClass) switch
    {
        ("area", "hatch") => "שטח הצללה",
        ("area", "closed-polyline") => "שטח פוליליין סגור",
        ("area", "painted-width") => "שטח צבוע לפי רוחב משורטט",
        ("length", "closed-perimeter") => "היקף פוליליין סגור",
        ("length", _) => "אורך קו פתוח",
        ("count", _) => "ספירת בלוקים",
        ("volume", _) => "נפח",
        _ => $"{kind}/{methodClass}",
    };

    private static string BasisLabel(DraftQuantityBasis basis) => basis switch
    {
        DraftQuantityBasis.HatchArea => "שטח הצללה",
        DraftQuantityBasis.LengthWithClosedPerimeters => "אורך (כולל היקפים סגורים)",
        DraftQuantityBasis.OpenLength => "אורך קווים פתוחים",
        DraftQuantityBasis.Count => "ספירת בלוקים",
        _ => basis.ToString(),
    };

    // ------------------------------------------------------------------ per record

    private enum Channel { Block, BlockPattern, Attributes, BlockProps, PropertySet, Legend, LegendByColor, NearbyText, HatchPattern }

    private enum Tier { Strong, Candidate, Supporting }

    /// <summary>One channel's families. <c>Partial</c>: read from a list the collector cut, so a text may be missing.</summary>
    private sealed record Signal(Channel Channel, Tier Tier, string Key, HashSet<string> Set, bool Partial = false);

    private enum Refusal { None, DuplicateIds, ExistingSource, NoFamilyForMeasurement }

    private enum Verdict { Proposed, NoEvidence, BasisMismatch, Existing, OtherSubject, Conflict, MixedSubjects, Ambiguous, Withheld, Refused }

    private static string AbstainKind(Verdict verdict) => verdict switch
    {
        Verdict.Conflict => RecognitionProposal.AbstainConflict,
        Verdict.MixedSubjects => RecognitionProposal.AbstainMixedSubjects,
        Verdict.NoEvidence => "no-evidence",
        Verdict.BasisMismatch => "basis-mismatch",
        Verdict.Existing => "existing",
        Verdict.OtherSubject => "other-subject",
        Verdict.Withheld => "withheld",
        Verdict.Refused => "refused",
        _ => "ambiguous",
    };

    private sealed class Outcome
    {
        public required NeutralQuantityRecord Record { get; init; }
        public Verdict Verdict { get; set; }
        public Refusal Refusal { get; set; }
        public string? Family { get; set; }
        public List<string> Alternatives { get; } = new();
        public Dictionary<string, string> Why { get; } = new(StringComparer.Ordinal);
        public List<string> Keys { get; } = new();
        public List<string> Observed { get; } = new();
        public List<string> Inferred { get; } = new();
        public List<string> Missing { get; } = new();

        public void Alternative(string family, string why)
        {
            if (Alternatives.Contains(family)) return;
            Alternatives.Add(family);
            Why[family] = why;
        }

        public void Cite(IEnumerable<string> keys)
        {
            foreach (var key in keys)
                if (!Keys.Contains(key)) Keys.Add(key);
        }
    }

    private static Outcome GroupRefusal(NeutralQuantityRecord record, Refusal refusal, string reason)
    {
        var outcome = new Outcome { Record = record, Verdict = Verdict.Refused, Refusal = refusal };
        outcome.Missing.Add(reason);
        return outcome;
    }

    private enum WidthKind { Unknown, Widthless, HostProven, Unproven }

    private sealed record WidthReading(WidthKind Kind, double Local, double Host, string? Why, IReadOnlyList<string> Keys);

    /// <summary>
    /// The one-way geometry of a record. <c>PaintedWidth</c> (host metres), when set: the draft builder will measure this
    /// line as painted area (a wide proven width on a width-classified marking layer), where only width-split families apply.
    /// </summary>
    private sealed record Geometry(WidthReading Width, string? Linetype, bool? Closed, double? PaintedWidth);

    private static Outcome Analyze(NeutralQuantityRecord record, GroupContext ctx)
    {
        var outcome = new Outcome { Record = record };
        var p = (IReadOnlyDictionary<string, string>?)record.Measurement?.Parameters ?? new Dictionary<string, string>();
        var signals = new List<Signal>();
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var boundUtility = new HashSet<string>(StringComparer.Ordinal);
        var unusable = new List<string>();
        var readTexts = 0;
        var readName = false;
        var weakCues = new List<string>();
        var layerLeaf = SectionProjectionLogic.LayerLeaf(record.Source?.Layer);
        if (layerLeaf.Length == 0) layerLeaf = ctx.Group.LayerLeaf ?? string.Empty;

        // An evidence schema we do not know is not read at all (fail closed). A record without ev_schema is legacy; an
        // ev_schema that is present must be read and exactly mahod-evidence/1 (as FamilyRecognitionAssist requires):
        // a missing or invalid status or another version means ev_* follow a contract this reader does not know.
        var evidenceReadable = EvidenceReader.SchemaReadable(p);
        if (!evidenceReadable)
            unusable.Add("גרסת חוזה הראיות אינה mahod-evidence/1 או שאינה קריאה — ראיות ev_* לא נקראו");

        // (a) Block channel: library block patterns first (the layer name may be random), else the block name's words.
        var blockStatus = EvidenceReader.Status(p, EvidenceKeys.BlockNameEffective);
        if (blockStatus.Usable && p.TryGetValue(EvidenceKeys.BlockNameEffective, out var rawBlock) &&
            EvidenceReader.Clean(SectionProjectionLogic.LayerLeaf(rawBlock)) is { } blockLeaf)
        {
            if (blockLeaf.StartsWith('*'))
                outcome.Observed.Add($"בלוק אנונימי ({Shown(blockLeaf)}) — שם כזה אינו ראיה");
            else
            {
                readName = true;
                outcome.Observed.Add($"בלוק: {Shown(blockLeaf)}");
                // A library block pattern is strong only with the context that makes it specific: a layer-free rule
                // whose pattern has at least three characters (arrow-D*), or a layer-bound rule whose layer patterns
                // this record's layer matches. A short or layer-bound pattern on another layer ("M" is marking-m-blocks
                // only on TR-MARK-ARW*, "813*" is a bare numeric code) is a supporting hint.
                var strongGlob = new List<string>();
                var weakGlob = new List<string>();
                foreach (var rule in ctx.Library.Rules.Where(r => r.BlockPatterns != null))
                {
                    var matching = rule.BlockPatterns!.Where(pattern => EngineerBoqLibrary.Glob(blockLeaf, pattern)).ToList();
                    if (matching.Count == 0) continue;
                    var layerFree = rule.LayerPatterns.Any(pattern => pattern.Trim() == "*");
                    var specific = layerFree
                        ? matching.Any(pattern => pattern.Count(c => c is not ('*' or '?')) >= 3)
                        : EngineerBoqLibrary.AnyGlob(layerLeaf, rule.LayerPatterns);
                    (specific ? strongGlob : weakGlob).Add(rule.Id);
                }
                foreach (var id in strongGlob.Where(id => !ctx.Possible.Contains(id))) rejected.Add(id);
                var byPattern = new HashSet<string>(strongGlob.Where(ctx.Possible.Contains), StringComparer.Ordinal);
                var byWords = LeafSet(blockLeaf, ctx, rejected, boundUtility);
                // A broad library pattern (BIKE*) and the name's own words (BIKE_RACK) must agree; if they do not,
                // the name is ambiguous rather than decided by the pattern. A name naming two subjects stays so.
                var both = Intersect(new[] { byPattern, byWords });
                var set = byPattern.Count == 0 ? byWords
                    : byWords.Count == 0 ? byPattern
                    : both.Count > 0 && !byWords.Contains(FamilySignalTable.MixedSubjectMarker) ? both
                    : Union(new List<HashSet<string>> { byPattern, byWords });
                signals.Add(new Signal(Channel.Block, Tier.Strong, EvidenceKeys.BlockNameEffective, set));
                var byWeakPattern = new HashSet<string>(weakGlob.Where(id => ctx.Possible.Contains(id) && !byPattern.Contains(id)), StringComparer.Ordinal);
                if (byWeakPattern.Count > 0)
                    signals.Add(new Signal(Channel.BlockPattern, Tier.Supporting, EvidenceKeys.BlockNameEffective, byWeakPattern));
            }
        }
        else if (blockStatus.State is EvidenceState.Unavailable or EvidenceState.Invalid)
            unusable.Add(UnusableNote(EvidenceKeys.BlockNameEffective, blockStatus));

        if (evidenceReadable)
        {
            // (b) Text channels on the object itself.
            foreach (var (key, channel) in new[]
                     {
                         (EvidenceKeys.BlockAttributes, Channel.Attributes), (EvidenceKeys.BlockProps, Channel.BlockProps),
                         (EvidenceKeys.PsetComponent, Channel.PropertySet),
                     })
            {
                if (!Usable(p, key, outcome, unusable)) continue;
                var leaves = new List<HashSet<string>>();
                foreach (var text in EvidenceReader.Texts(p, key))
                {
                    if (readTexts++ < MaxRecordTexts) outcome.Observed.Add(ObservedText(channel, text));
                    leaves.Add(LeafSet(text.Text, ctx, rejected, boundUtility));
                }
                signals.Add(new Signal(channel, Tier.Strong, key, Combine(leaves, existingDominates: true)));
            }

            // Legend row: a candidate the engineer has not verified. Matched by pattern/block/linetype it is strong
            // evidence; matched by colour only (or an unstated basis) it is a colour association and only narrows.
            if (Usable(p, EvidenceKeys.LegendRow, outcome, unusable) && EvidenceReader.TryObservation(p, EvidenceKeys.LegendRow, out var legend))
            {
                var strongRows = new List<HashSet<string>>();
                var colourRows = new List<HashSet<string>>();
                foreach (var row in ObservationItems(legend, "rows", "items").Take(8))
                {
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("text", out var textElement) ||
                        textElement.ValueKind != JsonValueKind.String || EvidenceReader.Clean(textElement.GetString()) is not { } text)
                        continue;
                    var match = row.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    var byShape = match is "pattern" or "block" or "linetype";
                    if (readTexts++ < MaxRecordTexts)
                        outcome.Observed.Add($"שורת מקרא (לא מאומתת, התאמה לפי {MatchLabel(match)}): {Shown(text)}");
                    (byShape ? strongRows : colourRows).Add(LeafSet(text, ctx, rejected, byShape ? boundUtility : null));
                }
                if (strongRows.Count > 0) signals.Add(new Signal(Channel.Legend, Tier.Strong, EvidenceKeys.LegendRow, Combine(strongRows, existingDominates: true)));
                if (colourRows.Count > 0) signals.Add(new Signal(Channel.LegendByColor, Tier.Supporting, EvidenceKeys.LegendRow, Combine(colourRows, existingDominates: false)));
            }

            // Nearby text: context, not truth. Texts touching or inside the element outrank farther ones.
            if (Usable(p, EvidenceKeys.NearbyText, outcome, unusable) && EvidenceReader.TryObservation(p, EvidenceKeys.NearbyText, out var nearby))
            {
                var items = new List<(string Text, double? Distance, HashSet<string> Set)>();
                foreach (var item in NearbyItems(nearby))
                {
                    if (readTexts++ < MaxRecordTexts) outcome.Observed.Add($"טקסט סמוך (מועמד בלבד): {Shown(item.Text)}");
                    items.Add((item.Text, item.Distance, LeafSet(item.Text, ctx, rejected)));
                }
                var matched = items.Where(i => i.Set.Count > 0).ToList();
                var touching = matched.Where(i => i.Distance is { } d && d <= FamilySignalTable.TouchingDistanceMetres).ToList();
                var used = touching.Count > 0 ? touching : matched;
                var union = Union(used.Select(i => i.Set).ToList());
                if (touching.Count > 0 && touching.Count < matched.Count)
                    outcome.Inferred.Add("טקסט הנוגע ברכיב או בתוכו קודם לטקסטים רחוקים יותר");
                // The collector keeps only the nearest texts: a cut list (even of touching texts) may miss one that disagrees.
                var partial = EvidenceReader.Status(p, EvidenceKeys.NearbyText).State == EvidenceState.Truncated;
                signals.Add(new Signal(Channel.NearbyText, Tier.Candidate, EvidenceKeys.NearbyText, union, partial));
            }

            // Hatch pattern: no seeded signatures; only a text-like pattern name (GRASS, a named custom pattern) narrows.
            if (Usable(p, EvidenceKeys.Hatch, outcome, unusable) && EvidenceReader.TryObservation(p, EvidenceKeys.Hatch, out var hatch) &&
                hatch.ValueKind == JsonValueKind.Object && hatch.TryGetProperty("pattern", out var pattern) &&
                pattern.ValueKind == JsonValueKind.String && EvidenceReader.Clean(pattern.GetString()) is { } patternName)
            {
                outcome.Observed.Add($"תבנית הצללה: {Shown(patternName)}");
                var set = LeafSet(patternName, ctx, rejected);
                set.RemoveWhere(id => id.StartsWith('#'));
                signals.Add(new Signal(Channel.HatchPattern, Tier.Supporting, EvidenceKeys.Hatch, set));
            }
        }

        // (c) Geometry: observed, and used only to withhold.
        var width = ReadWidth(record, p, evidenceReadable);
        switch (width.Kind)
        {
            case WidthKind.HostProven:
                outcome.Observed.Add($"רוחב משורטט במארח: {Metres(width.Host)} מ'");
                break;
            case WidthKind.Unproven:
                outcome.Observed.Add($"רוחב משורטט מקומי {Metres(width.Local)} — {width.Why}");
                break;
            case WidthKind.Widthless:
                outcome.Observed.Add("קו ללא רוחב משורטט");
                break;
        }
        var linetype = ReadLinetype(p);
        if (linetype != null) outcome.Observed.Add($"סוג קו: {Shown(linetype)}");
        bool? closed = null;
        if (evidenceReadable && EvidenceReader.TryObservation(p, EvidenceKeys.Closed, out var closedValue))
            closed = closedValue.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(closedValue.GetString(), out var b) => b,
                _ => null,
            };
        if (closed == true) outcome.Observed.Add("העצם סגור");
        // The draft builder re-reads an open line on a width-classified marking layer by its drawn width; a wide one is
        // measured as painted area, where it admits only width-split families (EngineerBoqDraftBuilder.Accepts). This
        // uses the builder's own readers so it predicts exactly what the draft will do; it only withholds, and nothing
        // it reads is cited beyond the width keys read above.
        double? paintedWidth = null;
        if (ctx.Kind == "length" && ctx.MethodClass == "open" && record.Measurement?.Parameters is { } rawParameters &&
            EngineerBoqLibrary.AnyGlob(layerLeaf, ctx.Library.WidthClassifiedLayerPatterns) &&
            EngineerBoqDraftBuilder.ReadDrawnWidth(rawParameters, out var drawnWidth) == EngineerBoqDraftBuilder.DrawnWidthState.Proven &&
            XrefWidthPolicy.HostWidthScale(evidenceReadable ? record : WithoutEvidence(record)) is { } builderScale &&
            drawnWidth * builderScale > PaintedAreaWidthMetres)
            paintedWidth = drawnWidth * builderScale;

        // (d) Weak cues: reported, never used to choose.
        if (ReadColour(p, evidenceReadable) is { } colour)
        {
            outcome.Observed.Add($"צבע: {colour.Text}{(colour.Yellow ? " (צהוב)" : string.Empty)} — רמז חלש בלבד");
            weakCues.Add("צבע");
        }
        if (SourceRoleCue(ctx.Group.Source) != null) weakCues.Add("תפקיד המקור");

        Decide(outcome, ctx, signals, rejected, unusable, weakCues, readName || readTexts > 0, new Geometry(width, linetype, closed, paintedWidth),
            boundUtility);
        return outcome;
    }

    private static void Decide(Outcome outcome, GroupContext ctx, List<Signal> signals, HashSet<string> rejected,
        List<string> unusable, List<string> weakCues, bool readText, Geometry geometry, HashSet<string>? boundUtility = null)
    {
        var strong = signals.Where(s => s.Tier == Tier.Strong && s.Set.Count > 0).ToList();
        var candidate = signals.Where(s => s.Tier == Tier.Candidate && s.Set.Count > 0).ToList();
        var supporting = signals.Where(s => s.Tier == Tier.Supporting && s.Set.Count > 0).ToList();

        void Abstain(Verdict verdict) => outcome.Verdict = verdict;
        void AddUnusable() { foreach (var note in unusable) outcome.Missing.Add(note); }

        var existing = strong.Where(s => s.Set.Contains(FamilySignalTable.ExistingMarker)).ToList();
        if (existing.Count > 0)
        {
            outcome.Cite(existing.Select(s => s.Key));
            outcome.Missing.Add($"{Labels(existing)} מציינים מצב קיים או פירוק — אינה עבודה חדשה ב{ctx.Library.Texts.LibraryName}; לא הוצעה משפחה.");
            Abstain(Verdict.Existing);
            return;
        }

        // The object itself names an infrastructure domain this measurement cannot be: that is a conflict with any other
        // channel, not silence, so nothing is proposed; the engineer sees which domain and why.
        if (boundUtility is { Count: > 0 })
        {
            outcome.Cite(strong.Select(s => s.Key));
            foreach (var id in ctx.Ordered(boundUtility))
                outcome.Missing.Add($"הראיה על העצם מתאימה ל{ctx.Display(id)}, שנמדדת לפי {BasisLabel(ctx.Rules[id].Basis)} — לא לפי {MeasurementLabel(ctx.Kind, ctx.MethodClass)}.");
            foreach (var signal in strong.Concat(candidate))
                foreach (var id in ctx.Ordered(signal.Set.Where(ctx.IsFamily)))
                    outcome.Alternative(id, $"לפי {Label(signal.Channel)}");
            Abstain(Verdict.BasisMismatch);
            AddUnusable();
            return;
        }

        var basis = strong.Count > 0 ? strong : candidate;
        if (basis.Count == 0)
        {
            if (rejected.Count > 0)
            {
                foreach (var id in ctx.Ordered(rejected))
                    outcome.Missing.Add($"הראיה מתאימה ל{ctx.Display(id)}, שנמדדת לפי {BasisLabel(ctx.Rules[id].Basis)} — לא לפי {MeasurementLabel(ctx.Kind, ctx.MethodClass)}.");
                Abstain(Verdict.BasisMismatch);
            }
            else
            {
                // A supporting hint (hatch pattern name, legend row by colour) is shown, never decisive.
                foreach (var signal in supporting)
                    foreach (var id in ctx.Ordered(signal.Set.Where(ctx.IsFamily)))
                        outcome.Alternative(id, $"לפי {Label(signal.Channel)} — רמז תומך בלבד");
                if (supporting.Count > 0)
                {
                    outcome.Cite(supporting.Select(s => s.Key));
                    outcome.Missing.Add($"רמז תומך בלבד ({Labels(supporting)}) אינו מספיק להצעת משפחה — נדרשת ראיה ישירה (בלוק, תכונה, PropertySet או מקרא לפי תבנית).");
                }
                outcome.Missing.Add(weakCues.Count > 0 && !readText && supporting.Count == 0
                    ? $"אין ראיה קריאה מלבד {string.Join(" ו", weakCues)} — רמזים חלשים אינם מספיקים להצעת משפחה."
                    : "אין ראיה קריאה שמזהה משפחה (בלוק מהספרייה, תכונות, PropertySet, שורת מקרא או טקסט סמוך).");
                if (readText && supporting.Count == 0)
                    outcome.Missing.Add($"השמות והטקסטים שנקראו אינם מתארים נושא מוכר ב{ctx.Library.Texts.LibraryName}.");
                Abstain(Verdict.NoEvidence);
            }
            AddUnusable();
            return;
        }

        // A text or channel naming two subjects (a roads element and an infrastructure domain, or two domains) is not a
        // decision, and no other channel narrows it to one of them: never silently the roads family.
        var mixed = basis.Where(s => s.Set.Contains(FamilySignalTable.MixedSubjectMarker)).ToList();
        if (mixed.Count > 0)
        {
            outcome.Cite(basis.Select(s => s.Key));
            foreach (var signal in basis)
                foreach (var id in ctx.Ordered(signal.Set.Where(ctx.IsFamily)))
                    outcome.Alternative(id, $"לפי {Label(signal.Channel)}");
            outcome.Missing.Add($"{Labels(mixed)} מתארים יותר מנושא אחד ({string.Join(", ", ctx.Ordered(mixed.SelectMany(s => s.Set).Where(ctx.IsFamily)))}) — " +
                                "טקסט עם שני נושאים אינו הכרעה; נדרשת הכרעת מהנדס.");
            Abstain(Verdict.MixedSubjects);
            return;
        }

        var viaStrong = strong.Count > 0;
        var intersection = Intersect(basis.Select(s => s.Set));
        if (intersection.Count == 0)
        {
            outcome.Cite(basis.Select(s => s.Key));
            foreach (var signal in basis)
                foreach (var id in ctx.Ordered(signal.Set.Where(ctx.IsFamily)))
                    outcome.Alternative(id, $"לפי {Label(signal.Channel)}");
            outcome.Missing.Add($"{Labels(basis)} מצביעים על משפחות שונות — נדרשת הכרעת מהנדס.");
            foreach (var signal in basis)
                outcome.Missing.Add($"{Label(signal.Channel)}: {string.Join(", ", ctx.Ordered(signal.Set).Select(Marker))}");
            Abstain(Verdict.Conflict);
            return;
        }

        var families = ctx.Ordered(intersection.Where(ctx.IsFamily));
        var markers = intersection.Where(id => !ctx.IsFamily(id)).ToList();
        outcome.Cite(basis.Select(s => s.Key));
        if (families.Count == 0)
        {
            if (markers.Contains(FamilySignalTable.ExistingMarker))
            {
                outcome.Missing.Add($"{Labels(basis)} מציינים מצב קיים או פירוק — לא הוצעה משפחה.");
                Abstain(Verdict.Existing);
            }
            else
            {
                outcome.Missing.Add($"{Labels(basis)} מתארים נושא שהראיות אינן מאפשרות לשייך למשפחה נתמכת ב{ctx.Library.Texts.LibraryName} — " +
                                    "נדרשת השלמת זיהוי; לא הומצאה משפחה.");
                Abstain(Verdict.OtherSubject);
            }
            return;
        }
        if (markers.Count > 0)
        {
            foreach (var id in families) outcome.Alternative(id, $"לפי {Labels(basis)}");
            outcome.Missing.Add($"{Labels(basis)} מתאימים ל-{string.Join(", ", families)} אך גם ל{(markers.Contains(FamilySignalTable.ExistingMarker) ? "מצב קיים" : "נושא שאינו בספרייה")} — נדרשת הכרעה.");
            Abstain(Verdict.Ambiguous);
            return;
        }

        // One-way geometry: may withhold a family, never admit one.
        var kept = new List<string>();
        var withheldWhy = new List<string>();
        foreach (var id in families)
        {
            var reason = Withheld(id, ctx, geometry);
            if (reason == null) { kept.Add(id); continue; }
            withheldWhy.Add($"{ctx.Display(id)}: {reason.Value.Why}");
            outcome.Cite(reason.Value.Keys);
        }
        if (kept.Count == 0)
        {
            foreach (var id in families) outcome.Alternative(id, $"לפי {Labels(basis)}; נשללה לפי הגאומטריה");
            outcome.Missing.Add("הגאומטריה שוללת את כל המשפחות שהראיה מתאימה להן:");
            outcome.Missing.AddRange(withheldWhy);
            Abstain(Verdict.Withheld);
            return;
        }
        foreach (var why in withheldWhy) outcome.Inferred.Add($"נשללה: {why}");

        var current = kept;
        var narrowedBy = new List<Signal>();
        if (current.Count > 1 && viaStrong)
            current = Narrow(current, candidate, narrowedBy);
        if (current.Count > 1)
            current = Narrow(current, supporting, narrowedBy);

        if (current.Count > 1)
        {
            outcome.Cite(narrowedBy.Select(s => s.Key));
            foreach (var id in current) outcome.Alternative(id, $"מתאים ל{Labels(basis)} — הראיה כללית");
            outcome.Missing.Add($"הראיה ({Labels(basis)}) מתאימה לכמה משפחות: {string.Join(", ", current)} — נדרש פרט מבחין (תכונה, בלוק או שורת מקרא מפורשת).");
            if (candidate.Any(s => s.Partial)) outcome.Missing.Add(TruncatedNearbyNote);
            AddWidthAndDashHints(outcome, current, geometry.Width);
            Abstain(Verdict.Ambiguous);
            return;
        }

        // A candidate proposes alone only over a complete list: a cut nearby-text list may have dropped a text that
        // disagrees (the unanimity it shows is over the nearest texts only).
        if (!viaStrong && basis.Any(s => s.Partial))
        {
            foreach (var id in current) outcome.Alternative(id, $"לפי {Labels(basis)} — רשימת טקסטים קטועה");
            outcome.Missing.Add(TruncatedNearbyNote);
            Abstain(Verdict.Ambiguous);
            return;
        }

        // Proposed.
        var family = current[0];
        outcome.Verdict = Verdict.Proposed;
        outcome.Family = family;
        outcome.Keys.Clear();
        var supporters = signals.Where(s => s.Set.Contains(family) && (s.Tier != Tier.Supporting || narrowedBy.Contains(s))).ToList();
        outcome.Cite(supporters.Select(s => s.Key));
        foreach (var id in families.Where(id => id != family))
        {
            var reason = Withheld(id, ctx, geometry);
            if (reason != null) outcome.Cite(reason.Value.Keys);
        }
        outcome.Inferred.Insert(0, $"התאמה למשפחה {ctx.Display(family)} לפי {Labels(supporters)}");
        if (!viaStrong)
            outcome.Inferred.Add("הראיה היחידה היא מועמדת (טקסט סמוך) — לא אומתה");
        if (supporters.Any(s => s.Channel is Channel.Legend or Channel.LegendByColor))
            outcome.Inferred.Add("שורת המקרא לא אומתה בשרטוט — לאשר");
        if (families.Count > 1)
        {
            var others = families.Where(id => id != family).ToList();
            foreach (var id in others.Where(kept.Contains))
                outcome.Alternative(id, $"מתאים גם ל{Labels(basis)}; הצמצום לפי {Labels(narrowedBy)}");
            if (narrowedBy.Count > 0)
                outcome.Inferred.Add($"{Labels(basis)} התאימו גם ל-{string.Join(", ", others)}; הצמצום לפי {Labels(narrowedBy)}");
        }
        // A candidate channel that points elsewhere does not veto a strong decision, but it is shown.
        foreach (var signal in candidate.Where(s => viaStrong && !s.Set.Contains(family)))
            foreach (var id in ctx.Ordered(signal.Set.Where(ctx.IsFamily)))
                outcome.Alternative(id, $"לפי {Label(signal.Channel)} — מועמד בלבד");
        var rule = ctx.Rules[family];
        if (rule.Emits.Count == 0)
            outcome.Missing.Add($"למשפחה {ctx.Display(family)} אין פריט במתכון — נדרשת הגדרה הנדסית.");
        if (rule.SplitByDrawnWidth && geometry.Width.Kind == WidthKind.Unproven)
            outcome.Missing.Add($"רוחב משורטט מקומי {Metres(geometry.Width.Local)} ללא הוכחה במארח — חלוקת הרוחב לתמחור נשארת פתוחה.");
    }

    private static List<string> Narrow(List<string> current, List<Signal> narrowing, List<Signal> narrowedBy)
    {
        foreach (var signal in narrowing)
        {
            // A cut list, or a text naming two subjects, never narrows.
            if (signal.Partial || signal.Set.Contains(FamilySignalTable.MixedSubjectMarker)) continue;
            var next = current.Where(signal.Set.Contains).ToList();
            if (next.Count == 0 || next.Count == current.Count) continue;
            narrowedBy.Add(signal);
            current = next;
            if (current.Count == 1) break;
        }
        return current;
    }

    private static void AddWidthAndDashHints(Outcome outcome, List<string> current, WidthReading width)
    {
        var widthless = FamilySignalTable.WidthlessFamilies.Where(current.Contains).ToList();
        if (widthless.Count > 0 && current.Count > widthless.Count)
            outcome.Missing.Add(width.Kind switch
            {
                WidthKind.Unproven => $"רוחב משורטט מקומי {Metres(width.Local)} ללא הוכחה במארח ({width.Why}) — לא ניתן להבחין בין קו ברוחב לקו ללא רוחב.",
                WidthKind.Widthless => "הקו משורטט ללא רוחב, וגם רכיב ברוחב אפשרי עם פרמטר רוחב — נדרשת הכרעה.",
                _ => "אין רוחב משורטט קריא — לא ניתן להבחין בין קו ברוחב לקו ללא רוחב.",
            });
        if (current.Count(FamilySignalTable.DashRatios.ContainsKey) > 1)
            outcome.Missing.Add("יחס הקווקוו לא ידוע — סוג קו או מקרא עם יחס (3-3 / 3-1.5 / 1-1) יכריעו.");
    }

    /// <summary>Why geometry withholds a family (one-way), with the keys that prove it; null when it does not.</summary>
    private static (string Why, IReadOnlyList<string> Keys)? Withheld(string family, GroupContext ctx, Geometry geometry)
    {
        var width = geometry.Width;
        if (width.Kind == WidthKind.HostProven && width.Host > FamilySignalTable.WidthlessMetres &&
            FamilySignalTable.WidthlessFamilies.Contains(family))
            return ($"רוחב משורטט מוכח במארח {Metres(width.Host)} מ' — אינו קו ללא רוחב", width.Keys);
        if (geometry.PaintedWidth is { } painted && !ctx.Rules[family].SplitByDrawnWidth)
            return ($"בשכבת סימון שנמדדת לפי רוחב, קו ברוחב {Metres(painted)} מ' נמדד בטיוטה כשטח צבוע — " +
                    "המשפחה אינה מחולקת לפי רוחב ולא תתומחר ממנו", width.Keys);
        if (geometry.Linetype is { } linetype && FamilySignalTable.DashRatios.TryGetValue(family, out var ratio))
        {
            if (linetype.Equals("Continuous", StringComparison.OrdinalIgnoreCase))
                return ($"סוג הקו {linetype} רציף — אינו קו מקווקו", new[] { EvidenceKeys.EntityLinetype, EvidenceKeys.LayerLinetype });
            if (LinetypeDashRatio(linetype) is { } stated &&
                (Math.Abs(stated.Paint - ratio.Paint) > 1e-6 || Math.Abs(stated.Gap - ratio.Gap) > 1e-6))
                return ($"סוג הקו {linetype} ביחס {stated.Text} — אינו {ratio.Paint.ToString(CultureInfo.InvariantCulture)}-{ratio.Gap.ToString(CultureInfo.InvariantCulture)}",
                    new[] { EvidenceKeys.EntityLinetype, EvidenceKeys.LayerLinetype });
        }
        if (geometry.Closed == true && ctx.Rules[family].Basis == DraftQuantityBasis.OpenLength)
            return ("העצם סגור, והמשפחה נמדדת בקווים פתוחים בלבד", new[] { EvidenceKeys.Closed });
        return null;
    }

    /// <summary>
    /// The paint-gap ratio a linetype name states: its only '-'-joined pair of numbers. A name with no pair, with
    /// several pairs or with a longer chain states no ratio (it then withholds nothing).
    /// </summary>
    private static (double Paint, double Gap, string Text)? LinetypeDashRatio(string linetype)
    {
        var chains = LinetypeNumberChain.Matches(linetype);
        if (chains.Count != 1) return null;
        var parts = chains[0].Value.Split('-');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var paint) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var gap))
            return null;
        return (paint, gap, chains[0].Value);
    }

    // ------------------------------------------------------------------ evidence readers

    private static bool Usable(IReadOnlyDictionary<string, string> p, string key, Outcome outcome, List<string> unusable)
    {
        var status = EvidenceReader.Status(p, key);
        if (status.State == EvidenceState.Truncated)
            outcome.Observed.Add($"ראיה קטועה: {key} (נקרא חלק מתוך {status.TruncatedTotal?.ToString(CultureInfo.InvariantCulture)})");
        if (status.Usable) return true;
        if (status.State is EvidenceState.Unavailable or EvidenceState.Invalid) unusable.Add(UnusableNote(key, status));
        return false;
    }

    private static string UnusableNote(string key, EvidenceStatus status) => status.State == EvidenceState.Invalid
        ? $"הראיה {key} ללא סטטוס תקף — לא נקראה."
        : $"הראיה {key} לא זמינה ({Shown(EvidenceReader.Clean(status.Label) ?? "unavailable", 60)}).";

    /// <summary>
    /// Text leaf → possible families (plus markers). Phrases naming only families the measurement cannot be are
    /// dropped (and remembered for the explanation); the remaining phrase sets are intersected, or united when they
    /// disagree (a text naming two subjects is ambiguous, not a decision). An out-of-library subject the table names
    /// (a wall, a tree, a pipe without a domain) is a phrase of <see cref="FamilySignalTable.OtherSubjectMarker"/>, so
    /// it counts whatever other phrase the text also has.
    /// </summary>
    private static HashSet<string> LeafSet(string text, GroupContext ctx, HashSet<string> rejected, HashSet<string>? boundUtility = null)
    {
        var reading = ctx.Read(text);
        if (reading.IsExisting) return new HashSet<string>(StringComparer.Ordinal) { FamilySignalTable.ExistingMarker };
        var sets = new List<HashSet<string>>();
        IEnumerable<IReadOnlyList<string>> phraseFamilies = reading.Hits.Select(h => h.Phrase.Families);
        if (reading.Hits.Count == 0 && reading.CatalogCode != null && ctx.EmitFamilies.TryGetValue(reading.CatalogCode, out var byCode))
            phraseFamilies = new[] { (IReadOnlyList<string>)byCode };
        foreach (var families in phraseFamilies)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in families)
            {
                if (id.StartsWith('#')) set.Add(id);
                else if (ctx.Possible.Contains(id)) set.Add(id);
                else if (ctx.IsFamily(id)) rejected.Add(id);
            }
            if (set.Count > 0) sets.Add(set);
            // Text on the object itself naming an infrastructure subject this measurement cannot be (a drainage word on a
            // hatch area) is kept as a conflict, never silence: dropping it would let another channel decide alone.
            else if (boundUtility != null && families.Count > 0 && families.All(id => !id.StartsWith('#') && EngineerBoqLibrary.IsUtilityFamily(id)))
                foreach (var id in families) boundUtility.Add(id);
        }
        // Known engineering vocabulary outside the table, with no phrase at all (a cabinet, a bollard...): another subject.
        if (reading.Hits.Count == 0 && reading.CatalogCode == null && MappingProposalEngine.HasKnownSubjectVocabulary(text))
            sets.Add(new HashSet<string>(StringComparer.Ordinal) { FamilySignalTable.OtherSubjectMarker });
        if (sets.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var intersection = Intersect(sets);
        return intersection.Count > 0 ? intersection : Union(sets);
    }

    private static HashSet<string> Combine(List<HashSet<string>> leaves, bool existingDominates)
    {
        var sets = leaves.Where(l => l.Count > 0).ToList();
        if (sets.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        if (existingDominates && sets.Any(s => s.Contains(FamilySignalTable.ExistingMarker)))
            return new HashSet<string>(StringComparer.Ordinal) { FamilySignalTable.ExistingMarker };
        // A leaf naming two subjects stays two subjects: another leaf of the channel does not narrow it.
        if (sets.Any(s => s.Contains(FamilySignalTable.MixedSubjectMarker))) return Union(sets);
        var intersection = Intersect(sets);
        return intersection.Count > 0 ? intersection : Union(sets);
    }

    /// <summary>
    /// Sets that disagree are united: ambiguous, never a decision. When the united families belong to more than one
    /// subject domain (the roads library and an infrastructure domain, or two infrastructure domains), the evidence
    /// names two subjects and <see cref="FamilySignalTable.MixedSubjectMarker"/> is added.
    /// </summary>
    private static HashSet<string> Union(IReadOnlyCollection<HashSet<string>> sets)
    {
        var union = new HashSet<string>(sets.SelectMany(s => s), StringComparer.Ordinal);
        if (union.Where(id => !id.StartsWith('#')).Select(DomainOf).Distinct(StringComparer.Ordinal).Skip(1).Any())
            union.Add(FamilySignalTable.MixedSubjectMarker);
        return union;
    }

    /// <summary>The subject domain of a family: "roads" for the roads library, else the infrastructure domain (lines and structures alike).</summary>
    private static string DomainOf(string family) => !EngineerBoqLibrary.IsUtilityFamily(family)
        ? "roads"
        : family.EndsWith(FamilySignalTable.StructuresSuffix, StringComparison.Ordinal)
            ? family[..^FamilySignalTable.StructuresSuffix.Length]
            : family;

    private static HashSet<string> Intersect(IEnumerable<HashSet<string>> sets)
    {
        HashSet<string>? result = null;
        foreach (var set in sets)
        {
            if (result == null) result = new HashSet<string>(set, StringComparer.Ordinal);
            else result.IntersectWith(set);
        }
        return result ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The items of an observation: an array, an object wrapping one under a list name, or a single object.</summary>
    private static List<JsonElement> ObservationItems(JsonElement observation, params string[] listNames)
    {
        if (observation.ValueKind == JsonValueKind.Array) return observation.EnumerateArray().ToList();
        if (observation.ValueKind != JsonValueKind.Object) return new List<JsonElement>();
        foreach (var name in listNames)
            if (observation.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner.EnumerateArray().ToList();
        return new List<JsonElement> { observation };
    }

    private static IEnumerable<(string Text, double? Distance)> NearbyItems(JsonElement observation)
    {
        foreach (var item in ObservationItems(observation, "texts", "items").Take(16))
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String ||
                EvidenceReader.Clean(t.GetString()) is not { } text)
                continue;
            double? distance = item.TryGetProperty("distance_m", out var d) && d.ValueKind == JsonValueKind.Number &&
                               d.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 ? value : null;
            yield return (text, distance);
        }
    }

    /// <summary>
    /// Drawn width, mirroring EngineerBoqDraftBuilder.DrawnWidth (metres databases only, constant or equal min/max),
    /// but reading only keys whose status is usable, and telling a proven widthless line from an unknown one.
    /// A width is a host width only through <see cref="XrefWidthPolicy.HostWidthScale"/>; when the evidence contract is
    /// not readable, ev_xref_transform is not read either, so only a host entity (no transform needed) is proven.
    /// </summary>
    private static WidthReading ReadWidth(NeutralQuantityRecord record, IReadOnlyDictionary<string, string> p, bool evidenceReadable)
    {
        double? Number(string key) =>
            EvidenceReader.Status(p, key).Usable && p.TryGetValue(key, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
        var keys = new List<string>();
        var width = Number(ConstantWidthKey);
        if (width != null) keys.Add(ConstantWidthKey);
        else if (Number(MinWidthKey) is { } min && Number(MaxWidthKey) is { } max && Math.Abs(max - min) <= 1e-6)
        {
            width = min;
            keys.Add(MinWidthKey);
            keys.Add(MaxWidthKey);
        }
        if (width is not { } local || local < 0) return new WidthReading(WidthKind.Unknown, 0, 0, null, Array.Empty<string>());
        if (local <= FamilySignalTable.WidthlessMetres) return new WidthReading(WidthKind.Widthless, 0, 0, null, keys);
        if (local > 20) return new WidthReading(WidthKind.Unknown, 0, 0, null, Array.Empty<string>());
        // b24: a host record carries the scan's physical factor (a unit decision included); a record without it (an
        // XREF, or a scan written before b24) keeps the raw-unit rule.
        var metres = p.ContainsKey(QuantityPhysicalUnits.AuthorityKey)
            ? QuantityPhysicalUnits.MetresPerUnit(p, declaredMetres: false) == 1.0
            : EvidenceReader.Status(p, UnitsKey).Usable && p.TryGetValue(UnitsKey, out var units) &&
              string.Equals(units?.Trim(), "Meters", StringComparison.OrdinalIgnoreCase);
        if (!metres) return new WidthReading(WidthKind.Unproven, local, 0, "יחידות השרטוט אינן מטרים או לא נקראו", keys);
        keys.Add(UnitsKey);
        var scale = evidenceReadable ? XrefWidthPolicy.HostWidthScale(record) : XrefWidthPolicy.HostWidthScale(WithoutEvidence(record));
        if (scale is not { } s)
            return new WidthReading(WidthKind.Unproven, local, 0,
                evidenceReadable ? "אין הוכחת התמרת XREF למארח" : "חוזה הראיות אינו קריא — התמרת ה-XREF למארח לא נקראה", keys);
        var xref = (record.Source?.Xref ?? string.Empty).Trim();
        if (evidenceReadable && xref.Length > 0 && !xref.Equals("(host)", StringComparison.OrdinalIgnoreCase) &&
            EvidenceReader.Status(p, EvidenceKeys.XrefTransform).Usable)
            keys.Add(EvidenceKeys.XrefTransform);
        return new WidthReading(WidthKind.HostProven, local, local * s, null, keys);
    }

    /// <summary>A copy of the record without its ev_* parameters, for readers that must not see an unknown contract.</summary>
    private static NeutralQuantityRecord WithoutEvidence(NeutralQuantityRecord record)
    {
        var measurement = record.Measurement;
        var parameters = (measurement?.Parameters ?? new Dictionary<string, string>())
            .Where(pair => !pair.Key.StartsWith("ev_", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new NeutralQuantityRecord
        {
            RecordId = record.RecordId,
            ProjectProfileId = record.ProjectProfileId,
            RunId = record.RunId,
            Source = record.Source,
            Measurement = new QuantityMeasurement
            {
                Kind = measurement?.Kind ?? string.Empty,
                Method = measurement?.Method ?? string.Empty,
                RawValue = measurement?.RawValue ?? 0,
                Unit = measurement?.Unit ?? string.Empty,
                Parameters = parameters,
            },
        };
    }

    /// <summary>The effective linetype (EngineerBoqDraftBuilder.EffectiveLinetype) from usable keys only.</summary>
    private static string? ReadLinetype(IReadOnlyDictionary<string, string> p)
    {
        var usable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { EvidenceKeys.EntityLinetype, EvidenceKeys.LayerLinetype })
            if (EvidenceReader.Status(p, key).Usable && p.TryGetValue(key, out var value) && value != null)
                usable[key] = value;
        var linetype = EngineerBoqDraftBuilder.EffectiveLinetype(usable);
        return linetype == null ? null : EvidenceReader.Clean(linetype);
    }

    private static (string Text, bool Yellow)? ReadColour(IReadOnlyDictionary<string, string> p, bool evidenceReadable)
    {
        if (evidenceReadable && EvidenceReader.TryObservation(p, EvidenceKeys.ColorEffective, out var colour))
        {
            var text = colour.ValueKind switch
            {
                JsonValueKind.String => colour.GetString(),
                JsonValueKind.Array => string.Join(",", colour.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?")),
                _ => null,
            };
            var parts = (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 255))
            {
                var rgb = parts.Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                return ($"{rgb[0]},{rgb[1]},{rgb[2]}", rgb[0] >= 200 && rgb[1] >= 170 && rgb[2] <= 120);
            }
        }
        if (EvidenceReader.Status(p, EvidenceKeys.EntityColorIndex).Usable && p.TryGetValue(EvidenceKeys.EntityColorIndex, out var raw) &&
            int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var aci) && aci is >= 1 and <= 255)
            return ($"ACI {aci.ToString(CultureInfo.InvariantCulture)}", aci is 2 or 40 or 50);
        return null;
    }

    private static string? SourceRoleCue(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (EngineerBoqLibrary.Glob(source, "*-HA-*")) return "מודל הצללות (HA)";
        if (EngineerBoqLibrary.Glob(source, "*-GM-*")) return "מודל גאומטרי (GM)";
        if (EngineerBoqLibrary.Glob(source, "*-SM-*")) return "מודל שילוט וסימון (SM)";
        return null;
    }

    private static List<string> SourceRoleNotes(RecognitionGroupInput group)
    {
        var cue = SourceRoleCue(group.Source);
        return cue == null ? new List<string>() : new List<string> { $"מקור {Shown(group.Source)} — לפי מוסכמת 6422: {cue} (רמז חלש בלבד)" };
    }

    // ------------------------------------------------------------------ labels

    private static string Label(Channel channel) => channel switch
    {
        Channel.Block => "הבלוק",
        Channel.BlockPattern => "תבנית בלוק בספרייה שאינה מספיקה בלי השכבה שלה",
        Channel.Attributes => "תכונות הבלוק",
        Channel.BlockProps => "מאפייני הבלוק הדינמי",
        Channel.PropertySet => "ה-PropertySet",
        Channel.Legend => "שורת המקרא",
        Channel.LegendByColor => "שורת מקרא לפי צבע",
        Channel.NearbyText => "הטקסט הסמוך",
        Channel.HatchPattern => "תבנית ההצללה",
        _ => channel.ToString(),
    };

    private static string Labels(IEnumerable<Signal> signals)
    {
        var labels = signals.Select(s => Label(s.Channel)).Distinct(StringComparer.Ordinal).ToList();
        return labels.Count switch
        {
            0 => "—",
            1 => labels[0],
            _ => string.Join(", ", labels.Take(labels.Count - 1)) + " ו" + labels[^1],
        };
    }

    private static string Marker(string id) => id switch
    {
        FamilySignalTable.ExistingMarker => "מצב קיים",
        FamilySignalTable.OtherSubjectMarker => "נושא מחוץ לספרייה",
        FamilySignalTable.MixedSubjectMarker => "שני נושאים",
        _ => id,
    };

    private static string MatchLabel(string? match) => match switch
    {
        "pattern" => "תבנית",
        "block" => "בלוק",
        "linetype" => "סוג קו",
        "color" => "צבע",
        "legend-guess" => "מקרא שזוהה לפי צורה בלבד",
        _ => "בסיס לא ידוע",
    };

    private static string ObservedText(Channel channel, EvidenceText text)
    {
        var value = Shown(text.Text);
        var tag = text.Tag == null ? null : Shown(text.Tag, 40);
        return channel switch
        {
            Channel.Attributes => tag == null ? $"תכונת בלוק: {value}" : $"תכונה {tag}: {value}",
            Channel.BlockProps => tag == null ? $"מאפיין בלוק דינמי: {value}" : $"מאפיין בלוק דינמי {tag}: {value}",
            Channel.PropertySet => text.Field switch
            {
                "component" => $"PropertySet רכיב: {value}",
                "subassembly" => $"PropertySet תת-הרכבה: {value}",
                "catalog_code" => $"PropertySet קוד פריט (ראיה בלבד): {value}",
                // The reader's "<set>:<property>" name/value pairs: the property is what the value says.
                "value" when tag != null => $"PropertySet {tag}: {value}",
                _ => $"PropertySet {Shown(text.Field, 40)}: {value}",
            },
            _ => value,
        };
    }

    private static string Shown(string? text, int max = MaxShownChars)
    {
        var clean = EvidenceReader.Clean(text) ?? string.Empty;
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }

    private static string Metres(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ partition and assemble

    private static IReadOnlyList<RecognitionProposal> Assemble(RecognitionGroupInput group, IReadOnlyList<NeutralQuantityRecord> records,
        List<Outcome> outcomes, GroupContext ctx, List<string> groupNotes, CatalogSnapshot? catalog)
    {
        // A group-level refusal for duplicate ids covers every distinct id once.
        if (outcomes.Count == 1 && outcomes[0].Refusal == Refusal.DuplicateIds)
        {
            var ids = records.Select(r => r.RecordId).Distinct(StringComparer.Ordinal).ToList();
            return new[]
            {
                new RecognitionProposal(group.GroupId, ids, RecognitionStatus.Abstained, null, Array.Empty<string>(),
                    Array.Empty<RecognitionEvidenceRef>(), groupNotes, Array.Empty<string>(), Array.Empty<RecognitionAlternative>(),
                    outcomes[0].Missing, RecognitionProposal.OriginLocal) { AbstainKind = AbstainKind(Verdict.Refused) },
            };
        }

        var partitions = new List<(string Key, List<Outcome> Members)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            var key = outcome.Verdict == Verdict.Proposed
                ? "P|" + outcome.Family
                : $"A|{outcome.Verdict}|{outcome.Refusal}|{string.Join(",", ctx.Ordered(outcome.Alternatives))}";
            if (!index.TryGetValue(key, out var at))
            {
                index[key] = at = partitions.Count;
                partitions.Add((key, new List<Outcome>()));
            }
            partitions[at].Members.Add(outcome);
        }

        var result = new List<RecognitionProposal>();
        foreach (var (_, members) in partitions)
        {
            var head = members[0];
            var proposed = head.Verdict == Verdict.Proposed;
            var recordIds = members.Select(m => m.Record.RecordId).ToList();
            var refs = members.SelectMany(m => m.Keys.Select(k => (Key: k, m.Record)))
                .Where(x => EvidenceReader.Status(x.Record.Measurement?.Parameters ?? new Dictionary<string, string>(), x.Key).Usable)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new RecognitionEvidenceRef(g.Key, g.Select(x => x.Record.RecordId).Distinct(StringComparer.Ordinal).ToList()))
                .ToList();
            if (proposed && refs.Count == 0)
            {
                // Never a proposal without cited evidence.
                foreach (var member in members)
                {
                    member.Verdict = Verdict.NoEvidence;
                    member.Missing.Add("אין ראיה קריאה שאפשר לצטט — לא הוצעה משפחה.");
                }
                proposed = false;
            }
            var alternatives = new List<RecognitionAlternative>();
            foreach (var id in ctx.Ordered(members.SelectMany(m => m.Alternatives)))
            {
                if (proposed && id == head.Family) continue;
                var why = members.Select(m => m.Why.TryGetValue(id, out var w) ? w : null).First(w => w != null)!;
                alternatives.Add(new RecognitionAlternative(id, why));
            }
            var observed = Cap(groupNotes.Concat(members.SelectMany(m => m.Observed)), MaxObserved, "תצפיות");
            var inferred = Cap(members.SelectMany(m => m.Inferred), MaxInferred, "הסקות");
            var missing = Cap(members.SelectMany(m => m.Missing), MaxMissing, "פרטים");
            if (proposed)
            {
                var rule = ctx.Rules[head.Family!];
                // The active list's own codes: a library code as listed or through an approved edition link.
                var codes = rule.Emits.Select(e => e.Code.Trim()).Where(c => c.Length > 0)
                    .Select(c => catalog == null ? c : catalog.ResolveLibraryCode(c)).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                inferred = inferred.Append("הצעה בלבד: המשפחה, פריטי המתכון והפרמטרים דורשים אישור מהנדס").ToList();
                result.Add(new RecognitionProposal(group.GroupId, recordIds, RecognitionStatus.Proposed, head.Family, codes, refs,
                    observed, inferred, alternatives, missing, RecognitionProposal.OriginLocal));
            }
            else
            {
                if (missing.Count == 0) missing = new List<string> { "אין ראיה קריאה שמזהה משפחה." };
                result.Add(new RecognitionProposal(group.GroupId, recordIds, RecognitionStatus.Abstained, null, Array.Empty<string>(), refs,
                    observed, inferred, alternatives, missing, RecognitionProposal.OriginLocal) { AbstainKind = AbstainKind(head.Verdict) });
            }
        }
        return result;
    }

    private static List<string> Cap(IEnumerable<string> lines, int max, string noun)
    {
        var distinct = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count <= max) return distinct;
        var kept = distinct.Take(max - 1).ToList();
        kept.Add($"ועוד {(distinct.Count - kept.Count).ToString(CultureInfo.InvariantCulture)} {noun} שלא הוצגו");
        return kept;
    }
}
