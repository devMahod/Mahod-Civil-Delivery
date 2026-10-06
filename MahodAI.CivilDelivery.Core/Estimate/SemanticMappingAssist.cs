using System.Text.Json;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Shared;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.CivilDelivery.Estimate;

public interface ISemanticMappingAssistant
{
    Task<SemanticMappingAssistResult> AssistAsync(MappingProposalEngine.DiscoveredGroup group,
        CatalogSnapshot catalog, string? engineerContext, CancellationToken cancellationToken);
}

public sealed record SemanticMappingAssistResult(IReadOnlyList<MappingProposal> Proposals,
    string Message, bool IsAbstained)
{
    public string ContextId { get; init; } = "";
    public string CatalogId { get; init; } = "";
    public string CatalogHash { get; init; } = "";
}

// Deliberately narrow wire contracts: no quantities, prices, paths, coordinates or approvals.
public sealed record SemanticEvidence(string Key, string Value);
public sealed record SemanticSearchTerm(string Text, string EvidenceKey, string Quote);
public sealed record SemanticSearchRequest(string ContextId, string CanonicalUnit,
    IReadOnlyList<SemanticEvidence> Evidence);
public sealed record SemanticSearchResponse(string ContextId, IReadOnlyList<SemanticSearchTerm> Terms,
    string? AbstentionReason = null);
public sealed record SemanticRankCandidate(string Code, string Description, string CanonicalUnit,
    string ItemFingerprint);
public sealed record SemanticRankRequest(string ContextId, string CatalogHash, string CanonicalUnit,
    IReadOnlyList<SemanticEvidence> Evidence, IReadOnlyList<SemanticSearchTerm> SearchTerms,
    IReadOnlyList<SemanticRankCandidate> Candidates);
public sealed record SemanticRankedCode(string Code, string Explanation, IReadOnlyList<string> EvidenceKeys);
public sealed record SemanticRankResponse(string ContextId, string CatalogHash,
    IReadOnlyList<SemanticRankedCode> Ranked, string? AbstentionReason = null);

public interface ISemanticMappingProvider
{
    Task<SemanticSearchResponse> SuggestSearchTermsAsync(SemanticSearchRequest request, CancellationToken ct);
    Task<SemanticRankResponse> RankCandidatesAsync(SemanticRankRequest request, CancellationToken ct);
}

