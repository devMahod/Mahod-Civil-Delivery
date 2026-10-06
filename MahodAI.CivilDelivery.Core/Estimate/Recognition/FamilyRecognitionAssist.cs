using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

// Deliberately narrow wire contracts: no quantities (a count group's size included), prices, paths, coordinates,
// matrices, handles, record ids, XREF/file names, rule keys or approvals. Image bytes never travel inside the JSON;
// only their hashes do.

/// <summary>
/// One evidence key aggregated over the group. <see cref="Status"/> is <c>read</c> (citable: CAD evidence read from the
/// drawing, or the engineer's own description), <c>identifier</c> (the layer name: context only, never citable) or the
/// state of evidence that could not be used (<c>absent</c>, <c>unavailable</c>, <c>invalid</c>) with an empty value.
/// <see cref="Value"/> lists distinct texts with the number of records carrying each; <see cref="Coverage"/> of
/// <see cref="Total"/> records carry a usable value. For a <c>count</c> group the number of records is the measured
/// quantity, so the per-text numbers, <see cref="Coverage"/> and <see cref="Total"/> are coarse percentages of the
/// group's records instead (multiples of 10, <see cref="Total"/> = 100).
/// </summary>
public sealed record FamilyRankEvidence(string Key, string Value, string Status, int Coverage, int Total);

/// <summary>A library family whose quantity basis accepts the group, identified by its rule fingerprint.</summary>
public sealed record FamilyRankCandidate(string FamilyId, string Element, string Basis, string RuleFingerprint);

/// <summary>An image that travels beside the request: its SHA-256 and kind (<c>group-preview</c> | <c>legend-crop</c>).</summary>
public sealed record FamilyRankImage(string Sha256, string Kind);

public sealed record FamilyRankRequest(string ContextId, string LibraryId, string LibraryHash, string MeasurementKind,
    string CanonicalUnit, string MethodClass, IReadOnlyList<FamilyRankEvidence> Evidence, IReadOnlyList<string> WithheldKeys,
    IReadOnlyList<FamilyRankCandidate> Candidates, IReadOnlyList<FamilyRankImage> Images)
{
    /// <summary>
    /// The validated image bytes and the engineer's permit. Never serialized: a transport may send the bytes as separate
    /// parts only when <see cref="VisionImagePolicy.IsPermitted"/> holds for this request.
    /// </summary>
    [JsonIgnore] public VisionPayload? Vision { get; init; }
}

/// <summary>Text citations quote a sent value. An image:sha256 citation describes a visual hypothesis,
/// not a verbatim quote or a verified CAD observation; it can name only an image in this exact request.</summary>
public sealed record FamilyRankCitation(string EvidenceKey, string Quote);
public sealed record FamilyRankChoice(string FamilyId, string Explanation, IReadOnlyList<FamilyRankCitation> Citations,
    IReadOnlyList<string>? MissingDetails = null);
public sealed record FamilyRankResponse(string ContextId, string LibraryHash, IReadOnlyList<FamilyRankChoice> Ranked,
    string? AbstentionReason = null);

public interface IFamilyRecognitionProvider
{
    Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken ct);
}

/// <summary>
/// Implemented by a family-ranking transport whose images also depend on an organisation policy. The assist asks it
/// before sending, so a disabled policy is reported as "images are off; ask without images" instead of as an outage.
/// </summary>
public interface IFamilyVisionGate
{
    /// <summary>True only when the organisation policy currently allows images.</summary>
    bool IsVisionAllowed();
}

/// <summary>
/// The exact request that would be sent for a group (for the disclosure shown to the engineer and for binding a
/// <see cref="VisionSendPermit"/> to its <see cref="FamilyRankRequest.ContextId"/>), or the reason none would be sent.
/// </summary>
public sealed record FamilyRankPreparation(FamilyRankRequest? Request, string? AbstentionMessage);

/// <summary>
/// Fixed public classes of an assistant answer that was received but rejected. Diagnostic only: a class names the rule that
/// failed first (subject citation, supporting citation, the model's own prose, its abstention reason…), never what the
/// model wrote, and acceptance never depends on which class is reported.
/// </summary>
public static class FamilyRankRejection
{
    /// <summary>The provider returned no answer object.</summary>
    public const string ResponseMissing = "response_missing";
    /// <summary>context_id or library_hash is not the request's.</summary>
    public const string ContextMismatch = "context_or_library_mismatch";
    /// <summary>No list, more than three or repeated families, a missing family id, or a ranking beside an abstention reason.</summary>
    public const string RankingMalformed = "ranking_malformed";
    /// <summary>A ranked family is not in the request's closed candidate list.</summary>
    public const string FamilyNotCandidate = "family_not_candidate";
    /// <summary>An explanation or missing_details note breaks the length or privacy rules, or there are more than five notes.</summary>
    public const string ProseRefused = "model_prose_refused";
    /// <summary>A family has no citation, more than six, a null key or quote, or a repeated evidence key.</summary>
    public const string CitationsMalformed = "citations_malformed";
    /// <summary>A subject-key citation is not an exact, meaningful, public quote of a read value that was sent.</summary>
    public const string SubjectCitationRefused = "subject_citation_refused";
    /// <summary>A supporting-key citation is not an exact public quote of a read value that was sent (the layer never is).</summary>
    public const string SupportingCitationRefused = "supporting_citation_refused";
    /// <summary>An image citation names no image of this request, or its description carries no visible subject.</summary>
    public const string ImageCitationRefused = "image_citation_refused";
    /// <summary>Every citation is valid but none quotes subject meaning: support alone never carries a family.</summary>
    public const string NoSubjectCitation = "no_subject_citation";
    /// <summary>An abstention without a reason.</summary>
    public const string ReasonMissing = "abstention_reason_missing";
    /// <summary>An abstention whose reason breaks the length or privacy rules; the reason is never shown.</summary>
    public const string ReasonRefused = "abstention_reason_refused";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ResponseMissing, ContextMismatch, RankingMalformed, FamilyNotCandidate, ProseRefused, CitationsMalformed,
        SubjectCitationRefused, SupportingCitationRefused, ImageCitationRefused, NoSubjectCitation, ReasonMissing, ReasonRefused,
    };
}

