using System.Collections.Generic;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq
{
    /// <summary>
    /// Natali 30.09.2026 ("נתאגר אותו עוד קצת עם הקובץ CIVIL — צריכה כמויות של חפירה מילוי אספלטים מצעים"): the tool
    /// measures a corridor drawing itself. The plugin only READS Civil (applied assemblies, their 'Bot' links and shapes, the
    /// existing-ground surface on each section line) into these records; every calculation — cut / fill between the design
    /// bottom and the ground (the method of MahodCivilNet CalcSectionVolumes3, which produced the 'Road Volumes (Bot - MK)'
    /// tables in her file), layer volumes and plan areas, coverage — is Core, unit-tested without Civil.
    /// All values are metres (the reader converts drawing units); elevations are absolute.
    /// </summary>
    public sealed record CorridorLink(double X1, double Z1, double X2, double Z2);

    /// <summary>A shape of one corridor code at one station: Civil's area and its links' end points (offset, elevation).</summary>
    public sealed record CorridorShapeSample(string Code, double Area, IReadOnlyList<CorridorLink> Links);

    /// <summary>One applied assembly. Status "ok" | "mk_partial" are measured; any other status (with Reason) is a read
    /// failure that must never be integrated as zero or bridged.</summary>
    public sealed record CorridorStationSample(
        string Corridor, int Baseline, int Region, double Station, string Status, string? Reason,
        IReadOnlyList<CorridorLink> BottomLinks,
        IReadOnlyList<IReadOnlyList<(double X, double Z)>> GroundPieces,
        IReadOnlyList<CorridorShapeSample> Shapes);

    /// <summary>The shapes of one applied assembly, keyed like the cut/fill kernel (CorridorBotSurfaceLogic.RegionKey parts).
    /// A failed read carries ReadFailure and no shapes; it is never integrated as zero.</summary>
    public sealed record CorridorShapeStation(string CorridorId, string BaselineId, string RegionId, double StationM,
        bool ReadSucceeded, string? ReadFailure, IReadOnlyList<CorridorShapeSample> Shapes);

    /// <summary>A baseline region as Civil declares it, with the applied-assembly count read before any de-duplication.</summary>
    public sealed record CorridorRegionSample(string Corridor, int Baseline, int Region, double Start, double End, int AppliedAssemblies);

    public sealed record CorridorMeasurementInput(
        string DrawingPath, string DrawingHash, string SurfaceName, string LinkCode, string RunId,
        IReadOnlyList<CorridorRegionSample> Regions, IReadOnlyList<CorridorStationSample> Stations)
    {
        /// <summary>Corridors skipped by the reader (out of date, unreadable) with the reason; they are reported, never priced.</summary>
        public IReadOnlyList<(string Corridor, string Reason)> SkippedCorridors { get; init; } = new List<(string, string)>();
        /// <summary>Tables of the MahodCivilNet CalcVolumes tool found in the drawing (layer CalcVolumes2_Table), for comparison only.</summary>
        public IReadOnlyList<CorridorVolumeTable> DrawingTables { get; init; } = new List<CorridorVolumeTable>();
    }

    /// <summary>One row of a 'Road Volumes' table in the drawing: header → value, as printed.</summary>
    public sealed record CorridorVolumeTable(string Handle, string Title, string Road, IReadOnlyDictionary<string, double> Values);

    /// <summary>Per corridor totals (average end area between two consecutive measured stations of one region).</summary>
    public sealed record CorridorTotals(
        string Corridor, bool Complete, int Stations, int FailedStations, int PartialStations, IReadOnlyList<string> CoverageIssues,
        double Length, double PlanAreaCut, double PlanAreaFill, double PlanArea3DCut, double PlanArea3DFill,
        double VolumeCut, double VolumeFill,
        IReadOnlyDictionary<string, double> CodeVolumes, IReadOnlyDictionary<string, double> CodePlanAreas,
        IReadOnlyList<string> LayerOrderIssues);
}
