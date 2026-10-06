using System.Globalization;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using FamilyDecision = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyDecision;
using FamilyEvidenceMatch = MahodAI.CivilDelivery.Shared.ProjectProfile.EstimateProfile.FamilyEvidenceMatch;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>Synthetic records, groups and catalogs for family-decision tests. Nothing here is project data.</summary>
internal static class FamilyFixtures
{
    public const string Ha = "6422-HA-MODEL-NATAZ";
    public const string Gm = "6422-GM-MODEL-NATAZ";
    public const string Sm = "6422-SM-MODEL-NATAZ";
    public const string Survey = "6422-SP-MEDVA-ALL-2026-MHD";
    public const string Approver = "Natali (synthetic)";
    public static readonly DateTime T1 = new(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime T2 = new(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
    public static readonly string[] Hatch = { EvidenceKeys.Hatch };
    private static int _handle;

    public static EngineerBoqLibrary Library => EngineerBoqLibrary.RoadsV1;

    public static NeutralQuantityRecord Rec(string? xref, string layer, string kind, string method, double value, string unit,
        string? hatch = null, string hatchStatus = "read", string? pset = null, string? block = null, string? drawingHash = null)
    {
        var parameters = new Dictionary<string, string> { [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1 };
        if (hatch != null)
        {
            parameters[EvidenceKeys.Hatch] =
                $"{{\"pattern\":\"{hatch}\",\"scale\":1,\"angle\":0,\"solid\":false,\"associative\":true,\"space\":\"source\"}}";
            parameters[EvidenceKeys.Hatch + EvidenceKeys.StatusSuffix] = hatchStatus;
        }
        else parameters[EvidenceKeys.Hatch + EvidenceKeys.StatusSuffix] = "absent";
        if (pset != null)
        {
            parameters[EvidenceKeys.PsetComponent] =
                $"{{\"component\":\"{pset}\",\"subassembly\":\"SYNTHETIC-SA\",\"catalog_code\":\"\",\"placeholder\":false}}";
            parameters[EvidenceKeys.PsetComponent + EvidenceKeys.StatusSuffix] = "read";
        }
        else parameters[EvidenceKeys.PsetComponent + EvidenceKeys.StatusSuffix] = "absent";
        if (block != null) parameters[EvidenceKeys.BlockNameEffective] = block;
        var handle = Interlocked.Increment(ref _handle).ToString("X", CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = $"q-fam-{handle}",
            ProjectProfileId = "SYNTHETIC",
            RunId = "SYNTHETIC-RUN",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg",
                DrawingHash = drawingHash ?? new string('a', 64),
                Handle = handle,
                EntityType = "X",
                Layer = xref == null ? layer : $"{xref}|{layer}",
                Xref = xref,
            },
            Measurement = new QuantityMeasurement { Kind = kind, Method = method, RawValue = value, Unit = unit, Parameters = parameters },
        };
    }

    public static NeutralQuantityRecord HatchArea(string layer, double value, string hatch = "ANSI31", string? pset = null,
        string? xref = Ha, string unit = "מ\"ר", string? drawingHash = null, string hatchStatus = "read") =>
        Rec(xref, layer, "area", "hatch-area", value, unit, hatch: hatch, hatchStatus: hatchStatus, pset: pset, drawingHash: drawingHash);

    public static NeutralQuantityRecord Line(string layer, double value, string? xref = Gm) =>
        Rec(xref, layer, "length", "polyline-length", value, "מ'");

    /// <summary>A group built from its records' own facts (pre-width method class, declared role from the library).</summary>
    public static RecognitionGroupInput Group(string groupId, params NeutralQuantityRecord[] records)
    {
        var first = records[0];
        var source = FamilyDecisionPolicy.NormalizeSource(first.Source.Xref);
        var kind = first.Measurement.Kind;
        first.Measurement.Parameters.TryGetValue(EvidenceKeys.BlockNameEffective, out var block);
        return new RecognitionGroupInput(groupId, source, EngineerBoqDraftBuilder.SourceRole(source, Library),
            SectionProjectionLogic.LayerLeaf(first.Source.Layer), kind, first.Measurement.Unit,
            FamilyDecisionPolicy.PreWidthMethodClass(kind, first.Measurement.Method), kind == "count" ? block : null, records);
    }

    public static FamilyDecision Approve(string family, DateTime at, IReadOnlyList<string> keys, params RecognitionGroupInput[] groups) =>
        FamilyDecisionPolicy.CreateApproval(family, groups, keys, Library, Approver, "זיהוי לפי הצללה ומקרא — בדיקה סינתטית", at);

    public static CatalogSnapshot Catalog(string hash, string description = "Synthetic paving item") => new()
    {
        SnapshotId = "FIXTURE-ONLY",
        FileHash = hash,
        Items =
        {
            ["TEST.1"] = new CatalogItem { Code = "TEST.1", Description = description, UnitRaw = "מ\"ר" },
            ["TEST.2"] = new CatalogItem { Code = "TEST.2", Description = "Synthetic second item", UnitRaw = "מ\"ר" },
        },
    };

    public static EngineerBoqLibrary WithRules(IReadOnlyList<DraftRule> rules, string? id = null,
        IReadOnlyList<string>? surveyPatterns = null) => new()
    {
        Id = id ?? Library.Id,
        Title = Library.Title,
        Basis = Library.Basis,
        Parameters = Library.Parameters,
        Rules = rules,
        ScopeItems = Library.ScopeItems,
        SurveySourcePatterns = surveyPatterns ?? Library.SurveySourcePatterns,
        UtilitySourcePatterns = Library.UtilitySourcePatterns,
        DraftingAidLayerPatterns = Library.DraftingAidLayerPatterns,
        ExistingLayerPatterns = Library.ExistingLayerPatterns,
        WidthClassifiedLayerPatterns = Library.WidthClassifiedLayerPatterns,
        WidthParameterKeys = Library.WidthParameterKeys,
        Texts = Library.Texts,
    };
}

/// <summary>
/// Family decisions: one engineer approval covers several randomly named groups, survives re-measurement, and goes
/// stale (kept, not applied, not deleted) only where its scope, rule, role or cited evidence changed.
/// All records are SYNTHETIC.
/// </summary>
public sealed class FamilyDecisionPolicyTests
{
    private static EngineerBoqLibrary Library => FamilyFixtures.Library;

    private static FamilyResolution One(IReadOnlyList<FamilyDecision> decisions, RecognitionGroupInput group, EngineerBoqLibrary? library = null) =>
        FamilyDecisionPolicy.Resolve(decisions, new[] { group }, library ?? Library).Single();

    private static IEnumerable<string> Codes(params FamilyDecision[] decisions)
    {
        var estimate = new ProjectProfile.EstimateProfile();
        estimate.FamilyDecisions.AddRange(decisions);
        return FamilyDecisionPolicy.Validate(estimate).Select(f => f.Code);
    }

    [Fact]
    public void OneApprovalCoversSeveralRandomLayerGroupsAndApprovesNoItem()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100), FamilyFixtures.HatchArea("asdasd23423", 40));
        var b = FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250));

        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a, b);

        decision.Status.Should().Be(FamilyDecisionPolicy.Active);
        decision.Decision.Should().Be(FamilyDecisionPolicy.ApproveFamily);
        decision.LibraryId.Should().Be(Library.Id);
        decision.FamilyId.Should().Be("road-pavement");
        decision.RuleVersion.Should().Be(LibraryIdentity.RuleFingerprint(Library.Rules.Single(r => r.Id == "road-pavement")));
        decision.Selectors.Should().HaveCount(2).And.OnlyContain(s =>
            s.Source == FamilyFixtures.Ha && s.MeasurementKind == "area" && s.MethodClass == "hatch" && s.Unit == "m2" &&
            s.EvidenceMatch.Count == 0 && CatalogIdentity.IsValidSha256(s.EvidenceFingerprint));
        decision.Selectors.Select(s => s.LayerLeaf).Should().BeEquivalentTo("asdasd23423", "qwe987");
        decision.EvidenceKeys.Should().Equal(EvidenceKeys.Hatch);
        decision.ItemApprovals.Should().BeEmpty("a family approval never approves catalog items");
        decision.ParameterOverrides.Should().BeEmpty();
        decision.ApprovedAtUtc.Should().Be(FamilyFixtures.T1);
        decision.DecisionId.Should().Be(FamilyDecisionPolicy.DecisionId(decision));
        CatalogIdentity.IsValidSha256(decision.DecisionId).Should().BeTrue();
        Codes(decision).Should().BeEmpty();

        var resolved = FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { a, b }, Library);

        resolved.Select(r => r.GroupId).Should().Equal("A", "B");
        resolved.Should().OnlyContain(r => r.State == FamilyDecisionState.Applied && r.IsApplied && !r.IsExclusion &&
                                           r.FamilyId == "road-pavement" && r.DecisionId == decision.DecisionId &&
                                           r.StaleReason == null && r.SelectorIndex >= 0 &&
                                           r.ApprovedBy == FamilyFixtures.Approver && r.ApprovedAtUtc == FamilyFixtures.T1);
    }

    [Fact]
    public void CodexA_NewDrawingHashHandlesAndQuantityKeepTheDecision_RenamingOneLayerUncoversOnlyThatGroup()
    {
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch,
            FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100)),
            FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250)));
        var newHash = new string('b', 64);

        // Rescan: new records (new handles), a new drawing hash, and A measured at 120 instead of 100.
        var a2 = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 120, drawingHash: newHash));
        var b2 = FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250, drawingHash: newHash));
        FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { a2, b2 }, Library)
            .Should().OnlyContain(r => r.State == FamilyDecisionState.Applied && r.FamilyId == "road-pavement");

        // A's layer renamed under a literal selector: A is no longer covered, B keeps its decision.
        var renamed = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("renamed-zz-1", 120, drawingHash: newHash));
        var result = FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { renamed, b2 }, Library);
        result[0].State.Should().Be(FamilyDecisionState.NotCovered);
        result[0].FamilyId.Should().BeNull();
        result[1].State.Should().Be(FamilyDecisionState.Applied);
    }

    [Fact]
    public void CodexB_PropertySetMeaningChangeStalesOnlyTheAffectedSelector()
    {
        var keys = new[] { EvidenceKeys.Hatch, EvidenceKeys.PsetComponent };
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, pset: "ASF-5-19-70"));
        var b = FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250, pset: "ASF-5-19-70"));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, keys, a, b);
        var changedA = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, pset: "S5"));

        var result = FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { changedA, b }, Library);

        result[0].State.Should().Be(FamilyDecisionState.Stale);
        result[0].StaleReason.Should().Be(FamilyDecisionPolicy.StaleEvidence);
        result[0].FamilyId.Should().Be("road-pavement", "a stale outcome names the family to re-approve");
        result[0].DecisionId.Should().Be(decision.DecisionId);
        result[1].State.Should().Be(FamilyDecisionState.Applied);
        decision.Status.Should().Be(FamilyDecisionPolicy.Active, "resolution never edits or deletes a decision");
    }

    [Fact]
    public void CodexB_UnitChangeStalesOnlyTheAffectedSelector()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var b = FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a, b);
        var dunams = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 0.1, unit: "דונם"));
        var unknown = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, unit: "??"));

        var result = FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { dunams, b, unknown }, Library);

        result[0].State.Should().Be(FamilyDecisionState.Stale);
        result[0].StaleReason.Should().Be(FamilyDecisionPolicy.StaleScope);
        result[1].State.Should().Be(FamilyDecisionState.Applied);
        result[2].StaleReason.Should().Be(FamilyDecisionPolicy.StaleScope, "an unknown unit is never guessed");
    }

    [Fact]
    public void CodexB_ANewSourceIsNeverIncludedByAnOldApproval_ReapprovalKeepsHistory()
    {
        var gm = FamilyFixtures.Group("GM", FamilyFixtures.Line("x-77", 30), FamilyFixtures.Line("x-77", 12));
        var decision = FamilyFixtures.Approve("curb-road", FamilyFixtures.T1, Array.Empty<string>(), gm);
        var ha = FamilyFixtures.Group("HA", FamilyFixtures.Line("x-77", 30, xref: FamilyFixtures.Ha));

        var before = FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { gm, ha }, Library);
        before[0].State.Should().Be(FamilyDecisionState.Applied);
        before[1].State.Should().Be(FamilyDecisionState.NotCovered, "GM → GM+HA never widens an old approval to HA");

        var both = FamilyFixtures.Approve("curb-road", FamilyFixtures.T2, Array.Empty<string>(), gm, ha);
        var history = FamilyDecisionPolicy.Supersede(new[] { decision }, decision.DecisionId!, both);

        history.Should().HaveCount(2);
        history[0].Status.Should().Be(FamilyDecisionPolicy.Superseded);
        history[0].SupersededBy.Should().Be(both.DecisionId);
        history[0].DecisionId.Should().Be(decision.DecisionId).And.Be(FamilyDecisionPolicy.DecisionId(history[0]),
            "superseding keeps the historical identity");
        decision.Status.Should().Be(FamilyDecisionPolicy.Active, "the input list is not modified");
        Codes(history.ToArray()).Should().BeEmpty();
        FamilyDecisionPolicy.Resolve(history, new[] { gm, ha }, Library)
            .Should().OnlyContain(r => r.State == FamilyDecisionState.Applied && r.DecisionId == both.DecisionId);
    }

    [Fact]
    public void SameTimeDecisionsOfDifferentFamiliesConflict_ALaterDecisionWins()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var p = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        var q = FamilyFixtures.Approve("sidewalk-paving", FamilyFixtures.T1, FamilyFixtures.Hatch, a);

        var conflict = One(new[] { p, q }, a);
        conflict.State.Should().Be(FamilyDecisionState.Stale);
        conflict.StaleReason.Should().Be(FamilyDecisionPolicy.StaleConflict);
        conflict.FamilyId.Should().BeNull();
        conflict.DecisionId.Should().BeNull();
        conflict.SelectorIndex.Should().Be(-1);

        var later = FamilyFixtures.Approve("sidewalk-paving", FamilyFixtures.T2, FamilyFixtures.Hatch, a);
        var resolved = One(new[] { p, later }, a);
        resolved.State.Should().Be(FamilyDecisionState.Applied);
        resolved.FamilyId.Should().Be("sidewalk-paving");
        resolved.DecisionId.Should().Be(later.DecisionId);
    }

    [Fact]
    public void AnOlderApprovalIsNeverAFallbackForANewerDecisionThatWentStale()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var older = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, Array.Empty<string>(), a);
        var newer = FamilyFixtures.Approve("sidewalk-paving", FamilyFixtures.T2, FamilyFixtures.Hatch, a);
        var repainted = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, hatch: "AR-CONC"));

        One(new[] { older }, repainted).State.Should().Be(FamilyDecisionState.Applied, "the older decision cites no evidence");
        var resolved = One(new[] { older, newer }, repainted);
        resolved.State.Should().Be(FamilyDecisionState.Stale);
        resolved.StaleReason.Should().Be(FamilyDecisionPolicy.StaleEvidence);
        resolved.DecisionId.Should().Be(newer.DecisionId);
    }

    [Fact]
    public void RoleGate_SurveyOrUtilitySourcesAreNeverApprovedOrApplied()
    {
        var survey = FamilyFixtures.Group("S", FamilyFixtures.HatchArea("asdasd23423", 100, xref: FamilyFixtures.Survey));
        survey.SourceRole.Should().Be(DraftSourceRole.Survey);
        var approveSurvey = () => FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, survey);
        approveSurvey.Should().Throw<InvalidOperationException>().WithMessage("*survey or existing-utilities*");

        var design = FamilyFixtures.Group("D", FamilyFixtures.HatchArea("asdasd23423", 100, xref: "6422-HA-NEW"));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, design);
        One(new[] { decision }, design).State.Should().Be(FamilyDecisionState.Applied);

        One(new[] { decision }, design with { SourceRole = DraftSourceRole.ExistingUtilities })
            .StaleReason.Should().Be(FamilyDecisionPolicy.StaleRole, "a declared existing-utilities group is never new work");
        var widened = FamilyFixtures.WithRules(Library.Rules,
            surveyPatterns: Library.SurveySourcePatterns.Append("*-HA-NEW").ToList());
        One(new[] { decision }, design, widened).StaleReason.Should().Be(FamilyDecisionPolicy.StaleRole,
            "the library now reads this source as survey");
    }

    [Fact]
    public void BasisGate_AFamilyNeverCoversAMeasurementItsRuleDoesNotMeasure()
    {
        var closed = FamilyFixtures.Group("C",
            FamilyFixtures.Rec(FamilyFixtures.Ha, "asdasd23423", "area", "closed-polyline-area", 100, "מ\"ר"));
        closed.MethodClass.Should().Be("closed-polyline");
        var approveClosed = () => FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, Array.Empty<string>(), closed);
        approveClosed.Should().Throw<InvalidOperationException>().WithMessage("*does not measure*");

        // A structurally valid decision whose family's basis rejects the addressed group is stale, not applied.
        var hatch = FamilyFixtures.Group("H", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, hatch);
        decision.FamilyId = "curb-road";
        decision.RuleVersion = LibraryIdentity.RuleFingerprint(Library.Rules.Single(r => r.Id == "curb-road"));
        decision.DecisionId = FamilyDecisionPolicy.DecisionId(decision);
        Codes(decision).Should().BeEmpty();

        One(new[] { decision }, hatch).StaleReason.Should().Be(FamilyDecisionPolicy.StaleBasis);
        FamilyDecisionPolicy.BasisAccepts(Library.Rules.Single(r => r.Id == "curb-road"), "length", "open-w15", "מ'")
            .Should().BeTrue("a drawn width decides pricing, not the family");
        FamilyDecisionPolicy.BasisAccepts(Library.Rules.Single(r => r.Id == "curb-road"), "length", "open", "??")
            .Should().BeFalse("an unknown unit is never accepted");
    }

    [Fact]
    public void RuleVersionChangeOrMissingFamilyMakesTheDecisionStale_ExplanatoryTextDoesNot()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        var rule = Library.Rules.Single(r => r.Id == "road-pavement");

        var renoted = FamilyFixtures.WithRules(Library.Rules.Select(r => r.Id == rule.Id ? r with { Note = "הערה אחרת" } : r).ToList());
        One(new[] { decision }, a, renoted).State.Should().Be(FamilyDecisionState.Applied);
        var nextEdition = FamilyFixtures.WithRules(Library.Rules, id: "mahod-roads-nti-urban-v1.3");
        One(new[] { decision }, a, nextEdition).State.Should().Be(FamilyDecisionState.Applied,
            "a new library edition with the same rule meaning keeps the decision");

        var changed = FamilyFixtures.WithRules(Library.Rules.Select(r => r.Id == rule.Id ? r with { Emits = rule.Emits.Take(1).ToList() } : r).ToList());
        One(new[] { decision }, a, changed).StaleReason.Should().Be(FamilyDecisionPolicy.StaleRuleChanged);
        var missing = FamilyFixtures.WithRules(Library.Rules.Where(r => r.Id != rule.Id).ToList());
        One(new[] { decision }, a, missing).StaleReason.Should().Be(FamilyDecisionPolicy.StaleLibraryMissing);
    }

    [Fact]
    public void ADecisionFromAnotherLibraryLineIsStale_AnEditionOfTheSameLineIsNot()
    {
        // Codex 01:27 (02/10): the same rule fingerprint in two libraries is not a portable approval.
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        One(new[] { decision }, a, FamilyFixtures.WithRules(Library.Rules, id: "mahod-roads-nti-urban-v2.0")).State
            .Should().Be(FamilyDecisionState.Applied, "a later edition of the same line keeps the decision");
        var otherLine = FamilyFixtures.WithRules(Library.Rules, id: EngineerBoqLibrary.LandscapeV1.Id);
        var resolved = One(new[] { decision }, a, otherLine);
        resolved.State.Should().Be(FamilyDecisionState.Stale);
        resolved.StaleReason.Should().Be(FamilyDecisionPolicy.StaleLibraryChanged);
        decision.Status.Should().Be(FamilyDecisionPolicy.Active, "a stale decision is kept in the profile, never removed");

        // Codex 02:37: an exclusion follows the same line rule — kept across editions, stale in another discipline.
        var exclusion = FamilyDecisionPolicy.CreateApproval(string.Empty, new[] { a }, FamilyFixtures.Hatch, Library,
            FamilyFixtures.Approver, "קו עזר גרפי", FamilyFixtures.T1, FamilyDecisionPolicy.Exclude);
        One(new[] { exclusion }, a, FamilyFixtures.WithRules(Library.Rules, id: "mahod-roads-nti-urban-v2.0")).State
            .Should().Be(FamilyDecisionState.Applied, "an exclusion survives a later edition of the same line");
        var excludedElsewhere = One(new[] { exclusion }, a, otherLine);
        excludedElsewhere.State.Should().Be(FamilyDecisionState.Stale);
        excludedElsewhere.StaleReason.Should().Be(FamilyDecisionPolicy.StaleLibraryChanged);
        exclusion.Status.Should().Be(FamilyDecisionPolicy.Active);
    }

    [Fact]
    public void EvidenceThatCanNoLongerBeReadMakesTheDecisionStale()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        var unreadable = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, hatchStatus: "unavailable:read-failed"));

        One(new[] { decision }, unreadable).StaleReason.Should().Be(FamilyDecisionPolicy.StaleEvidence);
    }

    [Fact]
    public void OnlyReadEvidenceMayBeCited()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var citeAbsent = () => FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, new[] { EvidenceKeys.PsetComponent }, a);
        citeAbsent.Should().Throw<InvalidOperationException>().WithMessage("*only read evidence may be cited*");
        var citeStatus = () => FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, new[] { EvidenceKeys.Hatch + "_status" }, a);
        citeStatus.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordsThatDoNotMatchTheirGroupAreNeverFingerprintedOrApplied()
    {
        var honest = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, honest);
        var mislabelled = honest with { Records = new[] { FamilyFixtures.HatchArea("another-layer", 100) } };

        FamilyDecisionPolicy.GroupFactsProblem(mislabelled).Should().Contain("another layer");
        One(new[] { decision }, mislabelled).StaleReason.Should().Be(FamilyDecisionPolicy.StaleScope);
        var approve = () => FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, mislabelled);
        approve.Should().Throw<InvalidOperationException>().WithMessage("*another layer*");
    }

    [Fact]
    public void DrawnWidthClassesShareOnePreWidthSelector()
    {
        var line = FamilyFixtures.Line("k-55", 80, xref: FamilyFixtures.Sm);
        var open = FamilyFixtures.Group("L", line);
        open.MethodClass.Should().Be("open");
        var decision = FamilyFixtures.Approve("marking-lines", FamilyFixtures.T1, Array.Empty<string>(), open);
        decision.Selectors.Single().MethodClass.Should().Be("open");

        One(new[] { decision }, open with { MethodClass = "open-w15" }).State.Should().Be(FamilyDecisionState.Applied);
        One(new[] { decision }, open with { MethodClass = "open-width-unproven" }).State.Should().Be(FamilyDecisionState.Applied);
    }

    [Fact]
    public void AnEvidenceSelectorCoversNewRandomLayersWithTheSameMeaningButNeverANewSource()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100, hatch: "AR-CONC"));
        var match = new[] { new FamilyEvidenceMatch { Key = EvidenceKeys.Hatch, Text = "AR-CONC" } };
        var decision = FamilyDecisionPolicy.CreateEvidenceApproval("sidewalk-paving", new[] { a }, match,
            Array.Empty<string>(), Library, FamilyFixtures.Approver, "דפוס הצללה של מדרכה", FamilyFixtures.T1);

        decision.Selectors.Should().ContainSingle().Which.LayerLeaf.Should().BeNull();
        decision.EvidenceKeys.Should().Equal(EvidenceKeys.Hatch);
        Codes(decision).Should().BeEmpty();

        var sameMeaning = FamilyFixtures.Group("C", FamilyFixtures.HatchArea("qqq-777", 60, hatch: "AR-CONC"));
        var otherPattern = FamilyFixtures.Group("D", FamilyFixtures.HatchArea("qqq-888", 60, hatch: "ANSI31"));
        var otherSource = FamilyFixtures.Group("E", FamilyFixtures.HatchArea("qqq-777", 60, hatch: "AR-CONC", xref: "6422-HA-OTHER"));
        FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { a, sameMeaning, otherPattern, otherSource }, Library)
            .Select(r => r.State).Should().Equal(
                FamilyDecisionState.Applied, FamilyDecisionState.Applied, FamilyDecisionState.NotCovered, FamilyDecisionState.NotCovered);

        var mixed = FamilyFixtures.Group("M", FamilyFixtures.HatchArea("m1", 10, hatch: "AR-CONC"), FamilyFixtures.HatchArea("m1", 10, hatch: "ANSI31"));
        var approveMixed = () => FamilyDecisionPolicy.CreateEvidenceApproval("sidewalk-paving", new[] { mixed }, match,
            Array.Empty<string>(), Library, FamilyFixtures.Approver, "r", FamilyFixtures.T1);
        approveMixed.Should().Throw<InvalidOperationException>().WithMessage("*every record*");
    }

    [Fact]
    public void AnExclusionResolvesWithoutAFamilyAndConflictsWithAnApprovalAtTheSameTime()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var exclusion = FamilyDecisionPolicy.CreateApproval(string.Empty, new[] { a }, FamilyFixtures.Hatch, Library,
            FamilyFixtures.Approver, "קו עזר גרפי", FamilyFixtures.T1, FamilyDecisionPolicy.Exclude);

        exclusion.FamilyId.Should().BeNull();
        exclusion.RuleVersion.Should().BeNull();
        Codes(exclusion).Should().BeEmpty();
        var resolved = One(new[] { exclusion }, a);
        resolved.State.Should().Be(FamilyDecisionState.Applied);
        resolved.FamilyId.Should().BeNull();
        resolved.IsExclusion.Should().BeTrue();

        var named = () => FamilyDecisionPolicy.CreateApproval("road-pavement", new[] { a }, FamilyFixtures.Hatch, Library,
            FamilyFixtures.Approver, "r", FamilyFixtures.T1, FamilyDecisionPolicy.Exclude);
        named.Should().Throw<ArgumentException>();
        var approval = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        One(new[] { exclusion, approval }, a).StaleReason.Should().Be(FamilyDecisionPolicy.StaleConflict);
    }

    [Fact]
    public void DecisionIdIsContentIdentity()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100), FamilyFixtures.HatchArea("asdasd23423", 40));
        var b = FamilyFixtures.Group("B", FamilyFixtures.HatchArea("qwe987", 250));
        var first = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a, b);

        FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, b, a).DecisionId
            .Should().Be(first.DecisionId, "group order does not matter");
        var remeasured = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 999, drawingHash: new string('d', 64)));
        FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, remeasured, b).DecisionId
            .Should().Be(first.DecisionId, "quantities, handles and the drawing hash do not take part");

        var copy = FamilyDecisionPolicy.Clone(first)!;
        copy.Status = FamilyDecisionPolicy.Revoked;
        copy.RevokedReason = "בוטל";
        copy.SupersededBy = new string('c', 64);
        FamilyDecisionPolicy.DecisionId(copy).Should().Be(first.DecisionId, "status history is not content");
        copy.Reason = "line one\r\nline two";
        var withCrLf = FamilyDecisionPolicy.DecisionId(copy);
        withCrLf.Should().NotBe(first.DecisionId);
        copy.Reason = "line one\nline two";
        FamilyDecisionPolicy.DecisionId(copy).Should().Be(withCrLf, "YAML line-break normalization cannot change the identity");
        FamilyFixtures.Approve("road-pavement", FamilyFixtures.T2, FamilyFixtures.Hatch, a, b).DecisionId
            .Should().NotBe(first.DecisionId);
    }

    [Fact]
    public void ValidateReportsEveryStructuralCodeAndNothingForACompleteDecision()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var valid = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        FamilyDecision Copy(Action<FamilyDecision> edit, bool reidentify = false)
        {
            var copy = FamilyDecisionPolicy.Clone(valid)!;
            edit(copy);
            if (reidentify) copy.DecisionId = FamilyDecisionPolicy.DecisionId(copy);
            return copy;
        }

        var nullList = new ProjectProfile.EstimateProfile { FamilyDecisions = null! };
        FamilyDecisionPolicy.Validate(nullList).Should().ContainSingle().Which.Code.Should().Be(FamilyDecisionPolicy.ListNullCode);
        Codes(valid).Should().BeEmpty();

        var catalog = FamilyFixtures.Catalog(new string('a', 64));
        var item = FamilyDecisionPolicy.CreateItemApproval("TEST.1", catalog, "מ\"ר", FamilyFixtures.Approver, FamilyFixtures.T1);
        FamilyDecision Override(double value) => Copy(d => d.ParameterOverrides.Add(new ProjectProfile.EstimateProfile.FamilyParameterOverride
        {
            Key = "FULL_DEPTH_SHARE", Value = value, Reason = "r", ApprovedBy = FamilyFixtures.Approver, ApprovedAtUtc = FamilyFixtures.T1,
        }), reidentify: true);

        var incomplete = new[]
        {
            Copy(d => d.ApprovedBy = " ", reidentify: true),
            Copy(d => d.ApprovedAtUtc = null, reidentify: true),
            Copy(d => d.Selectors[0].LayerLeaf = null, reidentify: true),
            Copy(d => d.Selectors[0].Source = null, reidentify: true),
            Copy(d => d.Selectors[0].EvidenceFingerprint = "abc", reidentify: true),
            Copy(d => d.Selectors.Clear(), reidentify: true),
            Copy(d => d.RuleVersion = null, reidentify: true),
            Copy(d => d.FamilyId = "road-pavement@w10", reidentify: true),
            Copy(d => d.Status = FamilyDecisionPolicy.Superseded),
            Copy(d => d.Decision = "split", reidentify: true),
            Copy(d => d.DecisionId = "not-a-sha"),
            Copy(d => d.ItemApprovals.Add(new ProjectProfile.EstimateProfile.FamilyItemApproval { CatalogCode = "TEST.1" }), reidentify: true),
            Override(double.NaN),
            Override(double.PositiveInfinity),
        };
        foreach (var decision in incomplete)
            Codes(decision).Should().Equal(new[] { FamilyDecisionPolicy.IncompleteCode }, "each entry has one structural problem");

        Codes(Override(0.4)).Should().BeEmpty();
        Codes(Copy(d => d.Reason = "edited after approval")).Should().Equal(FamilyDecisionPolicy.IdMismatchCode);
        Codes(valid, FamilyDecisionPolicy.Clone(valid)!).Should().Equal(FamilyDecisionPolicy.DuplicateCode);
        Codes(valid, Copy(d => { d.Status = FamilyDecisionPolicy.Revoked; d.RevokedReason = "r"; })).Should().BeEmpty(
            "history may repeat an identity; only two active entries compete");
        Codes(Copy(d => { d.ItemApprovals.Add(item); d.ItemApprovals.Add(FamilyDecisionPolicy.CreateItemApproval("test.1", catalog, null, "other", FamilyFixtures.T2)); }, reidentify: true))
            .Should().Equal(FamilyDecisionPolicy.ItemApprovalDuplicateCode);
        Codes(Copy(d =>
        {
            d.ParameterOverrides.Add(new() { Key = "FULL_DEPTH_SHARE", Value = 0.4, Reason = "r", ApprovedBy = "n", ApprovedAtUtc = FamilyFixtures.T1 });
            d.ParameterOverrides.Add(new() { Key = "FULL_DEPTH_SHARE", Value = 0.5, Reason = "r", ApprovedBy = "n", ApprovedAtUtc = FamilyFixtures.T1 });
        }, reidentify: true)).Should().Equal(FamilyDecisionPolicy.ParameterOverrideDuplicateCode);
    }

    [Fact]
    public void RevokeAndSupersedeKeepHistoryAndRefuseUnknownTargets()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var decision = FamilyFixtures.Approve("road-pavement", FamilyFixtures.T1, FamilyFixtures.Hatch, a);

        var revoked = FamilyDecisionPolicy.Revoke(new[] { decision }, decision.DecisionId!, "זוהה בטעות\nבבדיקה");

        revoked.Should().ContainSingle();
        revoked[0].Status.Should().Be(FamilyDecisionPolicy.Revoked);
        revoked[0].RevokedReason.Should().Be("זוהה בטעות בבדיקה");
        revoked[0].DecisionId.Should().Be(decision.DecisionId);
        decision.Status.Should().Be(FamilyDecisionPolicy.Active);
        Codes(revoked.ToArray()).Should().BeEmpty();
        One(revoked, a).State.Should().Be(FamilyDecisionState.NotCovered);

        var again = () => FamilyDecisionPolicy.Revoke(revoked, decision.DecisionId!, "again");
        again.Should().Throw<InvalidOperationException>();
        var other = FamilyFixtures.Approve("sidewalk-paving", FamilyFixtures.T2, FamilyFixtures.Hatch, a);
        var unknown = () => FamilyDecisionPolicy.Supersede(new[] { decision }, new string('e', 64), other);
        unknown.Should().Throw<InvalidOperationException>();
        var noReason = () => FamilyDecisionPolicy.Revoke(new[] { decision }, decision.DecisionId!, "  ");
        noReason.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ItemApprovalIsCurrentOnlyForTheExactPriceListItem()
    {
        var catalog = FamilyFixtures.Catalog(new string('a', 64));
        var approval = FamilyDecisionPolicy.CreateItemApproval("TEST.1", catalog, "מ\"ר", FamilyFixtures.Approver, FamilyFixtures.T1);

        FamilyDecisionPolicy.IsItemApprovalCurrent(approval, catalog).Should().BeTrue();
        FamilyDecisionPolicy.IsItemApprovalCurrent(approval, FamilyFixtures.Catalog(new string('b', 64))).Should().BeFalse("another price-list file");
        FamilyDecisionPolicy.IsItemApprovalCurrent(approval, FamilyFixtures.Catalog(new string('a', 64), "Changed description"))
            .Should().BeFalse("the item the engineer saw changed");
        var otherBook = FamilyFixtures.Catalog(new string('a', 64));
        FamilyDecisionPolicy.IsItemApprovalCurrent(approval, new CatalogSnapshot
        {
            SnapshotId = "OTHER-BOOK", FileHash = otherBook.FileHash, Items = otherBook.Items,
        }).Should().BeFalse("another snapshot id");
        var incomplete = FamilyDecisionPolicy.CreateItemApproval("TEST.1", catalog, null, FamilyFixtures.Approver, FamilyFixtures.T1);
        incomplete.ApprovedBy = null;
        FamilyDecisionPolicy.IsItemApprovalCurrent(incomplete, catalog).Should().BeFalse();
        var absent = () => FamilyDecisionPolicy.CreateItemApproval("NOT-REAL", catalog, null, FamilyFixtures.Approver, FamilyFixtures.T1);
        absent.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreationRefusesWhatItCannotProve()
    {
        var a = FamilyFixtures.Group("A", FamilyFixtures.HatchArea("asdasd23423", 100));
        var variant = () => FamilyFixtures.Approve("marking-lines@w10", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        variant.Should().Throw<ArgumentException>();
        var unknownFamily = () => FamilyFixtures.Approve("no-such-family", FamilyFixtures.T1, FamilyFixtures.Hatch, a);
        unknownFamily.Should().Throw<InvalidOperationException>();
        var unspecifiedTime = () => FamilyFixtures.Approve("road-pavement",
            DateTime.SpecifyKind(FamilyFixtures.T1, DateTimeKind.Unspecified), FamilyFixtures.Hatch, a);
        unspecifiedTime.Should().Throw<ArgumentException>();
        var injectedApprover = () => FamilyDecisionPolicy.CreateApproval("road-pavement", new[] { a }, FamilyFixtures.Hatch, Library,
            "name\nprofile_id: other", "r", FamilyFixtures.T1);
        injectedApprover.Should().Throw<ArgumentException>();
        var noReason = () => FamilyDecisionPolicy.CreateApproval("road-pavement", new[] { a }, FamilyFixtures.Hatch, Library,
            FamilyFixtures.Approver, " \n ", FamilyFixtures.T1);
        noReason.Should().Throw<ArgumentException>();
        var noGroups = () => FamilyDecisionPolicy.CreateApproval("road-pavement", Array.Empty<RecognitionGroupInput>(), FamilyFixtures.Hatch,
            Library, FamilyFixtures.Approver, "r", FamilyFixtures.T1);
        noGroups.Should().Throw<ArgumentException>();
    }
}
