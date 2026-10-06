using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

public enum RecognitionStatus
{
    /// <summary>One family is proposed for the records. Still a proposal: nothing is approved or priced by it.</summary>
    Proposed,
    /// <summary>The evidence does not support one family. The engineer decides; the missing details say what is unknown.</summary>
    Abstained,
}

/// <summary>A cited evidence key and the records whose read-status value supports the proposal.</summary>
public sealed record RecognitionEvidenceRef(string Key, IReadOnlyList<string> RecordIds);

public sealed record RecognitionAlternative(string FamilyId, string Why);

/// <summary>
/// The recognition output for (a subset of) one measured group. A split group yields several proposals with the
/// same <see cref="GroupId"/> and disjoint <see cref="RecordIds"/>. There is no quantity, price or approval here:
/// <see cref="CandidateCodes"/> are the chosen family's library items that exist in the active price list.
/// <see cref="Observed"/> holds CAD facts; <see cref="Inferred"/> holds interpretations of them.
/// </summary>
public sealed record RecognitionProposal(
    string GroupId,
    IReadOnlyList<string> RecordIds,
    RecognitionStatus Status,
    string? FamilyId,
    IReadOnlyList<string> CandidateCodes,
    IReadOnlyList<RecognitionEvidenceRef> EvidenceRefs,
    IReadOnlyList<string> Observed,
    IReadOnlyList<string> Inferred,
    IReadOnlyList<RecognitionAlternative> Alternatives,
    IReadOnlyList<string> MissingDetails,
    string Origin)
{
    /// <summary>Exact visual content/scope reviewed for this hypothesis; not an approval or CAD evidence reference.</summary>
    public FamilyVisualBinding? VisualBinding { get; init; }
    public const string OriginLocal = "local";
    public const string OriginAi = "ai";

    /// <summary>Readable channels name different families.</summary>
    public const string AbstainConflict = "conflict";
    /// <summary>One text names more than one subject.</summary>
    public const string AbstainMixedSubjects = "mixed-subjects";

    /// <summary>
    /// Why a local proposal abstained (<see cref="AbstainConflict"/>, <see cref="AbstainMixedSubjects"/>, "ambiguous",
    /// "no-evidence", "basis-mismatch", "existing", "other-subject", "withheld", "refused"); null when proposed or when
    /// the classifier does not say. A machine-readable companion of <see cref="MissingDetails"/>, not a new decision.
    /// </summary>
    public string? AbstainKind { get; init; }

    /// <summary>
    /// For an assistant answer that was received but rejected: its fixed class (<see cref="FamilyRankRejection"/>); null
    /// otherwise. A public diagnostic only: never model text, never a decision, and it never makes a proposal.
    /// </summary>
    public string? AssistRejection { get; init; }

    /// <summary>The evidence contradicts itself: another CAD name on the object must not outvote it.</summary>
    public bool EvidenceContradicts => Status == RecognitionStatus.Abstained &&
        AbstainKind is AbstainConflict or AbstainMixedSubjects;
}

/// <summary>One measured group handed to a classifier: its identity, facts and records.</summary>
public sealed record RecognitionGroupInput(
    string GroupId,
    string Source,
    DraftSourceRole SourceRole,
    string LayerLeaf,
    string Kind,
    string Unit,
    string MethodClass,
    string? Block,
    IReadOnlyList<NeutralQuantityRecord> Records);

/// <summary>A family classifier. Deterministic, host-free and without network access.</summary>
public interface IFamilyClassifier
{
    /// <summary>Name/version/source identity reported with every prediction.</summary>
    string Identity { get; }

    /// <summary>Every input record appears in exactly one returned proposal.</summary>
    IReadOnlyList<RecognitionProposal> Classify(RecognitionGroupInput group, EngineerBoqLibrary library, CatalogSnapshot? catalog);
}

/// <summary>
/// Content identity of the active library. A family decision stores the <see cref="RuleFingerprint"/> of the rule it
/// approved; a rule whose meaning changed (patterns, basis, items, factors, parameters) no longer matches it.
/// Explanatory text (notes, display names, confidence labels) does not take part.
/// </summary>
public static class LibraryIdentity
{
    public static string RuleFingerprint(DraftRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var builder = new StringBuilder();
        void Field(string name, string? value) => builder.Append(name).Append('=').Append(value ?? "␀").Append('\u001e');
        Field("id", rule.Id);
        Field("element", rule.Element);
        Field("layers", string.Join('\u001f', rule.LayerPatterns));
        Field("basis", rule.Basis.ToString());
        Field("blocks", rule.BlockPatterns == null ? null : string.Join('\u001f', rule.BlockPatterns));
        Field("primary", rule.PrimarySourcePattern);
        Field("separate", rule.SeparateBoqRow ? "1" : "0");
        Field("included", rule.IncludedByDefault ? "1" : "0");
        Field("split-width", rule.SplitByDrawnWidth ? "1" : "0");
        Field("variant", rule.Variant);
        foreach (var emit in rule.Emits)
            Field("emit", string.Join('\u001f', emit.Code, emit.Factor.ToString("R", CultureInfo.InvariantCulture),
                emit.ParameterKey ?? string.Empty, emit.SecondParameterKey ?? string.Empty, emit.ComplementKey ?? string.Empty));
        return Sha(builder.ToString());
    }

    public static string LibraryHash(EngineerBoqLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var builder = new StringBuilder(library.Id).Append('\u001d');
        foreach (var rule in library.Rules) builder.Append(RuleFingerprint(rule)).Append('\u001e');
        foreach (var parameter in library.Parameters)
            builder.Append(string.Join('\u001f', parameter.Key, parameter.DefaultValue.ToString("R", CultureInfo.InvariantCulture),
                parameter.Min.ToString("R", CultureInfo.InvariantCulture), parameter.Max.ToString("R", CultureInfo.InvariantCulture))).Append('\u001e');
        return Sha(builder.ToString());
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