/// <summary>
/// Ranks library families for a measured group whose layer name carries no meaning, from the group's read CAD evidence
/// and the engineer's description. The output is a hypothesis for engineering review: origin "ai", status Proposed or
/// Abstained, score-free, never an approval, quantity or price. Candidate codes are the chosen family's library items
/// that exist in the active price list; the model never supplies a code. This class has no profile, file writer,
/// quantity calculator or price provider.
/// </summary>
public sealed class FamilyRecognitionAssist
{
    public const int MaxCandidates = 80;
    public const int MaxEngineerContextLength = 500;
    public const int MaxValuesPerKey = 8;
    public const int MaxValueChars = 1000;
    public const int MaxLayerChars = 250;
    public const int MaxQuoteChars = 200;
    public const int MaxExplanationChars = 700;
    public const int MaxMissingDetails = 5;
    public const int MaxMissingDetailChars = 200;
    public const int MaxAbstentionReasonChars = 240;
    public const string StatusRead = "read";
    public const string StatusIdentifier = "identifier";
    public const string LayerKey = "layer";
    public const string EngineerContextKey = "engineer_context";
    public const string ImageCitationPrefix = "image:";
    public const string ValueSeparator = " | ";
    public const string ReviewWarning = "השערת עוזר בלבד — לא אישור משפחה, סעיף, כמות או מחיר. יש לבדוק מול השרטוט לפני אישור הנדסי.";

    private const string NotEligible = "הקבוצה אינה מתאימה להצעת משפחה (אין בה רשומות, היחידה אינה ידועה או שהספרייה אינה תקינה). אפשר לבחור משפחה ידנית.";
    private const string NoCandidates = "אין בספרייה משפחה שבסיס המדידה שלה מתאים לקבוצה. אפשר לבחור ידנית.";
    private const string NoEvidence = "אין לקבוצה ראיה קריאה שמתארת מה היא (ערך של תכונת בלוק, רכיב, טקסט סמוך או שורת מקרא שהותאמה לפי תבנית, בלוק או סוג קו), ושם שכבה לבדו אינו מספיק — וגם לא שם בלוק, סוג קו, תבנית הצללה, שם תכונה או צבע. אפשר לתאר את הקבוצה במילים או לבחור משפחה ידנית.";
    private const string ContextRejected = "התיאור שהוזן ארוך מדי או כולל נתיב, קואורדינטות, מחיר או כמות, ולכן לא נשלח. יש לקצר ולנסח מחדש.";
    private const string VisionRefused = "התמונות לא נשלחו: אין הרשאה תקפה לתמונות האלה בדיוק, או שתמונה אינה PNG מותר. לא נשלח דבר; אפשר לבקש הצעה בלי תמונות.";
    private const string VisionPolicyOff = "התמונות לא נשלחו: שליחת תמונות לעוזר אינה מופעלת במדיניות הארגון במחשב הזה. לא נשלח דבר; אפשר לבקש הצעה בלי תמונות (לבטל את הסימון 'לצרף תמונת צורה').";
    private const string EvidenceWithheld = "יש לקבוצה ראיה שמתארת אותה, אבל היא נראתה כמידע פרטי (קואורדינטות, נתיב, מחיר או כמות) ולכן לא נשלחה. אפשר לתאר את הקבוצה במילים או לבחור משפחה ידנית.";
    private const string NoGroundedProposal = "לא התקבלה הצעת משפחה מבוססת ראיות. לא נשמר שינוי; אפשר לבחור משפחה ידנית.";
    private const string ProviderAbstainedPrefix = "ספק AI נמנע מהצעה: ";
    private const string ProviderAbstainedSuffix = ". לא נשמר שינוי; נדרשת ראיה נוספת או בדיקה הנדסית.";
    /// <summary>Internal result of <see cref="Rejection"/>: a bound abstention whose public reason may be shown. Not a rejection class.</summary>
    private const string BoundAbstention = "bound-abstention";
    private const string Changed = "הקבוצה, הספרייה או המחירון השתנו בזמן הבקשה, ולכן ההצעה לא הוצגה. לא נשמר שינוי; אפשר לבקש שוב.";
    private const string Cancelled = "בקשת ההצעה בוטלה. לא נשמר שינוי; הבחירה הידנית נשארת זמינה.";
    private const string TimedOut = "בקשת ההצעה לא הסתיימה בזמן. לא נשמר שינוי; אפשר לנסות שוב או לבחור ידנית.";
    private const string Unavailable = "שירות ההצעות אינו זמין כרגע. לא נשמר שינוי; אפשר להמשיך בבחירה ידנית.";

    /// <summary>
    /// CAD evidence that may be sent. Geometry samples and XREF transforms are never even copied here, so their
    /// coordinates, matrices and XREF/file names cannot reach a request.
    /// </summary>
    internal static readonly IReadOnlyList<string> SentEvidenceKeys = new[]
    {
        EvidenceKeys.BlockAttributes, EvidenceKeys.BlockProps, EvidenceKeys.PsetComponent, EvidenceKeys.Hatch,
        EvidenceKeys.LegendRow, EvidenceKeys.NearbyText, EvidenceKeys.ColorEffective, EvidenceKeys.Closed,
        EvidenceKeys.BlockNameEffective, EvidenceKeys.EntityLinetype, EvidenceKeys.LayerLinetype, EvidenceKeys.EntityColorIndex,
    };

    private static readonly IReadOnlyList<string> CapturedKeys = new[] { EvidenceKeys.Schema }.Concat(SentEvidenceKeys).ToArray();

