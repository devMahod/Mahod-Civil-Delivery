namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

/// <summary>A batch only fills unmapped rows. Undo preserves any later engineer decisions.</summary>
public sealed class InfrastructureAutoAssignmentBatch
{
    internal sealed record Proposal(InfrastructureGroup Group, string Code, string Topic, string Unit, double Quantity);
    private sealed record Applied(InfrastructureGroup Group, string Code, long Revision);
    private readonly CatalogSnapshot _catalog;
    private readonly IReadOnlyList<Proposal> _proposals;
    private readonly List<Applied> _applied = new();
    private bool _wasApplied;
    internal InfrastructureAutoAssignmentBatch(CatalogSnapshot catalog, IReadOnlyList<Proposal> proposals,
        int existing, int unmeasured, int unmatched)
    { _catalog = catalog; _proposals = proposals; ExistingCount = existing; UnmeasuredCount = unmeasured; UnmatchedCount = unmatched; }

    public int RecommendedCount => _proposals.Count;
    public int ExistingCount { get; }
    public int UnmeasuredCount { get; }
    public int UnmatchedCount { get; }
    public int AssignedCount => _applied.Count;

    public int Apply()
    {
        if (_wasApplied) return 0;
        _wasApplied = true;
        foreach (var p in _proposals)
        {
            var g = p.Group;
            if (g.Approved || g.Topic != p.Topic || g.MeasurementUnit != p.Unit || g.Quantity != p.Quantity) continue;
            g.Assign(_catalog, p.Code, automatic: true);
            _applied.Add(new(g, p.Code, g.AssignmentRevision));
        }
        return _applied.Count;
    }

    public int Undo()
    {
        var count = 0;
        foreach (var a in _applied)
        {
            var g = a.Group;
            if (!g.AutomaticallyAssigned || g.Code != a.Code || g.CatalogHash != _catalog.FileHash || g.AssignmentRevision != a.Revision) continue;
            g.Withdraw(); count++;
        }
        _applied.Clear();
        return count;
    }
}
