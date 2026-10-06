using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// Which records carry an <c>ev_geometry_sample</c>: the first <see cref="PerGroup"/> USABLE samples of every
/// recognition group, keyed exactly like <see cref="EngineerBoqDraftBuilder.RecognitionGroups"/> (source, layer leaf,
/// kind, unit, method class before any drawn-width re-read and, for counts, the block leaf) — the groups the
/// engineer decides and the group preview is drawn for. A coarser key (drawing, layer, kind) let the first closed
/// polylines of a layer use up the budget of the hatches drawn after them, so that hatch group had no preview at
/// all. A sample that could not be read does not use up its group's budget. Pure and host-free.
/// </summary>
public sealed class GeometrySampleBudget
{
    public const int DefaultPerGroup = 3;

    private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);

    public GeometrySampleBudget(int perGroup = DefaultPerGroup)
    {
        if (perGroup < 1) throw new ArgumentOutOfRangeException(nameof(perGroup));
        PerGroup = perGroup;
    }

    public int PerGroup { get; }
    /// <summary>Usable samples written.</summary>
    public int Emitted { get; private set; }
    /// <summary>Recognition groups that received at least one usable sample.</summary>
    public int GroupsWithSample => _used.Count;

    /// <summary>The recognition-group id of <paramref name="record"/> (<see cref="EngineerBoqDraftBuilder.RecognitionGroupId"/>).</summary>
    public static string GroupKey(NeutralQuantityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var kind = (record.Measurement.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var unit = (record.Measurement.Unit ?? string.Empty).Trim();
        record.Measurement.Parameters.TryGetValue(EvidenceKeys.BlockNameEffective, out var rawBlock);
        var block = kind == "count" && !string.IsNullOrWhiteSpace(rawBlock) ? SectionProjectionLogic.LayerLeaf(rawBlock) : string.Empty;
        return EngineerBoqDraftBuilder.RecognitionGroupId(FamilyDecisionPolicy.NormalizeSource(record.Source.Xref),
            SectionProjectionLogic.LayerLeaf(record.Source.Layer), kind, unit,
            EngineerBoqDraftBuilder.MethodClass(kind, record.Measurement.Method), block);
    }

    /// <summary>
    /// When the record's group still has room, builds its sample and writes it (value and status) to the record.
    /// Returns true only when a usable sample was stored; only those count against the group. A full group
    /// leaves the record untouched (it keeps its <c>unavailable:sample-budget</c>).
    /// </summary>
    public bool Offer(NeutralQuantityRecord record, Func<EvidenceValue> sample)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(sample);
        var key = GroupKey(record);
        var used = _used.GetValueOrDefault(key);
        if (used >= PerGroup) return false;
        EvidenceValue value;
        try { value = sample(); }
        catch (Exception ex) { value = EvidenceValue.Unavailable(ex.GetType().Name); }
        EvidenceJson.Write(record.Measurement.Parameters, EvidenceKeys.GeometrySample, value);
        // Write may still refuse the value (e.g. value-too-large): only a stored, usable sample counts.
        if (!EvidenceReader.Status(record.Measurement.Parameters, EvidenceKeys.GeometrySample).Usable) return false;
        _used[key] = used + 1;
        Emitted++;
        return true;
    }
}
