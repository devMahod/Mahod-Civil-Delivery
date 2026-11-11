using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The estimate the BOQ panel shows (a marked area or the whole drawing) (owner 2026-11-11, after an "Inspector" reel: mark an area → one number and
/// what it is made of). The bill behind it is this window's — same items, prices, report or defaults — so "full details"
/// opens exactly what the card summed.
/// </summary>
internal sealed partial class InfrastructureBoqWindow
{
    internal sealed record EstimateLine(string Code, string Description, string Unit, double Quantity, decimal Total);

    internal sealed record AreaEstimate(decimal Total, decimal Demolition, IReadOnlyList<EstimateLine> Lines, int Entities,
        double AreaSquareMetres, int Unpriced, string Pavement, bool PavementIsDefault, string? ConcreteNote);

    /// <summary>
    /// Prices the area the way "auto-assign" would (every pay layer still without an item gets the recommended one, saved as
    /// an automatic choice), then sums it by price-list item. The engineer's own choices are never changed.
    /// </summary>
    /// <param name="wait">Waits for the background ranking while the BOQ panel stays alive (its Cancel throws).</param>
    internal AreaEstimate Estimate(Func<Task<InfrastructureAutoAssignmentBatch>, InfrastructureAutoAssignmentBatch> wait)
    {
        var pending = Active.Where(g => !g.Approved && double.IsFinite(g.Quantity) && g.Quantity > 0 && PayItem(g) &&
                                        g.Disposition != InfrastructureDisposition.ExistingKeep).ToArray();
        if (pending.Length > 0)
        {
            var search = _search;
            var batch = wait(Task.Run(() => search.PlanAutomaticAssignment(pending)));
            if (batch.Apply() > 0) { _lastAutomaticBatch = batch; Persist(); }
        }
        var pay = Active.Where(g => PayItem(g) && g.Disposition == InfrastructureDisposition.New).ToArray();
        var lines = pay.Where(g => g.Total is > 0 && g.Code != null)
            .GroupBy(g => g.Code!)
            .Select(x => new EstimateLine(x.Key, x.First().Description ?? x.Key, x.First().MeasurementUnit, x.Sum(g => g.Quantity), x.Sum(g => g.Total!.Value)))
            .OrderByDescending(l => l.Total).ToArray();
        var demolition = Active.Where(g => PayItem(g) && g.Disposition == InfrastructureDisposition.ExistingDemolish).Sum(g => g.Total ?? 0m);
        var metres = _scan.MetresPerHostUnit;
        var area = _scan.Area == null ? 0 : _scan.Area.Boundary.Area * metres * metres;
        var byDefault = _plan?.DefaultArea > 0 && _defaultClass != null;
        var pavement = _reports.HasReport
            ? "מבנה המיסעה לפי דו״ח " + string.Join(", ", _reports.Reports.Select(r => Path.GetFileNameWithoutExtension(r.FileName))) +
              (byDefault ? $" (והשאר לפי ברירת מחדל: {_defaultClass!.Name})" : "")
            : byDefault ? $"ללא דו״ח מבנה — מבנה המיסעה לפי ברירת מחדל: {_defaultClass!.Name}"
            : _derived.Count > 0 ? "מבנה המיסעה כפי שהוזן ידנית" : "ללא מבנה מיסעה — האספלט והמצעים לא תומחרו";
        var concrete = _concretePlan.Defaults > 0 ? $"בטון: {_concretePlan.Defaults} אלמנטים לפי מידות טיפוסיות" : null;
        return new AreaEstimate(lines.Sum(l => l.Total), demolition, lines, _scan.Measurements.Count, area,
            pay.Count(g => g.Total == null && g.Quantity > 0), pavement, byDefault || (!_reports.HasReport && _derived.Count == 0), concrete);
    }
}