    /// <summary>
    /// Evidence that can say what a group is. Structural facts (colour, closed), CAD identifiers (block name, line type)
    /// and the hatch pattern name (a supporting hint, as in LocalFamilyClassifier) may support a proposal but never
    /// admit a request or carry a proposal on their own. Within these keys only the value can say what the group is:
    /// an attribute tag or property name is an identifier, and a legend row counts only when matched by pattern, block
    /// or line type (a colour match is a supporting hint).
    /// </summary>
    internal static readonly IReadOnlySet<string> SubjectKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        EvidenceKeys.BlockAttributes, EvidenceKeys.BlockProps, EvidenceKeys.PsetComponent, EvidenceKeys.LegendRow,
        EvidenceKeys.NearbyText,
    };

    /// <summary>CAD symbol names whose XREF-dependent or bound-XREF prefix can encode a source file name.</summary>
    private static readonly IReadOnlySet<string> IdentifierKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        EvidenceKeys.BlockNameEffective, EvidenceKeys.EntityLinetype, EvidenceKeys.LayerLinetype,
    };

    private const int MaxLegendRows = 8;
    private static readonly Regex BoundXrefPrefix = new(@"\A.*\$\d+\$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>Upper bound of all evidence text in one request, by construction.</summary>
    public static int MaxEvidenceChars => SentEvidenceKeys.Count * MaxValueChars + MaxLayerChars + MaxEngineerContextLength;

    private readonly IFamilyRecognitionProvider _provider;
    private readonly TimeSpan _timeout;

    public FamilyRecognitionAssist(IFamilyRecognitionProvider provider) : this(provider, TimeSpan.FromSeconds(30)) { }
    internal FamilyRecognitionAssist(IFamilyRecognitionProvider provider, TimeSpan timeout)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
    }

    /// <summary>
    /// Asks the provider to rank families for <paramref name="group"/> only when the local classifier abstained, disagreed
    /// with itself or produced nothing for it; otherwise returns no proposal. A returned proposal covers every record of the
    /// group. Images are sent only with a <see cref="VisionSendPermit"/> for exactly their hashes and this request's context.
    /// </summary>
    public async Task<IReadOnlyList<RecognitionProposal>> AssistAsync(RecognitionGroupInput group, EngineerBoqLibrary library,
        CatalogSnapshot? catalog, IReadOnlyList<RecognitionProposal> localProposals, string? engineerContext,
        VisionPayload? vision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(localProposals);
        var groupId = group.GroupId ?? string.Empty;
        IReadOnlyList<string> recordIds = Array.Empty<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            if (!NeedsAssistance(groupId, localProposals)) return Array.Empty<RecognitionProposal>();
            // Copy before the first await; the provider never receives a mutable source object.
            var snapshot = GroupSnapshot.Capture(group);
            recordIds = snapshot.RecordIds;
            var catalogCopy = CatalogCopy.Capture(catalog);
            var images = vision?.Images?.ToArray() ?? Array.Empty<VisionImage>();
            var prepared = TryPrepare(snapshot, library, engineerContext, images, out var message);
            if (prepared == null) return Abstain(groupId, recordIds, message);
            var request = prepared.Request;
            if (images.Length > 0)
            {
                if (!VisionImagePolicy.IsPermitted(request, vision)) return Abstain(groupId, recordIds, VisionRefused);
                // Images off by organisation policy is a clear text-only path, not an unavailable service.
                if (_provider is IFamilyVisionGate gate && !VisionAllowed(gate)) return Abstain(groupId, recordIds, VisionPolicyOff);
                request = request with { Vision = new VisionPayload(Array.AsReadOnly(images), vision!.Permit) };
            }
            var response = await _provider.RankFamiliesAsync(request, budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            var rejection = Rejection(prepared, response);
            if (rejection == BoundAbstention)
                return Abstain(groupId, recordIds, ProviderAbstainedPrefix + response!.AbstentionReason!.Trim() + ProviderAbstainedSuffix);
            if (rejection != null) return Abstain(groupId, recordIds, NoGroundedProposal, rejection);
            // Never publish against a library, group or price list edited while the call ran.
            if (!string.Equals(LibraryIdentity.LibraryHash(library), request.LibraryHash, StringComparison.Ordinal) ||
                !string.Equals(GroupSnapshot.Capture(group).Digest, snapshot.Digest, StringComparison.Ordinal) ||
                !string.Equals(CatalogCopy.Capture(catalog).Digest, catalogCopy.Digest, StringComparison.Ordinal))
                return Abstain(groupId, recordIds, Changed);
            var proposal = Propose(prepared, response!, library, catalogCopy);
            if (response!.Ranked[0].Citations.Any(citation => ImageCitation(prepared, citation)))
                proposal = proposal with { VisualBinding = FamilyVisualBindingPolicy.Capture(group, request.Images) };
            return new[] { proposal };
        }
        catch (VisionNotPermittedException refused)
        {
            // The transport refused the images before any HTTP (e.g. the policy was switched off meanwhile): say so.
            return Abstain(groupId, recordIds, refused.OrganizationPolicyDisabled ? VisionPolicyOff : VisionRefused);
        }
        catch (OperationCanceledException)
        {
            return Abstain(groupId, recordIds, cancellationToken.IsCancellationRequested ? Cancelled : TimedOut);
        }
        catch (Exception)
        {
            // Provider/HTTP/parser exceptions may contain bodies or credentials: never surface them.
            return Abstain(groupId, recordIds, Unavailable);
        }
        finally { budget.Cancel(); }
    }

    private static bool VisionAllowed(IFamilyVisionGate gate)
    {
        try { return gate.IsVisionAllowed(); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The exact request that <see cref="AssistAsync"/> would send for these inputs (without images' bytes), or why it
    /// would abstain before calling the provider. Deterministic for equal inputs.
    /// </summary>
    public static FamilyRankPreparation Prepare(RecognitionGroupInput group, EngineerBoqLibrary library, string? engineerContext,
        IReadOnlyList<VisionImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(library);
        var prepared = TryPrepare(GroupSnapshot.Capture(group), library, engineerContext,
            images?.ToArray() ?? Array.Empty<VisionImage>(), out var message);
        return new FamilyRankPreparation(prepared?.Request, prepared == null ? message : null);
    }

    /// <summary>
    /// The assistant only ranks when the local classifier abstained, proposed more than one family, or produced nothing
    /// for the group. Proposals of other groups or of other origins do not count.
    /// </summary>
    public static bool NeedsAssistance(string groupId, IReadOnlyList<RecognitionProposal> localProposals)
    {
        ArgumentNullException.ThrowIfNull(localProposals);
        var local = localProposals.Where(proposal => proposal != null &&
            string.Equals(proposal.Origin, RecognitionProposal.OriginLocal, StringComparison.Ordinal) &&
            string.Equals(proposal.GroupId, groupId, StringComparison.Ordinal)).ToArray();
        if (local.Length == 0) return true;
        if (local.Any(proposal => proposal.Status != RecognitionStatus.Proposed || string.IsNullOrWhiteSpace(proposal.FamilyId))) return true;
        return local.Select(proposal => proposal.FamilyId!).Distinct(StringComparer.Ordinal).Count() > 1;
    }

    /// <summary>
    /// Mirrors EngineerBoqDraftBuilder.Accepts/BasisAccepts (private there; the builder is not edited here): a family is
    /// a candidate only when its quantity basis accepts the group's measurement kind and method class. Keep both in sync.
    /// </summary>
    internal static bool Accepts(DraftRule rule, string kind, string methodClass) =>
        (rule.Basis switch
        {
            DraftQuantityBasis.HatchArea => kind == "area" && methodClass == "hatch",
            DraftQuantityBasis.LengthWithClosedPerimeters => kind == "length" &&
                                                            (IsOpenLine(methodClass) || methodClass == "closed-perimeter"),
            DraftQuantityBasis.OpenLength => kind == "length" && IsOpenLine(methodClass),
            DraftQuantityBasis.Count => kind == "count",
            _ => false,
        }) || (rule.SplitByDrawnWidth && methodClass == "painted-width");

    private static bool IsOpenLine(string methodClass) => methodClass is "open" or "open-w10" or "open-w15" or "open-width-unproven";

    private static Prepared? TryPrepare(GroupSnapshot snapshot, EngineerBoqLibrary library, string? engineerContext,
        IReadOnlyList<VisionImage> images, out string message)
    {
        message = NotEligible;
        if (images.Count > VisionImagePolicy.MaxImages || images.Any(image => !VisionImagePolicy.IsValidImage(image)) ||
            images.Select(image => image.Sha256).Distinct(StringComparer.Ordinal).Count() != images.Count)
        {
            message = VisionRefused;
            return null;
        }
        if (engineerContext?.Length > MaxEngineerContextLength ||
            (!string.IsNullOrWhiteSpace(engineerContext) && !FamilyTextGuards.SafeText(engineerContext, MaxEngineerContextLength)))
        {
            message = ContextRejected;
            return null;
        }
        var unit = Units.Parse(snapshot.Unit);
        if (snapshot.Records.Count == 0 || unit.Canonical == "?") return null;
        var rules = library.Rules;
        if (string.IsNullOrWhiteSpace(library.Id) || rules == null || rules.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.Id)) ||
            rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count) return null;
        var libraryHash = LibraryIdentity.LibraryHash(library);
        var candidates = rules.Where(rule => Accepts(rule, snapshot.Kind, snapshot.MethodClass)).Take(MaxCandidates)
            .Select(rule => new FamilyRankCandidate(rule.Id, rule.Element, rule.Basis.ToString(), LibraryIdentity.RuleFingerprint(rule)))
            .ToArray();
        if (candidates.Length == 0)
        {
            message = NoCandidates;
            return null;
        }

        var total = snapshot.Records.Count;
        // A count group's number of records IS its measured quantity: only coarse shares of it are sent.
        var countGroup = snapshot.Kind == "count";
        int Wire(int part) => countGroup ? ShareBucket(part, total) : part;
        string CountText(int part) => countGroup
            ? ShareBucket(part, total).ToString(CultureInfo.InvariantCulture) + "%"
            : part.ToString(CultureInfo.InvariantCulture);
        var wireTotal = Wire(total);
        var evidence = new List<FamilyRankEvidence>();
        var withheld = new List<string>();
        var sent = new Dictionary<string, IReadOnlyList<AggregatedValue>>(StringComparer.Ordinal);
        // The layer name is context only: a random name must never be cited as the meaning of the group.
        if (!string.IsNullOrWhiteSpace(snapshot.LayerLeaf))
        {
            if (FamilyTextGuards.SafeText(snapshot.LayerLeaf, MaxLayerChars))
                evidence.Add(new FamilyRankEvidence(LayerKey, snapshot.LayerLeaf, StatusIdentifier, wireTotal, wireTotal));
            else withheld.Add(LayerKey);
        }
        foreach (var key in SentEvidenceKeys)
        {
            var aggregate = Aggregate(snapshot.Records, key, CountText);
            // A key can be partly withheld: only intact, public leaves enter both the wire value and citations.
            if (aggregate.Withheld) withheld.Add(key);
            if (aggregate.Values.Count > 0)
            {
                // Defence in depth over the final bounded value; the privacy rule itself is unchanged.
                var value = string.Join(ValueSeparator, aggregate.Values.Select(item => Piece(item, CountText)));
                if (aggregate.Values.Any(item => !FamilyTextGuards.SafeText(item.Leaf.Sent, MaxValueChars)) ||
                    !FamilyTextGuards.SafeText(value, MaxValueChars))
                {
                    if (!aggregate.Withheld) withheld.Add(key);
                    continue;
                }
                evidence.Add(new FamilyRankEvidence(key, value, StatusRead, Wire(aggregate.Usable), wireTotal));
                sent[key] = aggregate.Values;
            }
            else if (aggregate.Unusable != null)
                evidence.Add(new FamilyRankEvidence(key, string.Empty, aggregate.Unusable, 0, wireTotal));
        }
        if (!string.IsNullOrWhiteSpace(engineerContext))
        {
            var context = engineerContext.Trim();
            evidence.Add(new FamilyRankEvidence(EngineerContextKey, context, StatusRead, wireTotal, wireTotal));
            sent[EngineerContextKey] = new[] { new AggregatedValue(new Leaf(context, null, context, true), Array.Empty<string>()) };
        }
        // Admission: a meaningful read subject VALUE (never a tag, property name, colour-matched legend row, hatch
        // pattern, layer, block or line-type name), or the engineer's own meaningful description.
        var admissible = evidence.Any(item => item.Status == StatusRead && sent.TryGetValue(item.Key, out var values) &&
            values.Any(value => value.Leaf.Subject && FamilyTextGuards.Meaningful(value.Leaf.Quotable)));
        // A reviewed image may carry subject meaning even when CAD text cannot. This only prepares the
        // request: AssistAsync still requires the exact engineer permit and organisation-policy gate before
        // sending. A returned image hypothesis must cite that image's hash and remains unapproved.
        if (!admissible && images.Count == 0)
        {
            // Say so when describing evidence existed but was withheld as private (coordinates, paths, prices).
            message = withheld.Any(SubjectKeys.Contains) ? EvidenceWithheld : NoEvidence;
            return null;
        }

        var imageRefs = images.Select(image => new FamilyRankImage(image.Sha256, image.Kind)).ToArray();
        var contextId = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
        {
            Group = snapshot.Digest, LibraryId = library.Id, LibraryHash = libraryHash, snapshot.Kind, Unit = unit.Canonical,
            snapshot.MethodClass, Evidence = evidence, Withheld = withheld, Candidates = candidates, Images = imageRefs,
        }));
        var request = new FamilyRankRequest(contextId, library.Id, libraryHash, snapshot.Kind, unit.Canonical, snapshot.MethodClass,
            Array.AsReadOnly(evidence.ToArray()), Array.AsReadOnly(withheld.ToArray()), Array.AsReadOnly(candidates),
            Array.AsReadOnly(imageRefs));
        message = string.Empty;
        return new Prepared(request, sent, snapshot);
    }

    private static string Piece(AggregatedValue item, Func<int, string> countText) =>
        item.Leaf.Sent + " (" + countText(item.Records.Count) + ")";

    /// <summary>
    /// A coarse share of the group's records in percent (a multiple of 10): 0 only for none, 100 only for all. Used
    /// for count groups, whose record count is the measured quantity and is never sent.
    /// </summary>
    internal static int ShareBucket(int part, int total)
    {
        if (total <= 0 || part <= 0) return 0;
        if (part >= total) return 100;
        var bucket = (int)Math.Round(part * 10.0 / total, MidpointRounding.AwayFromZero) * 10;
        return Math.Clamp(bucket, 10, 90);
    }

    /// <summary>
    /// Top distinct text values of one key over the group, most frequent first (ties ordinal), bounded in count and
    /// length. Only usable (read/truncated) values of records under the agreed evidence schema take part; for other
    /// records only the state is counted, never the free-text reason. A private leaf is omitted intact BEFORE the
    /// top-value limit, not edited into a public-looking value. Coverage counts only records behind retained leaves.
    /// </summary>
    private static KeyAggregate Aggregate(IReadOnlyList<RecordSnapshot> records, string key, Func<int, string> countText)
    {
        var byText = new Dictionary<string, (Leaf Leaf, List<string> Ids)>(StringComparer.Ordinal);
        var unusable = new Dictionary<EvidenceState, int>();
        var withheld = false;
        foreach (var record in records)
        {
            var status = EvidenceReader.Status(record.Parameters, key);
            if (status.Usable && key.StartsWith("ev_", StringComparison.Ordinal) && !HasSchemaV1(record.Parameters))
                status = new EvidenceStatus(EvidenceState.Invalid, "schema", null);
            if (!status.Usable)
            {
                if (status.State != EvidenceState.Missing)
                    unusable[status.State] = unusable.TryGetValue(status.State, out var count) ? count + 1 : 1;
                continue;
            }
            var leaves = Leaves(record.Parameters, key);
            if (leaves.Count == 0) continue;
            foreach (var leaf in leaves)
            {
                if (!FamilyTextGuards.SafeText(leaf.Sent, MaxValueChars))
                {
                    withheld = true;
                    continue;
                }
                // Records are visited in RecordId order, so the first leaf kept for a sent text is deterministic.
                if (!byText.TryGetValue(leaf.Sent, out var entry)) byText[leaf.Sent] = entry = (leaf, new List<string>());
                entry.Ids.Add(record.RecordId);
            }
        }
        var values = new List<AggregatedValue>();
        var length = 0;
        foreach (var pair in byText.OrderByDescending(pair => pair.Value.Ids.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (values.Count >= MaxValuesPerKey) break;
            var item = new AggregatedValue(pair.Value.Leaf,
                pair.Value.Ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
            var added = Piece(item, countText).Length + (values.Count > 0 ? ValueSeparator.Length : 0);
            if (length + added > MaxValueChars) break;
            length += added;
            values.Add(item);
        }
        var state = unusable.Count == 0
            ? null
            : unusable.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .First().Key.ToString().ToLowerInvariant();
        var usable = values.SelectMany(value => value.Records).Distinct(StringComparer.Ordinal).Count();
        return new KeyAggregate(usable, values.AsReadOnly(), state, withheld);
    }

    /// <summary>
    /// Text leaves of one usable value. Attribute/property texts are sent as "TAG=value" (the tag is CAD data too, but
    /// only the value can be quoted as meaning). Legend rows are sent as "basis: text". CAD symbol names are reduced to
    /// their leaf so no XREF/source file name travels.
    /// </summary>
    private static IReadOnlyList<Leaf> Leaves(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (key == EvidenceKeys.LegendRow) return LegendLeaves(parameters);
        var subject = SubjectKeys.Contains(key);
        var identifier = IdentifierKeys.Contains(key);
        var leaves = new List<Leaf>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (identifier)
        {
            // A block or line-type name: the XREF prefix is cut from the raw value BEFORE any cleaning or truncation, so
            // a long XREF file name can never survive as the "leaf".
            if (EvidenceReader.Status(parameters, key).Usable && parameters.TryGetValue(key, out var raw) &&
                EvidenceReader.Clean(IdentifierLeaf(raw)) is { } name)
                leaves.Add(new Leaf(name, null, name, subject));
            return leaves;
        }
        foreach (var text in EvidenceReader.Texts(parameters, key))
        {
            var value = text.Text;
            if (string.IsNullOrWhiteSpace(value)) continue;
            var prefix = text.Tag == null ? null : text.Tag + "=";
            // A PropertySet yes/no says whether, not what: sent, never a subject.
            var leaf = new Leaf(prefix + value, prefix, value,
                subject && !(key == EvidenceKeys.PsetComponent && global::MahodAI.CivilDelivery.Estimate.Evidence.PropertySetEvidence.IsYesNo(value)));
            if (seen.Add(leaf.Sent)) leaves.Add(leaf);
        }
        return leaves;
    }

    /// <summary>
    /// Legend rows as LocalFamilyClassifier reads them: a row's text with its match basis. Only a row matched by
    /// pattern, block or line type can say what the group is; a colour (or unstated) match is a supporting hint.
    /// </summary>
    private static IReadOnlyList<Leaf> LegendLeaves(IReadOnlyDictionary<string, string> parameters)
    {
        if (!EvidenceReader.TryObservation(parameters, EvidenceKeys.LegendRow, out var legend)) return Array.Empty<Leaf>();
        var leaves = new List<Leaf>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ObservationItems(legend, "rows", "items").Take(MaxLegendRows))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("text", out var textElement) ||
                textElement.ValueKind != JsonValueKind.String || EvidenceReader.Clean(textElement.GetString()) is not { } text)
                continue;
            var match = row.TryGetProperty("match", out var matchElement) && matchElement.ValueKind == JsonValueKind.String
                ? matchElement.GetString()
                : null;
            // Only known basis tokens are sent; any other value becomes "unstated" and is never echoed.
            var basis = match switch
            {
                "pattern" => "pattern",
                "block" => "block",
                "linetype" => "linetype",
                "color" => "color",
                _ => "unstated",
            };
            var prefix = basis + ": ";
            var leaf = new Leaf(prefix + text, prefix, text, basis is "pattern" or "block" or "linetype");
            if (seen.Add(leaf.Sent)) leaves.Add(leaf);
        }
        return leaves;
    }

    private static List<JsonElement> ObservationItems(JsonElement observation, params string[] listNames)
    {
        if (observation.ValueKind == JsonValueKind.Array) return observation.EnumerateArray().ToList();
        if (observation.ValueKind != JsonValueKind.Object) return new List<JsonElement>();
        foreach (var name in listNames)
            if (observation.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner.EnumerateArray().ToList();
        return new List<JsonElement> { observation };
    }

    /// <summary>
    /// The leaf of a CAD symbol name. An XREF-dependent prefix (<c>FILE|NAME</c>, possibly nested) or a bound-XREF
    /// prefix (<c>FILE$0$NAME</c>) can encode a source file name, so only the part after it is kept, as the draft
    /// builder and the local classifier do with SectionProjectionLogic.LayerLeaf. Null when nothing is left.
    /// </summary>
    internal static string? IdentifierLeaf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = name.Trim();
        var bar = value.LastIndexOf('|');
        if (bar >= 0) value = value[(bar + 1)..];
        value = BoundXrefPrefix.Replace(value, string.Empty, 1).Trim();
        return value.Length == 0 ? null : value;
    }

    private static bool HasSchemaV1(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue(EvidenceKeys.Schema, out var schema) && schema != null &&
        string.Equals(schema.Trim().Trim('"'), EvidenceKeys.SchemaV1, StringComparison.Ordinal);

    /// <summary>Fixed public diagnostic code only; never returns evidence, response text or exception details.</summary>
    public static string PublicOutcomeCode(RecognitionProposal proposal)
    {
        if (proposal.Status == RecognitionStatus.Proposed) return "proposal_requires_review";
        if (proposal.Origin != RecognitionProposal.OriginAi || proposal.MissingDetails.Count != 1) return "no_grounded_family";
        var message = proposal.MissingDetails[0];
        return message switch {
            Unavailable => "assistant_unavailable", TimedOut => "assistant_timeout", Cancelled => "assistant_cancelled",
            Changed => "source_or_catalog_changed", VisionRefused => "image_permit_refused", VisionPolicyOff => "image_policy_disabled",
            NoEvidence => "subject_evidence_missing", EvidenceWithheld => "evidence_withheld_by_privacy",
            ContextRejected => "engineer_context_refused", NotEligible => "group_not_eligible", NoCandidates => "no_compatible_family",
            NoGroundedProposal => "response_not_grounded_or_unusable",
            _ when message.StartsWith(ProviderAbstainedPrefix, StringComparison.Ordinal) => "provider_abstained",
            _ => "no_grounded_family" };
    }

    /// <summary>
    /// The fixed class (<see cref="FamilyRankRejection"/>) of an assistant answer that was received but rejected, or null.
    /// Public diagnostic only, beside <see cref="PublicOutcomeCode"/>, which keeps response_not_grounded_or_unusable for
    /// every such answer (existing consumers). Never model text, evidence or response content.
    /// </summary>
    public static string? PublicRejectionCode(RecognitionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Origin == RecognitionProposal.OriginAi && proposal.Status == RecognitionStatus.Abstained &&
               proposal.AssistRejection is { } code && FamilyRankRejection.All.Contains(code) ? code : null;
    }

    /// <summary>
    /// All-or-nothing validation of a ranking against the exact request that was sent (mirrors
    /// SemanticMappingAssist.ValidateRanking): null when every ranked choice is valid; <see cref="BoundAbstention"/> for an
    /// abstention bound to this request whose reason is public; otherwise the first failing <see cref="FamilyRankRejection"/>
    /// class. The class is diagnostic only: which one is reported never changes what is accepted, and a foreign response,
    /// malformed choice list or private/unbounded text keeps the generic refusal message.
    /// </summary>
    private static string? Rejection(Prepared prepared, FamilyRankResponse? response)
    {
        var request = prepared.Request;
        if (response == null) return FamilyRankRejection.ResponseMissing;
        if (response.ContextId != request.ContextId || response.LibraryHash != request.LibraryHash) return FamilyRankRejection.ContextMismatch;
        if (response.Ranked is { Count: 0 })
        {
            var reason = response.AbstentionReason?.Trim();
            if (string.IsNullOrEmpty(reason)) return FamilyRankRejection.ReasonMissing;
            return reason.Any(char.IsControl) || !FamilyTextGuards.SafeText(reason, MaxAbstentionReasonChars)
                ? FamilyRankRejection.ReasonRefused
                : BoundAbstention;
        }
        if (!string.IsNullOrWhiteSpace(response.AbstentionReason) || response.Ranked == null || response.Ranked.Count > 3 ||
            response.Ranked.Any(choice => choice?.FamilyId == null) ||
            response.Ranked.Select(choice => choice.FamilyId).Distinct(StringComparer.Ordinal).Count() != response.Ranked.Count)
            return FamilyRankRejection.RankingMalformed;
        foreach (var choice in response.Ranked)
            if (ChoiceRejection(prepared, choice) is { } rejection) return rejection;
        return null;
    }

    private static string? ChoiceRejection(Prepared prepared, FamilyRankChoice choice)
    {
        if (!prepared.Request.Candidates.Any(candidate => string.Equals(candidate.FamilyId, choice.FamilyId, StringComparison.Ordinal)))
            return FamilyRankRejection.FamilyNotCandidate;
        if (!FamilyTextGuards.SafeText(choice.Explanation, MaxExplanationChars)) return FamilyRankRejection.ProseRefused;
        var citations = choice.Citations;
        if (citations == null || citations.Count is < 1 or > 6 ||
            citations.Any(citation => citation?.EvidenceKey == null || citation.Quote == null) ||
            citations.Select(citation => citation.EvidenceKey).Distinct(StringComparer.Ordinal).Count() != citations.Count)
            return FamilyRankRejection.CitationsMalformed;
        foreach (var citation in citations)
            if (CitationRejection(prepared, citation) is { } rejection) return rejection;
        // At least one citation must quote a VALUE that says what the group is; identifiers (tags, property names,
        // block/line-type names, hatch patterns), colour-matched legend rows and structural facts only support it.
        if (!citations.Any(citation => SubjectCitation(prepared, citation) || ImageCitation(prepared, citation)))
            return FamilyRankRejection.NoSubjectCitation;
        var missing = choice.MissingDetails ?? Array.Empty<string>();
        return missing.Count <= MaxMissingDetails && missing.All(detail => FamilyTextGuards.SafeText(detail, MaxMissingDetailChars))
            ? null
            : FamilyRankRejection.ProseRefused;
    }

    private static string? CitationRejection(Prepared prepared, FamilyRankCitation citation)
    {
        if (citation.EvidenceKey.StartsWith(ImageCitationPrefix, StringComparison.Ordinal))
            return ImageCitation(prepared, citation) ? null : FamilyRankRejection.ImageCitationRefused;
        var subject = IsSubjectKey(citation.EvidenceKey);
        if (ValidTextCitation(prepared, citation, subject)) return null;
        return subject ? FamilyRankRejection.SubjectCitationRefused : FamilyRankRejection.SupportingCitationRefused;
    }

    /// <summary>Keys whose value can say what a group is: the subject evidence keys and the engineer's own description.</summary>
    private static bool IsSubjectKey(string key) => key == EngineerContextKey || SubjectKeys.Contains(key);

    /// <summary>
    /// A text citation quotes a read value of this exact request within the quote length and privacy bounds. A subject key
    /// must quote meaning. A supporting key (block or line-type name, colour, colour index, hatch pattern, closed) may
    /// quote a code or number exactly as sent, such as 505(506) or 255,255,255; it only ever supports a subject citation.
    /// The layer (status "identifier") and keys that were not sent are never citable.
    /// </summary>
    private static bool ValidTextCitation(Prepared prepared, FamilyRankCitation citation, bool subject)
    {
        var sent = prepared.Request.Evidence.FirstOrDefault(item => string.Equals(item.Key, citation.EvidenceKey, StringComparison.Ordinal));
        var quote = citation.Quote;
        return sent != null && sent.Status == StatusRead &&
               quote.Length is >= 2 and <= MaxQuoteChars && FamilyTextGuards.SafeText(quote, MaxQuoteChars) &&
               prepared.Sent.TryGetValue(citation.EvidenceKey, out var values) && values.Any(value => Quotes(value.Leaf, quote, subject));
    }

    private static bool ImageCitation(Prepared prepared, FamilyRankCitation citation) =>
        citation.EvidenceKey.StartsWith(ImageCitationPrefix, StringComparison.Ordinal) &&
        prepared.Request.Images.Any(image => string.Equals(ImageCitationPrefix + image.Sha256, citation.EvidenceKey, StringComparison.Ordinal)) &&
        FamilyTextGuards.SafeText(citation.Quote, MaxQuoteChars) && FamilyTextGuards.Meaningful(citation.Quote);

    private static bool SubjectCitation(Prepared prepared, FamilyRankCitation citation) =>
        IsSubjectKey(citation.EvidenceKey) &&
        prepared.Sent.TryGetValue(citation.EvidenceKey, out var values) &&
        values.Any(value => value.Leaf.Subject && Quotes(value.Leaf, citation.Quote, requireMeaning: true));

    /// <summary>
    /// The quote is an exact substring of one sent text and lies in its quotable value: the quote may start with the exact
    /// tag/basis prefix, but a tag, property name or legend basis alone is never a quote. With <paramref name="requireMeaning"/>
    /// (subject keys) the quoted value must carry meaning; otherwise (supporting keys) it needs a letter or digit, so a code
    /// or number can support but punctuation alone cannot.
    /// </summary>
    private static bool Quotes(Leaf leaf, string quote, bool requireMeaning)
    {
        if (!leaf.Sent.Contains(quote, StringComparison.Ordinal)) return false;
        var effective = leaf.Prefix != null && quote.StartsWith(leaf.Prefix, StringComparison.Ordinal)
            ? quote[leaf.Prefix.Length..]
            : quote;
        return effective.Length > 0 && leaf.Quotable.Contains(effective, StringComparison.Ordinal) &&
               (requireMeaning ? FamilyTextGuards.Meaningful(effective) : effective.Any(char.IsLetterOrDigit));
    }

    private static RecognitionProposal Propose(Prepared prepared, FamilyRankResponse response, EngineerBoqLibrary library, CatalogCopy catalog)
    {
        var request = prepared.Request;
        var snapshot = prepared.Snapshot;
        var top = response.Ranked[0];
        var rule = library.Rules.First(candidate => string.Equals(candidate.Id, top.FamilyId, StringComparison.Ordinal));
        // Codes come from the library recipe of the chosen family, filtered by the active price list; never from the model.
        var codes = rule.Emits.Select(emit => emit.Code?.Trim() ?? string.Empty)
            .Where(code => code.Length > 0).Select(catalog.Resolve).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var refs = new List<RecognitionEvidenceRef>();
        var observed = new List<string>();
        var inferred = new List<string> { ReviewWarning, "השערת העוזר: " + top.Explanation.Trim() };
        foreach (var citation in top.Citations.OrderBy(citation => citation.EvidenceKey, StringComparer.Ordinal))
        {
            if (ImageCitation(prepared, citation))
            {
                var image = request.Images.Single(i => ImageCitationPrefix + i.Sha256 == citation.EvidenceKey);
                // Never fabricate an ev_* CAD citation or record-level semantic coverage from a picture.
                observed.Add("תמונה שצורפה לבקשה: " + image.Kind + " · SHA-256 " + image.Sha256);
                inferred.Add("השערה חזותית לא מאומתת: \"" + citation.Quote + "\" — שיוך התמונה לקבוצה ומשמעותה דורשים בדיקה.");
                continue;
            }
            if (citation.EvidenceKey == EngineerContextKey)
            {
                inferred.Add("לפי תיאור המהנדס: \"" + citation.Quote + "\"");
                continue;
            }
            var ids = prepared.Sent[citation.EvidenceKey]
                .Where(value => Quotes(value.Leaf, citation.Quote, IsSubjectKey(citation.EvidenceKey)))
                .SelectMany(value => value.Records).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            refs.Add(new RecognitionEvidenceRef(citation.EvidenceKey, ids));
            observed.Add(citation.EvidenceKey + ": \"" + citation.Quote + "\" — " + ids.Length.ToString(CultureInfo.InvariantCulture) +
                " מתוך " + snapshot.Records.Count.ToString(CultureInfo.InvariantCulture) + " רשומות");
        }
        inferred.Add("ai_context=" + request.ContextId);
        inferred.Add("library_hash=" + request.LibraryHash);
        inferred.Add("rule_fingerprint=" + request.Candidates.First(candidate => candidate.FamilyId == top.FamilyId).RuleFingerprint);
        var missing = (top.MissingDetails ?? Array.Empty<string>()).Select(detail => detail.Trim()).ToList();
        if (top.Citations.Any(citation => ImageCitation(prepared, citation)))
            missing.Add("הצעה חזותית בלבד: לבדוק את שיוך התמונה ואת משמעות העצמים מול השרטוט; אין כאן אישור משפחה, סעיף או מחיר.");
        if (request.WithheldKeys.Count > 0)
            missing.Add("ערכי ראיות הושמטו מהבקשה כי כללו נתיב, קואורדינטות, מחיר או כמות; המפתחות שבהם הושמטו ערכים: " + string.Join(", ", request.WithheldKeys) + " — אין להסיק מכך שכל ערכי המפתח נשלחו או שהראיה מכסה את כל הרשומות.");
        var alternatives = response.Ranked.Skip(1)
            .Select(choice => new RecognitionAlternative(choice.FamilyId, choice.Explanation.Trim())).ToArray();
        return new RecognitionProposal(snapshot.GroupId, snapshot.RecordIds, RecognitionStatus.Proposed, top.FamilyId,
            Array.AsReadOnly(codes), refs.AsReadOnly(), observed.AsReadOnly(), inferred.AsReadOnly(), Array.AsReadOnly(alternatives),
            missing.AsReadOnly(), RecognitionProposal.OriginAi);
    }

    private static IReadOnlyList<RecognitionProposal> Abstain(string groupId, IReadOnlyList<string> recordIds, string message,
        string? rejection = null) =>
        new[]
        {
            new RecognitionProposal(groupId, recordIds, RecognitionStatus.Abstained, null, Array.Empty<string>(),
                Array.Empty<RecognitionEvidenceRef>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<RecognitionAlternative>(), new[] { message }, RecognitionProposal.OriginAi) { AssistRejection = rejection },
        };

    private static IReadOnlyDictionary<string, string> CopyParameters(IReadOnlyDictionary<string, string>? parameters)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters == null) return copy;
        foreach (var key in CapturedKeys)
            foreach (var name in new[] { key, key + EvidenceKeys.StatusSuffix })
                if (parameters.TryGetValue(name, out var value) && value != null) copy[name] = value;
        return copy;
    }

    /// <summary>The request and, per sent key, the texts it carries with the records behind each.</summary>
    private sealed record Prepared(FamilyRankRequest Request, IReadOnlyDictionary<string, IReadOnlyList<AggregatedValue>> Sent,
        GroupSnapshot Snapshot);

    /// <summary>
    /// One distinct text of a key as sent: <see cref="Sent"/> = <see cref="Prefix"/> + <see cref="Quotable"/>. The prefix
    /// (an attribute tag or property name with "=", or a legend row's match basis) is an identifier: quotes must fall in
    /// <see cref="Quotable"/>. Only a <see cref="Subject"/> text can admit a request or carry a proposal.
    /// </summary>
    private sealed record Leaf(string Sent, string? Prefix, string Quotable, bool Subject);

    private sealed record AggregatedValue(Leaf Leaf, IReadOnlyList<string> Records);

    private sealed record KeyAggregate(int Usable, IReadOnlyList<AggregatedValue> Values, string? Unusable, bool Withheld);

    private sealed record RecordSnapshot(string RecordId, IReadOnlyDictionary<string, string> Parameters, string LocalFingerprint);

    // Local freshness only. None of these source paths, handles, coordinates, amounts or provenance fields
    // are copied into SentEvidenceKeys or FamilyRankRequest. Only the final opaque ContextId leaves Core.
    private static string LocalRecordFingerprint(NeutralQuantityRecord record, int inputOrder)
    {
        var parameters = record.Measurement?.Parameters;
        string? Value(string key) => parameters != null && parameters.TryGetValue(key, out var value) ? value : null;
        return ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
        {
            record.SchemaVersion, record.ProjectProfileId, record.RunId, record.Source, record.Provenance,
            // The preview renderer takes the first usable samples in input order, not RecordId order.
            InputOrder = inputOrder,
            Measurement = new { record.Measurement?.Kind, record.Measurement?.Method, record.Measurement?.Unit,
                record.Measurement?.RawValue, record.Measurement?.GeometryEvidence },
            Geometry = new[] { Value(EvidenceKeys.GeometrySample), Value(EvidenceKeys.GeometrySample + EvidenceKeys.StatusSuffix),
                Value(EvidenceKeys.XrefTransform), Value(EvidenceKeys.XrefTransform + EvidenceKeys.StatusSuffix) },
        }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    /// <summary>An immutable copy of the group fields and evidence parameters used here, with a digest of them.</summary>
    private sealed record GroupSnapshot(string GroupId, string LayerLeaf, string Kind, string Unit, string MethodClass,
        IReadOnlyList<RecordSnapshot> Records, IReadOnlyList<string> RecordIds, string Digest)
    {
        public static GroupSnapshot Capture(RecognitionGroupInput group)
        {
            var records = (group.Records ?? Array.Empty<NeutralQuantityRecord>()).Where(record => record != null)
                .Select((record, index) => new RecordSnapshot(record.RecordId ?? string.Empty,
                    CopyParameters(record.Measurement?.Parameters), LocalRecordFingerprint(record, index)))
                .OrderBy(record => record.RecordId, StringComparer.Ordinal).ToArray();
            // XREF and bound-XREF prefixes can encode source file names; only the layer leaf is kept.
            var layer = IdentifierLeaf(group.LayerLeaf) ?? string.Empty;
            var kind = (group.Kind ?? string.Empty).Trim().ToLowerInvariant();
            var unit = (group.Unit ?? string.Empty).Trim();
            var methodClass = (group.MethodClass ?? string.Empty).Trim();
            var digest = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
            {
                Group = group.GroupId ?? string.Empty, Source = group.Source ?? string.Empty, Role = group.SourceRole.ToString(),
                Layer = layer, Kind = kind, Unit = unit, MethodClass = methodClass, Block = group.Block ?? string.Empty,
                Records = records.Select(record => new
                {
                    record.RecordId,
                    record.LocalFingerprint,
                    Parameters = record.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new[] { pair.Key, pair.Value }).ToArray(),
                }).ToArray(),
            }));
            var ids = Array.AsReadOnly(records.Select(record => record.RecordId).Distinct(StringComparer.Ordinal).ToArray());
            return new GroupSnapshot(group.GroupId ?? string.Empty, layer, kind, unit, methodClass, Array.AsReadOnly(records), ids, digest);
        }
    }

    /// <summary>
    /// The price-list codes available for candidate codes and the approved edition links of the list, with a digest to
    /// detect an edit during the call.
    /// </summary>
    private sealed record CatalogCopy(IReadOnlySet<string> Codes, IReadOnlyDictionary<string, string> Aliases, string Digest)
    {
        public static CatalogCopy Capture(CatalogSnapshot? catalog)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(catalog.SnapshotId) || !CatalogIdentity.IsValidSha256(catalog.FileHash))
                return new CatalogCopy(new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "none");
            var codes = catalog.Items.Values.Where(item => item != null && !string.IsNullOrWhiteSpace(item.Code))
                .Select(item => item.Code.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var aliases = catalog.LibraryAliases.Where(pair => codes.Contains(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var digest = ArtifactHash.Sha256OfText(JsonSerializer.Serialize(new
            {
                catalog.SnapshotId, catalog.FileHash, Codes = codes.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
                Aliases = aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value).ToArray(),
            }));
            return new CatalogCopy(codes, aliases, digest);
        }

        /// <summary>A library code as listed, or its approved edition link's listed item, else null.</summary>
        public string? Resolve(string code) =>
            Codes.Contains(code) ? code : Aliases.TryGetValue(code, out var linked) ? linked : null;
    }
}

