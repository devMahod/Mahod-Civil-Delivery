using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate.Evidence;
using AecDataType = Autodesk.Aec.PropertyData.DataType;
using AecPropertyData = Autodesk.Aec.PropertyData.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Reads <c>ev_pset_component</c> for <see cref="CivilEvidenceCollector"/>: the AEC PropertySets attached directly to a
    /// measured entity (AecPropDataMgd), each manual text, integer, real or yes/no value as invariant text. Autodesk reads
    /// only: the sets and their definitions are opened ForRead through the scan's own transaction; nothing is set,
    /// synchronized, added or removed. Interpretation and the contract value live in Core (<see cref="PropertySetEvidence"/>).
    /// <list type="bullet">
    /// <item>Automatic, formula, field, auto/alpha-increment and linked (anchor, location, project, graphic, classification)
    /// properties restate other data — the layer, handle, style, another object or space — and are not read: they are never
    /// the object's own statement of what it is. A set that has one is partial (truncated), never complete.</item>
    /// <item>A set that cannot be opened, is not attached to this very object or is style-based, or a manual property that
    /// cannot be read, makes the value <c>unavailable:pset-read-incomplete</c>.</item>
    /// <item>Inside an INSERT or an XREF, AEC can keep an instance's data in the reference's override container (by block
    /// path), which this reader does not resolve: such an entity reports <c>unavailable:pset-reference-context-unread</c>
    /// rather than its definition's sets as if they were the whole statement.</item>
    /// <item>Every AEC type is used only inside methods compiled on first use: a host without the AEC PropertyData module
    /// (AecPropDataMgd missing, or its classes not registered) reports <c>unavailable:pset-module-…</c> for every record,
    /// never absent.</item>
    /// <item>Bounded: at most <see cref="MaxSetsRead"/> sets and <see cref="MaxPropertiesRead"/> properties per set are
    /// examined, at most <see cref="MaxScanPropertiesRead"/> properties over the scan, and the reads may take
    /// <see cref="TimeBudget"/> over the scan. A read stopped part-way by either scan bound (<see cref="ScanFailure"/>) is
    /// reported on every record by the collector's Complete, never on a subset that would depend on traversal order or
    /// timing.</item>
    /// </list>
    /// Exceptions of the whole read reach the collector's <c>Put</c>, which reports <c>unavailable:&lt;ExceptionType&gt;</c>.
    /// </summary>
    internal sealed class CivilPropertySetReader
    {
        internal const int MaxSetsRead = 64;
        internal const int MaxPropertiesRead = 256;
        internal const int MaxScanPropertiesRead = 250_000;
        internal const string ScanCapReason = "pset-scan-cap", TimeBudgetReason = "pset-time-budget";
        internal static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(10);

        private readonly Stopwatch _clock = new();
        private bool _moduleChecked;
        private int _scanProperties;

        /// <summary>Why the AEC PropertyData module could not be used in this scan; null while it could (or before first use).</summary>
        internal string? ModuleFailure { get; private set; }

        /// <summary>Why this scan's PropertySet reading stopped part-way (a scan bound); null while every read ran.</summary>
        internal string? ScanFailure { get; private set; }

        /// <param name="insideReference">The entity lies inside an ordinary INSERT or an XREF (its frame has an INSERT above it).</param>
        internal EvidenceValue Read(Entity ent, Transaction tr, bool insideReference)
        {
            if (insideReference) return EvidenceValue.Unavailable("pset-reference-context-unread");
            if (!_moduleChecked)
            {
                _moduleChecked = true;
                // A missing AecPropDataMgd surfaces here, when ModuleState is compiled (FileNotFoundException).
                try { ModuleFailure = ModuleState(); }
                catch (Exception ex) { ModuleFailure = "pset-module-" + ex.GetType().Name; }
            }
            if (ModuleFailure != null) return EvidenceValue.Unavailable(ModuleFailure);
            if (ScanFailure != null) return EvidenceValue.Unavailable(ScanFailure);
            if (_clock.Elapsed > TimeBudget)
            {
                ScanFailure = TimeBudgetReason;
                return EvidenceValue.Unavailable(ScanFailure);
            }
            _clock.Start();
            try
            {
                var value = ReadAttached(ent, tr, ref _scanProperties);
                if (_scanProperties > MaxScanPropertiesRead) ScanFailure = ScanCapReason;
                return value;
            }
            finally { _clock.Stop(); }
        }

        // Classes not registered in this session (the module is not loaded): an empty answer would claim "no sets".
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string? ModuleState() =>
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(AecPropertyData.PropertySet)) == null
                ? "pset-module-not-loaded"
                : null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static EvidenceValue ReadAttached(Entity ent, Transaction tr, ref int examinedInScan)
        {
            var ids = new List<ObjectId>();
            foreach (ObjectId id in AecPropertyData.PropertyDataServices.GetPropertySets(ent))
            {
                // Refused before any set is opened.
                if (ids.Count >= MaxSetsRead) return EvidenceValue.Unavailable("pset-sets-over-read-cap");
                ids.Add(id);
            }
            if (ids.Count == 0) return EvidenceValue.Absent;

            var owner = ent.ObjectId;
            var sets = new List<PropertySetEvidence.Set>(ids.Count);
            var unreadSets = 0;
            foreach (var id in ids)
            {
                try
                {
                    // Only an object-based set attached to this very object is its own statement.
                    if (tr.GetObject(id, OpenMode.ForRead) is not AecPropertyData.PropertySet propertySet ||
                        propertySet.ObjectAttachedTo != owner ||
                        tr.GetObject(propertySet.PropertySetDefinition, OpenMode.ForRead) is not AecPropertyData.PropertySetDefinition definition ||
                        definition.IsStyleBased)
                    {
                        unreadSets++;
                        continue;
                    }
                    var properties = new List<(string? Name, string? Value)>();
                    var unread = 0;
                    var derived = 0;
                    var examined = 0;
                    foreach (AecPropertyData.PropertyDefinition property in definition.Definitions)
                    {
                        if (++examined > MaxPropertiesRead) return EvidenceValue.Unavailable("pset-properties-over-read-cap");
                        if (++examinedInScan > MaxScanPropertiesRead) return EvidenceValue.Unavailable(ScanCapReason);
                        try
                        {
                            if (Derived(property))
                            {
                                derived++;
                                continue;
                            }
                            if (property.DataType is not (AecDataType.Text or AecDataType.Integer or AecDataType.Real or AecDataType.TrueFalse))
                            {
                                // A kind this reader does not know: what it says is unknown, not nothing.
                                unread++;
                                continue;
                            }
                            // A null value is skipped; a value of an unknown type is a read that failed.
                            if (PropertySetEvidence.TryValueText(propertySet.GetAt(property.Id), out var text))
                                properties.Add((property.Name, text));
                            else
                                unread++;
                        }
                        catch (Exception) { unread++; }
                    }
                    sets.Add(new PropertySetEvidence.Set(propertySet.PropertySetDefinitionName, properties, unread, derived));
                }
                catch (Exception) { unreadSets++; }
            }
            return PropertySetEvidence.Build(sets, unreadSets);
        }

        /// <summary>A property whose value is derived from other data rather than entered on the object.</summary>
        private static bool Derived(AecPropertyData.PropertyDefinition property) =>
            property.GetType() != typeof(AecPropertyData.PropertyDefinition) ||
            property.Automatic ||
            property.ContainsFields ||
            property.DataType is AecDataType.AutoIncrement or AecDataType.AlphaIncrement;
    }
}