/// <summary>
/// Semantic hypotheses are retrieval hints, not facts or approvals. Every returned code is
/// reconstructed from an immutable local catalog copy and passes the existing semantic/unit guard.
/// This class has no profile, file writer, quantity calculator or price provider.
/// </summary>
public sealed class SemanticMappingAssist : ISemanticMappingAssistant
{
    public const int MaxCandidates = 80;
    public const int MaxEngineerContextLength = 500;
    public const string EvidenceKind = "ai-semantic-v1";
    public const string ReviewWarning = "השערה סמנטית בלבד — לא אישור מיפוי או מחיר. נדרש לבדוק התאמה, מידות ובסיס תשלום מול המקור לפני אישור הנדסי.";
    private const string ManualFallback = "לא התקבלה הצעה סמנטית מבוססת. בחרו מה השכבה מייצגת או הוסיפו תיאור של הקבוצה; אפשר גם להמשיך בחיפוש ידני בקטלוג.";
    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
    { "cad_block_name_effective", "cad_block_name_raw", "cad_entity_linetype", "cad_layer_linetype" };
    private static readonly Regex Words = new("[A-Za-zא-ת]+", RegexOptions.CultureInvariant);
    private static readonly Regex PrivateText = new(
        @"[A-Za-z]:[\\/]|\\\\|https?://|[/\\]|[\r\n\x00-\x08]|\b\d{5,}(?:\.\d+)?\s*[,; ]\s*\d{5,}|[₪$€]|\b(?:api[_ -]?key|token|password|price|quantity)\b|מחיר|כמות",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    { "layer", "block", "q", "unknown", "default", "bylayer", "byblock", "continuous", "unnamed", "none", "null" };
    private static readonly Regex ContactText = new(@"[^\s@]+@[^\s@]+|(?:\+?\d[\s().-]*){8,}", RegexOptions.CultureInvariant);
    private readonly ISemanticMappingProvider _provider;
    private readonly TimeSpan _timeout;

    public SemanticMappingAssist(ISemanticMappingProvider provider) : this(provider, TimeSpan.FromSeconds(30)) { }
    internal SemanticMappingAssist(ISemanticMappingProvider provider, TimeSpan timeout)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    public async Task<SemanticMappingAssistResult> AssistAsync(MappingProposalEngine.DiscoveredGroup group,
        CatalogSnapshot catalog, string? engineerContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(catalog);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            // Copy before the first await; a provider never receives either mutable source object.
            var copiedGroup = group with { CadMetadata = group.CadMetadata?.Select(field => field with
                { Values = Array.AsReadOnly(field.Values.ToArray()) }).ToArray(),
                RecognitionEvidence = CatalogEvidenceBridge.Snapshot(group.RecognitionEvidence) };
            var items = catalog.Items.Select(pair => new KeyValuePair<string, CatalogItem>(pair.Key,
                new CatalogItem { Code = pair.Value.Code, Description = pair.Value.Description, UnitRaw = pair.Value.UnitRaw })).ToArray();
            var catalogId = catalog.SnapshotId;
            var catalogHash = catalog.FileHash;
            if (string.IsNullOrWhiteSpace(catalogId) || !CatalogIdentity.IsValidSha256(catalogHash) ||
                items.Any(pair => !string.Equals(pair.Key, pair.Value.Code, StringComparison.OrdinalIgnoreCase)) ||
                items.Select(pair => pair.Value.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
                return Abstain();
            var unit = Units.Parse(copiedGroup.MeasuredUnit);
            if (unit.Canonical == "?" || copiedGroup.ObjectCount <= 0 ||
                !double.IsFinite(copiedGroup.TotalQuantity) || !MappingProposalEngine.IsProposalEligible(copiedGroup)) return Abstain();
            if (!TryEvidence(copiedGroup, engineerContext, out var evidence)) return Abstain();
            if (!MappingProposalEngine.HasAutomaticMeasurementSubjectEvidence(copiedGroup, engineerContext))
                return Abstain(MappingProposalEngine.CountSubjectReviewRequired);
            var catalogDigest = CatalogDigest(catalogId, catalogHash, items);
            var contextId = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
                { Group = copiedGroup, Evidence = evidence, Catalog = catalogDigest }));
            var searchRequest = new SemanticSearchRequest(contextId, unit.Canonical, evidence);
            var search = await _provider.SuggestSearchTermsAsync(searchRequest, budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
            if (!ValidateSearch(searchRequest, search)) return Abstain();
            var terms = Array.AsReadOnly(search.Terms.Select(term => term with { }).ToArray());
            var candidates = items.Select(pair => pair.Value)
                .Where(item => item.Unit.SameUnit(unit) &&
                    IsCandidateCompatibleWithEvidence(copiedGroup, item.Description, evidence))
                .Select(item => (Item: item, Hits: terms.Count(term => MappingProposalEngine.ContainsWord(item.Description, term.Text))))
                .Where(hit => hit.Hits > 0).OrderByDescending(hit => hit.Hits)
                .ThenBy(hit => hit.Item.Code, StringComparer.OrdinalIgnoreCase).Take(MaxCandidates)
                .Select(hit => new SemanticRankCandidate(hit.Item.Code, hit.Item.Description,
                    hit.Item.Unit.Canonical, CatalogIdentity.ItemFingerprint(hit.Item))).ToArray();
            if (candidates.Length == 0) return Abstain();
            var rankRequest = new SemanticRankRequest(contextId, catalogHash, unit.Canonical,
                evidence, terms, Array.AsReadOnly(candidates));
            var ranked = await _provider.RankCandidatesAsync(rankRequest, budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            if (!ValidateRanking(rankRequest, ranked)) return Abstain();
            // Avoid publishing a response against a source catalog/group edited while the call ran.
            if (!string.Equals(catalogDigest, CatalogDigest(catalog.SnapshotId, catalog.FileHash, catalog.Items), StringComparison.Ordinal) ||
                JsonSerializer.Serialize(group) != JsonSerializer.Serialize(copiedGroup)) return Abstain();
            var proposals = ranked.Ranked.Select(choice =>
            {
                var item = candidates.Single(candidate => candidate.Code == choice.Code);
                return new MappingProposal
                {
                    RuleKey = copiedGroup.RuleKey, Layer = copiedGroup.Layer,
                    MeasurementKind = copiedGroup.MeasurementKind, MeasuredUnit = copiedGroup.MeasuredUnit,
                    ObjectCount = copiedGroup.ObjectCount, TotalQuantity = copiedGroup.TotalQuantity,
                    ProposedCode = item.Code, CatalogDescription = item.Description, CatalogUnit = item.CanonicalUnit,
                    // An ordered hypothesis, not a calibrated confidence or a proven profile rule.
                    Score = 0, EvidenceKind = EvidenceKind,
                    Reasons = new List<string> { ReviewWarning, "השערת העוזר: " + choice.Explanation,
                        "מקורות להשערה: " + string.Join("; ", choice.EvidenceKeys.Select(key =>
                            evidence.Single(value => value.Key == key)).Select(value => EvidenceCitation(copiedGroup, value))),
                        "catalog_item_fingerprint=" + item.ItemFingerprint, "semantic_context=" + contextId }
                };
            }).ToArray();
            return new SemanticMappingAssistResult(Array.AsReadOnly(proposals), ReviewWarning, false)
                { ContextId = contextId, CatalogId = catalogId, CatalogHash = catalogHash };
        }
        catch (OperationCanceledException)
        {
            return Abstain(cancellationToken.IsCancellationRequested
                ? "בקשת ההצעות בוטלה. לא נשמר שינוי; החיפוש הידני נשאר זמין."
                : "בקשת ההצעות לא הסתיימה בזמן. לא נשמר שינוי; ניתן לנסות שוב או לחפש ידנית.");
        }
        catch (Exception)
        {
            // Provider/HTTP/parser exceptions may contain bodies or credentials: never surface them.
            return Abstain("שירות ההצעות אינו זמין כרגע. לא נשמר שינוי; ניתן להמשיך בחיפוש ידני.");
        }
        finally { budget.Cancel(); }
    }

    private static SemanticMappingAssistResult Abstain(string message = ManualFallback) =>
        new(Array.Empty<MappingProposal>(), message, true);

    /// <summary>
    /// Shared AI/editor boundary: only the same admissible source fields and engineer
    /// context may qualify a proposal. This neither checks pricing nor approves a mapping.
    /// </summary>
    public static bool IsCandidateCompatible(MappingProposalEngine.DiscoveredGroup group,
        string description, string? engineerContext)
    {
        ArgumentNullException.ThrowIfNull(group);
        return TryEvidence(group, engineerContext, out var evidence) &&
            IsCandidateCompatibleWithEvidence(group, description, evidence);
    }

    private static bool IsCandidateCompatibleWithEvidence(MappingProposalEngine.DiscoveredGroup group,
        string description, IReadOnlyList<SemanticEvidence> evidence) =>
        MappingProposalEngine.IsAutomaticMeasurementSubjectCompatible(group, description,
            evidence.FirstOrDefault(value => value.Key == "engineer_context")?.Value) &&
        MappingProposalEngine.IsSemanticallyCompatible(group.Layer, description,
            string.Join("\n", evidence.Select(value => value.Value)));

    private static bool TryEvidence(MappingProposalEngine.DiscoveredGroup group, string? context,
        out IReadOnlyList<SemanticEvidence> evidence)
    {
        var result = new List<SemanticEvidence>();
        evidence = Array.Empty<SemanticEvidence>();
        if (MappingProposalEngine.EvidenceRefusal(group) != null) return false;
        if (context?.Length > MaxEngineerContextLength || (!string.IsNullOrWhiteSpace(context) && !SafeText(context, 500))) return false;
        // XREF prefixes can encode source filenames; only the actual layer leaf is a subject hint.
        var layer = (group.Layer ?? "").Split('|').Last();
        if (!string.IsNullOrWhiteSpace(layer))
        {
            if (!SafeText(layer, 250)) return false;
            result.Add(new SemanticEvidence("layer", layer));
        }
        var fields = (group.CadMetadata ?? Array.Empty<QuantityCadMetadataPolicy.FieldSummary>())
            .Where(field => MetadataKeys.Contains(field.Key)).ToArray();
        if (fields.Any(field => field.Key == "cad_block_name_effective"))
            fields = fields.Where(field => field.Key != "cad_block_name_raw").ToArray();
        if (fields.GroupBy(field => field.Key).Any(grouped => grouped.Count() != 1) ||
            fields.Any(field => field.IsMixed || field.RecordCount != group.ObjectCount)) return false;
        foreach (var field in fields)
        {
            if (!SafeText(field.Values[0], 500)) return false;
            result.Add(new SemanticEvidence(field.Key, field.Values[0]));
        }
        // Only the bridge's uniform, fully read, object-bound subjects: never the raw ev_* JSON,
        // nearby text, handles, source paths, field/tag names, family codes or a truncated text.
        // The key is a stable local index; detailed attribution is reconstructed locally in Reasons.
        var subjects = group.RecognitionEvidence?.Subjects ?? Array.Empty<CatalogEvidenceBridge.Subject>();
        if (subjects.Count > CatalogEvidenceBridge.MaxSubjects || subjects.Any(subject =>
                !CatalogEvidenceBridge.SubjectKeys.Contains(subject.Key, StringComparer.Ordinal) ||
                subject.RecordCount != group.ObjectCount ||
                !SafeText(subject.Value, 500) || ContactText.IsMatch(subject.Value))) return false;
        for (var i = 0; i < subjects.Count; i++)
        {
            var subject = subjects[i];
            if (MappingProposalEngine.HasKnownSubjectVocabulary(subject.Value))
                result.Add(new SemanticEvidence(BridgeKey(subject, i), subject.Value));
        }
        if (!string.IsNullOrWhiteSpace(context)) result.Add(new SemanticEvidence("engineer_context", context.Trim()));
        if (!result.Any(AdmissibleSubject)) return false;
        evidence = Array.AsReadOnly(result.ToArray());
        return true;
    }

    private static string BridgeKey(CatalogEvidenceBridge.Subject subject, int index) => subject.Key + ":" + index;

    private static string EvidenceCitation(MappingProposalEngine.DiscoveredGroup group, SemanticEvidence value)
    {
        var subjects = group.RecognitionEvidence?.Subjects ?? Array.Empty<CatalogEvidenceBridge.Subject>();
        for (var i = 0; i < subjects.Count; i++)
            if (BridgeKey(subjects[i], i) == value.Key) return subjects[i].Citation;
        return value.Key + " = " + value.Value;
    }

    private static bool ValidateSearch(SemanticSearchRequest request, SemanticSearchResponse? response)
    {
        if (response == null || response.ContextId != request.ContextId ||
            !string.IsNullOrWhiteSpace(response.AbstentionReason) || response.Terms == null || response.Terms.Count is < 1 or > 5 ||
            response.Terms.Select(term => term.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Terms.Count) return false;
        return response.Terms.All(term => SafeText(term.Text, 80) && Regex.IsMatch(term.Text, "[א-ת]{2}") &&
            term.Quote.Length is >= 2 and <= 500 &&
            request.Evidence.Any(evidence => evidence.Key == term.EvidenceKey && AdmissibleSubject(evidence) &&
                AdmissibleSubject(new SemanticEvidence(evidence.Key, term.Quote)) &&
                evidence.Value.Contains(term.Quote, StringComparison.Ordinal)));
    }

    private static bool ValidateRanking(SemanticRankRequest request, SemanticRankResponse? response)
    {
        if (response == null || response.ContextId != request.ContextId || response.CatalogHash != request.CatalogHash ||
            !string.IsNullOrWhiteSpace(response.AbstentionReason) || response.Ranked == null || response.Ranked.Count is < 1 or > 3 ||
            response.Ranked.Select(choice => choice.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Ranked.Count) return false;
        return response.Ranked.All(choice => SafeText(choice.Explanation, 700) &&
            choice.EvidenceKeys != null && choice.EvidenceKeys.Count is >= 1 and <= 6 &&
            choice.EvidenceKeys.Distinct(StringComparer.Ordinal).Count() == choice.EvidenceKeys.Count &&
            choice.EvidenceKeys.All(key => request.Evidence.Any(value => value.Key == key && AdmissibleSubject(value))) &&
            // At least one explicit source supporting the search must also support the ranking.
            choice.EvidenceKeys.Any(key => request.SearchTerms.Any(term => term.EvidenceKey == key)) &&
            request.Candidates.Any(item => item.Code == choice.Code && item.CanonicalUnit == request.CanonicalUnit &&
                CatalogIdentity.IsValidSha256(item.ItemFingerprint)));
    }

    private static bool SafeText(string? text, int max) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= max && !PrivateText.IsMatch(text);
    // An explicit engineer description is a supplied interpretation, not a fact
    // inferred from a CAD identifier. Preserve free description/translation while
    // requiring known vocabulary for raw layer, block and line-type evidence.
    // An unknown layer is retained as context, but cannot be cited to manufacture
    // a subject even when independent engineer context allowed the request.
    private static bool AdmissibleSubject(SemanticEvidence evidence) =>
        MappingProposalEngine.HasKnownSubjectVocabulary(evidence.Value) ||
        evidence.Key == "engineer_context" && Meaningful(evidence.Value);
    private static bool Meaningful(string text) => Words.Matches(text).Select(match => match.Value)
        .Any(word => word.Length >= 3 && !GenericWords.Contains(word));
    private static string CatalogDigest(string id, string hash, IEnumerable<KeyValuePair<string, CatalogItem>> items) =>
        ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new { Id = id, Hash = hash,
            Items = items.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new { pair.Key, Fingerprint = CatalogIdentity.ItemFingerprint(pair.Value) }).ToArray() }));
}