/// <summary>
/// SafeText, PrivateText and Meaningful started as verbatim copies of SemanticMappingAssist (private there), which stays
/// byte-identical for the tested code-ranking path. The family path is deliberately STRICTER: it sends drawing texts
/// (nearby texts, attribute values), so any number with six or more digits, or with five or more digits and a decimal
/// part, is private however it is labelled or split (<c>X=204539.12 Y=649342.40</c>, two separate grid labels), not only
/// the comma/space-separated pair. Keep the shared parts in sync with SemanticMappingAssist.
/// </summary>
internal static class FamilyTextGuards
{
    private static readonly Regex Words = new("[A-Za-zא-ת]+", RegexOptions.CultureInvariant);
    private static readonly Regex PrivateText = new(
        @"[A-Za-z]:[\\/]|\\\\|https?://|[/\\]|[\r\n\x00-\x08]|\b\d{5,}(?:\.\d+)?\s*[,; ]\s*\d{5,}|\d{6,}|\d{5,}[.,]\d|[₪$€]|\b(?:api[_ -]?key|token|password|price|quantity)\b|מחיר|כמות",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    { "layer", "block", "q", "unknown", "default", "bylayer", "byblock", "continuous", "unnamed", "none", "null" };

    internal static bool SafeText(string? text, int max) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= max && !PrivateText.IsMatch(text);

    internal static bool Meaningful(string? text) => text != null && Words.Matches(text).Select(match => match.Value)
        .Any(word => word.Length >= 3 && !GenericWords.Contains(word));
}
