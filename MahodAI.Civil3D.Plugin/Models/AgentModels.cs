using System;
using System.Text.Json.Serialization;

namespace MahodAI.Civil3D.Plugin
{
    #region Request/Response Models

    public class SessionCreateRequest
    {
        [JsonPropertyName("drawing_id")]
        public string DrawingId { get; set; } = "";

        [JsonPropertyName("drawing_name")]
        public string DrawingName { get; set; } = "";

        [JsonPropertyName("summary")]
        public ApiSummary Summary { get; set; } = new();
    }

    public class SessionResponse
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class MessageRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }

        [JsonPropertyName("tool_params")]
        public object? ToolParams { get; set; }

        [JsonPropertyName("details")]
        public object? Details { get; set; }
    }

    public class MessageResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("detail_requests")]
        public System.Collections.Generic.List<DetailRequest>? DetailRequests { get; set; }

        [JsonPropertyName("tool_calls")]
        public System.Collections.Generic.List<ToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("analysis")]
        public object? Analysis { get; set; }
    }

    public class DetailRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = "";

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }

    public class ToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("arguments")]
        public object? Arguments { get; set; }
    }

    #endregion

    #region API Summary Models

    public class ApiSummary
    {
        [JsonPropertyName("metadata")]
        public ApiMetadata Metadata { get; set; } = new();

        [JsonPropertyName("coordinate_system")]
        public ApiCoordinateSystem? CoordinateSystem { get; set; }

        [JsonPropertyName("statistics")]
        public ApiStatistics Statistics { get; set; } = new();

        [JsonPropertyName("quality_issues")]
        public System.Collections.Generic.List<ApiQualityIssue>? QualityIssues { get; set; }

        [JsonPropertyName("surfaces")]
        public System.Collections.Generic.List<ApiSurfaceDetailed>? Surfaces { get; set; }

        [JsonPropertyName("alignments")]
        public System.Collections.Generic.List<ApiAlignmentDetailed>? AlignmentsDetailed { get; set; }

        [JsonPropertyName("profiles")]
        public System.Collections.Generic.List<ApiProfileDetailed>? Profiles { get; set; }

        [JsonPropertyName("corridors")]
        public System.Collections.Generic.List<ApiCorridor>? Corridors { get; set; }

        [JsonPropertyName("pipe_networks")]
        public System.Collections.Generic.List<ApiPipeNetwork>? PipeNetworks { get; set; }

        [JsonPropertyName("points")]
        public ApiPointsCollection? Points { get; set; }

        [JsonPropertyName("feature_lines")]
        public System.Collections.Generic.List<ApiFeatureLine>? FeatureLines { get; set; }

        [JsonPropertyName("parcels")]
        public System.Collections.Generic.List<ApiParcel>? Parcels { get; set; }

        [JsonPropertyName("layers")]
        public System.Collections.Generic.List<ApiLayerDetailed>? Layers { get; set; }

        [JsonPropertyName("layer_statistics")]
        public ApiLayerStatistics? LayerStatistics { get; set; }

        [JsonPropertyName("xrefs")]
        public System.Collections.Generic.List<ApiXrefDetailed>? XrefsDetailed { get; set; }

        [JsonPropertyName("blocks")]
        public System.Collections.Generic.List<ApiBlockDetailed>? Blocks { get; set; }

        // Legacy fields for backward compatibility
        [JsonPropertyName("geometry")]
        public ApiGeometry? Geometry { get; set; }

        [JsonPropertyName("extents")]
        public ApiExtents? Extents { get; set; }

        [JsonPropertyName("civil")]
        public ApiCivil? Civil { get; set; }

        [JsonPropertyName("warnings")]
        public System.Collections.Generic.List<string>? Warnings { get; set; }

        [JsonPropertyName("missing_xrefs")]
        public System.Collections.Generic.List<string>? MissingXrefs { get; set; }

        [JsonPropertyName("checks")]
        public System.Collections.Generic.List<ApiRuleCheck>? Checks { get; set; }

        [JsonPropertyName("alignments_basic")]
        public System.Collections.Generic.List<ApiAlignment>? Alignments { get; set; }

        [JsonPropertyName("profile_checks")]
        public System.Collections.Generic.List<ApiProfileCheck>? ProfileChecks { get; set; }

        [JsonPropertyName("xrefs_basic")]
        public System.Collections.Generic.List<ApiXref>? Xrefs { get; set; }

        [JsonPropertyName("curve_statistics")]
        public ApiCurveStatistics? CurveStatistics { get; set; }

        [JsonPropertyName("landxml_alignments")]
        public System.Collections.Generic.List<ApiLandXmlAlignment>? LandXmlAlignments { get; set; }
    }

    public class ApiMetadata
    {
        [JsonPropertyName("drawing_id")]
        public string DrawingId { get; set; } = "";

        [JsonPropertyName("drawing_name")]
        public string DrawingName { get; set; } = "unknown.dwg";

        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("file_size_mb")]
        public double? FileSizeMb { get; set; }

        [JsonPropertyName("dwg_version")]
        public string? DwgVersion { get; set; }

        [JsonPropertyName("created_date")]
        public string? CreatedDate { get; set; }

        [JsonPropertyName("modified_date")]
        public string? ModifiedDate { get; set; }

        [JsonPropertyName("last_saved_by")]
        public string? LastSavedBy { get; set; }

        [JsonPropertyName("units")]
        public string Units { get; set; } = "Meters";

        [JsonPropertyName("units_detail")]
        public ApiUnits? UnitsDetail { get; set; }

        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1.0;
    }

    public class ApiUnits
    {
        [JsonPropertyName("linear")]
        public string Linear { get; set; } = "Meters";

        [JsonPropertyName("angular")]
        public string Angular { get; set; } = "Degrees";

        [JsonPropertyName("area")]
        public string Area { get; set; } = "SquareMeters";

        [JsonPropertyName("volume")]
        public string Volume { get; set; } = "CubicMeters";
    }

    public class ApiCoordinateSystem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("epsg_code")]
        public int? EpsgCode { get; set; }

        [JsonPropertyName("projection")]
        public string? Projection { get; set; }

        [JsonPropertyName("datum")]
        public string? Datum { get; set; }

        [JsonPropertyName("zone")]
        public string? Zone { get; set; }

        [JsonPropertyName("units")]
        public string Units { get; set; } = "Meters";

        [JsonPropertyName("bounds")]
        public ApiBounds? Bounds { get; set; }
    }

    public class ApiBounds
    {
        [JsonPropertyName("min_x")]
        public double MinX { get; set; }

        [JsonPropertyName("min_y")]
        public double MinY { get; set; }

        [JsonPropertyName("max_x")]
        public double MaxX { get; set; }

        [JsonPropertyName("max_y")]
        public double MaxY { get; set; }
    }

    public class ApiStatistics
    {
        [JsonPropertyName("total_entities")]
        public int TotalEntities { get; set; }

        [JsonPropertyName("total_points")]
        public int TotalPoints { get; set; }

        [JsonPropertyName("total_triangles")]
        public int TotalTriangles { get; set; }

        [JsonPropertyName("z_min")]
        public double? ZMin { get; set; }

        [JsonPropertyName("z_max")]
        public double? ZMax { get; set; }

        [JsonPropertyName("by_entity_type")]
        public System.Collections.Generic.Dictionary<string, int>? ByEntityType { get; set; }

        [JsonPropertyName("layer_count")]
        public int LayerCount { get; set; }

        [JsonPropertyName("block_count")]
        public int BlockCount { get; set; }

        [JsonPropertyName("civil_object_count")]
        public int CivilObjectCount { get; set; }

        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("total_area")]
        public double TotalArea { get; set; }

        [JsonPropertyName("proxy_object_count")]
        public int ProxyObjectCount { get; set; }

        [JsonPropertyName("reg_app_count")]
        public int RegAppCount { get; set; }

        [JsonPropertyName("empty_layer_count")]
        public int EmptyLayerCount { get; set; }

        [JsonPropertyName("active_layer_count")]
        public int ActiveLayerCount { get; set; }
    }

    public class ApiGeometry
    {
        [JsonPropertyName("max_z")]
        public double MaxZ { get; set; }

        [JsonPropertyName("z_anomaly_count")]
        public int ZAnomalyCount { get; set; }

        [JsonPropertyName("has_very_high_z")]
        public bool HasVeryHighZ { get; set; }

        [JsonPropertyName("has_negative_z")]
        public bool HasNegativeZ { get; set; }
    }

    public class ApiExtents
    {
        [JsonPropertyName("min_x")]
        public double MinX { get; set; }

        [JsonPropertyName("min_y")]
        public double MinY { get; set; }

        [JsonPropertyName("max_x")]
        public double MaxX { get; set; }

        [JsonPropertyName("max_y")]
        public double MaxY { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }
    }

    public class ApiCivil
    {
        [JsonPropertyName("alignment_count")]
        public int AlignmentCount { get; set; }

        [JsonPropertyName("surface_count")]
        public int SurfaceCount { get; set; }

        [JsonPropertyName("surface_names")]
        public System.Collections.Generic.List<string>? SurfaceNames { get; set; }

        [JsonPropertyName("surfaces")]
        public System.Collections.Generic.List<ApiSurfaceInfo>? Surfaces { get; set; }

        [JsonPropertyName("corridor_count")]
        public int CorridorCount { get; set; }

        [JsonPropertyName("pipe_network_count")]
        public int PipeNetworkCount { get; set; }

        [JsonPropertyName("parcel_count")]
        public int ParcelCount { get; set; }

        [JsonPropertyName("total_alignment_length")]
        public double TotalAlignmentLength { get; set; }
    }

    public class ApiSurfaceInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("surface_type")]
        public string SurfaceType { get; set; } = "";

        [JsonPropertyName("min_elevation")]
        public double MinElevation { get; set; }

        [JsonPropertyName("max_elevation")]
        public double MaxElevation { get; set; }

        [JsonPropertyName("area")]
        public double Area { get; set; }

        [JsonPropertyName("point_count")]
        public int PointCount { get; set; }

        [JsonPropertyName("triangle_count")]
        public int TriangleCount { get; set; }

        [JsonPropertyName("is_empty")]
        public bool IsEmpty { get; set; }

        [JsonPropertyName("is_built")]
        public bool IsBuilt { get; set; }

        [JsonPropertyName("boundary_count")]
        public int BoundaryCount { get; set; }

        [JsonPropertyName("breakline_count")]
        public int BreaklineCount { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    public class ApiRuleCheck
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }

    public class ApiAlignment
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("min_radius")]
        public double MinRadius { get; set; }

        [JsonPropertyName("max_radius")]
        public double? MaxRadius { get; set; }

        [JsonPropertyName("design_speed_kph")]
        public double? DesignSpeedKph { get; set; }

        [JsonPropertyName("speed_segments")]
        public System.Collections.Generic.List<ApiSpeedSegment>? SpeedSegments { get; set; }

        [JsonPropertyName("tangent_count")]
        public int TangentCount { get; set; }

        [JsonPropertyName("curve_count")]
        public int CurveCount { get; set; }

        [JsonPropertyName("spiral_count")]
        public int SpiralCount { get; set; }

        [JsonPropertyName("has_superelevation")]
        public bool HasSuperelevation { get; set; }

        [JsonPropertyName("profile_names")]
        public System.Collections.Generic.List<string> ProfileNames { get; set; } = new();

        [JsonPropertyName("curves")]
        public System.Collections.Generic.List<ApiCurveInfo> Curves { get; set; } = new();

        [JsonPropertyName("superelevation")]
        public System.Collections.Generic.List<ApiSuperelevationInfo> Superelevation { get; set; } = new();

        [JsonPropertyName("road_type_hint")]
        public string? RoadTypeHint { get; set; }

        [JsonPropertyName("flagged_curves")]
        public System.Collections.Generic.List<ApiFlaggedCurve>? FlaggedCurves { get; set; }
    }

    public class ApiFlaggedCurve
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("required_radius")]
        public double RequiredRadius { get; set; }

        [JsonPropertyName("speed_kph")]
        public double SpeedKph { get; set; }
    }

    public class ApiFlaggedGrade
    {
        [JsonPropertyName("from_station")]
        public double FromStation { get; set; }

        [JsonPropertyName("to_station")]
        public double ToStation { get; set; }

        [JsonPropertyName("grade_percent")]
        public double GradePercent { get; set; }

        [JsonPropertyName("max_allowed_grade")]
        public double MaxAllowedGrade { get; set; }
    }

    public class ApiSpeedSegment
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("speed_kph")]
        public double SpeedKph { get; set; }
    }

    public class ApiCurveInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "";

        [JsonPropertyName("spiral_in_length")]
        public double? SpiralInLength { get; set; }

        [JsonPropertyName("spiral_out_length")]
        public double? SpiralOutLength { get; set; }
    }

    public class ApiSuperelevationInfo
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("left_slope_percent")]
        public double LeftSlopePercent { get; set; }

        [JsonPropertyName("right_slope_percent")]
        public double RightSlopePercent { get; set; }

        [JsonPropertyName("pivot_method")]
        public string PivotMethod { get; set; } = "";
    }

    public class ApiProfileCheck
    {
        [JsonPropertyName("alignment_name")]
        public string AlignmentName { get; set; } = "";

        [JsonPropertyName("profile_name")]
        public string ProfileName { get; set; } = "";

        [JsonPropertyName("profile_type")]
        public string ProfileType { get; set; } = "";

        [JsonPropertyName("min_grade_percent")]
        public double MinGradePercent { get; set; }

        [JsonPropertyName("max_grade_percent")]
        public double MaxGradePercent { get; set; }
    }

    public class ApiXref
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("is_resolved")]
        public bool IsResolved { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    public class ApiCurveStatistics
    {
        [JsonPropertyName("total_curve_count")]
        public int TotalCurveCount { get; set; }

        [JsonPropertyName("total_spiral_count")]
        public int TotalSpiralCount { get; set; }

        [JsonPropertyName("total_arc_count")]
        public int TotalArcCount { get; set; }

        [JsonPropertyName("total_scs_count")]
        public int TotalScsCount { get; set; }

        [JsonPropertyName("min_curve_radius")]
        public double MinCurveRadius { get; set; }

        [JsonPropertyName("max_curve_radius")]
        public double MaxCurveRadius { get; set; }

        [JsonPropertyName("total_curve_length")]
        public double TotalCurveLength { get; set; }

        [JsonPropertyName("min_spiral_length")]
        public double MinSpiralLength { get; set; }

        [JsonPropertyName("max_spiral_length")]
        public double MaxSpiralLength { get; set; }

        [JsonPropertyName("total_spiral_length")]
        public double TotalSpiralLength { get; set; }

        [JsonPropertyName("tangent_count")]
        public int TangentCount { get; set; }

        [JsonPropertyName("total_tangent_length")]
        public double TotalTangentLength { get; set; }
    }

    public class ApiLandXmlAlignment
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("design_speed")]
        public double? DesignSpeed { get; set; }

        [JsonPropertyName("design_standard")]
        public string? DesignStandard { get; set; }

        [JsonPropertyName("min_radius")]
        public double MinRadius { get; set; }

        [JsonPropertyName("horizontal_curves")]
        public System.Collections.Generic.List<ApiHorizontalCurve>? HorizontalCurves { get; set; }

        [JsonPropertyName("vertical_curves")]
        public System.Collections.Generic.List<ApiVerticalCurve>? VerticalCurves { get; set; }

        [JsonPropertyName("tangents")]
        public System.Collections.Generic.List<ApiTangent>? Tangents { get; set; }

        [JsonPropertyName("statistics")]
        public ApiCurveStatistics? Statistics { get; set; }
    }

    public class ApiHorizontalCurve
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }

        [JsonPropertyName("curve_type")]
        public string CurveType { get; set; } = "";

        [JsonPropertyName("spiral_type")]
        public string? SpiralType { get; set; }

        [JsonPropertyName("spiral_in_length")]
        public double? SpiralInLength { get; set; }

        [JsonPropertyName("spiral_out_length")]
        public double? SpiralOutLength { get; set; }

        [JsonPropertyName("arc_length")]
        public double? ArcLength { get; set; }
    }

    public class ApiVerticalCurve
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("grade_in")]
        public double GradeIn { get; set; }

        [JsonPropertyName("grade_out")]
        public double GradeOut { get; set; }

        [JsonPropertyName("curve_type")]
        public string? CurveType { get; set; }

        [JsonPropertyName("radius")]
        public double Radius { get; set; }
    }

    public class ApiTangent
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("bearing")]
        public double? Bearing { get; set; }
    }

    #endregion

    #region Detailed Models

    public class ApiQualityIssue
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("location")]
        public ApiIssueLocation? Location { get; set; }

        [JsonPropertyName("actual_value")]
        public object? ActualValue { get; set; }

        [JsonPropertyName("expected_value")]
        public object? ExpectedValue { get; set; }

        [JsonPropertyName("suggested_fix")]
        public ApiSuggestedFix? SuggestedFix { get; set; }
    }

    public class ApiIssueLocation
    {
        [JsonPropertyName("entity_type")]
        public string? EntityType { get; set; }

        [JsonPropertyName("entity_name")]
        public string? EntityName { get; set; }

        [JsonPropertyName("station")]
        public double? Station { get; set; }

        [JsonPropertyName("coordinates")]
        public double[]? Coordinates { get; set; }
    }

    public class ApiSuggestedFix
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("can_auto_fix")]
        public bool CanAutoFix { get; set; }

        [JsonPropertyName("command")]
        public string? Command { get; set; }
    }

    public class ApiSurfaceDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "TIN";

        [JsonPropertyName("purpose")]
        public string? Purpose { get; set; }

        [JsonPropertyName("point_count")]
        public int PointCount { get; set; }

        [JsonPropertyName("triangle_count")]
        public int TriangleCount { get; set; }

        [JsonPropertyName("elevation_min")]
        public double? ElevationMin { get; set; }

        [JsonPropertyName("elevation_max")]
        public double? ElevationMax { get; set; }

        [JsonPropertyName("slope_min_percent")]
        public double? SlopeMinPercent { get; set; }

        [JsonPropertyName("slope_max_percent")]
        public double? SlopeMaxPercent { get; set; }

        [JsonPropertyName("slope_mean_percent")]
        public double? SlopeMeanPercent { get; set; }

        [JsonPropertyName("statistics")]
        public ApiSurfaceStatistics? Statistics { get; set; }

        [JsonPropertyName("slope_analysis")]
        public ApiSlopeAnalysis? SlopeAnalysis { get; set; }

        [JsonPropertyName("boundaries")]
        public System.Collections.Generic.List<ApiBoundary>? Boundaries { get; set; }

        [JsonPropertyName("breaklines")]
        public ApiBreaklines? Breaklines { get; set; }

        [JsonPropertyName("contours")]
        public ApiContours? Contours { get; set; }

        [JsonPropertyName("issues")]
        public System.Collections.Generic.List<ApiSurfaceIssue>? Issues { get; set; }
    }

    public class ApiSurfaceStatistics
    {
        [JsonPropertyName("min_elevation")]
        public double MinElevation { get; set; }

        [JsonPropertyName("max_elevation")]
        public double MaxElevation { get; set; }

        [JsonPropertyName("mean_elevation")]
        public double? MeanElevation { get; set; }

        [JsonPropertyName("area_2d")]
        public double? Area2d { get; set; }

        [JsonPropertyName("area_3d")]
        public double? Area3d { get; set; }

        [JsonPropertyName("point_count")]
        public int PointCount { get; set; }

        [JsonPropertyName("triangle_count")]
        public int TriangleCount { get; set; }
    }

    public class ApiSlopeAnalysis
    {
        [JsonPropertyName("min_slope_percent")]
        public double? MinSlopePercent { get; set; }

        [JsonPropertyName("max_slope_percent")]
        public double? MaxSlopePercent { get; set; }

        [JsonPropertyName("mean_slope_percent")]
        public double? MeanSlopePercent { get; set; }

        [JsonPropertyName("slope_ranges")]
        public System.Collections.Generic.List<ApiSlopeRange>? SlopeRanges { get; set; }
    }

    public class ApiSlopeRange
    {
        [JsonPropertyName("range")]
        public string Range { get; set; } = "";

        [JsonPropertyName("area_percent")]
        public double AreaPercent { get; set; }
    }

    public class ApiBoundary
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "Outer";

        [JsonPropertyName("vertex_count")]
        public int VertexCount { get; set; }
    }

    public class ApiBreaklines
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("types")]
        public System.Collections.Generic.List<string>? Types { get; set; }
    }

    public class ApiContours
    {
        [JsonPropertyName("minor_interval")]
        public double? MinorInterval { get; set; }

        [JsonPropertyName("major_interval")]
        public double? MajorInterval { get; set; }
    }

    public class ApiSurfaceIssue
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("location")]
        public double[]? Location { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Warning";
    }

    public class ApiAlignmentDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Centerline";

        [JsonPropertyName("design_criteria")]
        public ApiDesignCriteria? DesignCriteria { get; set; }

        [JsonPropertyName("geometry")]
        public ApiAlignmentGeometry? Geometry { get; set; }

        [JsonPropertyName("elements")]
        public System.Collections.Generic.List<ApiAlignmentElement>? Elements { get; set; }

        [JsonPropertyName("superelevation")]
        public System.Collections.Generic.List<ApiSuperelevation>? Superelevation { get; set; }

        [JsonPropertyName("statistics")]
        public ApiAlignmentStatistics? Statistics { get; set; }

        [JsonPropertyName("design_violations")]
        public System.Collections.Generic.List<ApiDesignViolation>? DesignViolations { get; set; }

        [JsonPropertyName("has_profile")]
        public bool HasProfile { get; set; }

        [JsonPropertyName("profile_names")]
        public System.Collections.Generic.List<string>? ProfileNames { get; set; }
    }

    public class ApiDesignCriteria
    {
        [JsonPropertyName("design_speed_kph")]
        public double? DesignSpeedKph { get; set; }

        [JsonPropertyName("design_standard")]
        public string? DesignStandard { get; set; }

        [JsonPropertyName("road_type")]
        public string? RoadType { get; set; }

        [JsonPropertyName("terrain_type")]
        public string? TerrainType { get; set; }
    }

    public class ApiAlignmentGeometry
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("start_point")]
        public double[]? StartPoint { get; set; }

        [JsonPropertyName("end_point")]
        public double[]? EndPoint { get; set; }
    }

    public class ApiAlignmentElement
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        // Line specific
        [JsonPropertyName("bearing")]
        public double? Bearing { get; set; }

        [JsonPropertyName("start_point")]
        public double[]? StartPoint { get; set; }

        [JsonPropertyName("end_point")]
        public double[]? EndPoint { get; set; }

        // Arc specific
        [JsonPropertyName("radius")]
        public double? Radius { get; set; }

        [JsonPropertyName("delta_angle")]
        public double? DeltaAngle { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }

        [JsonPropertyName("center_point")]
        public double[]? CenterPoint { get; set; }

        [JsonPropertyName("chord_length")]
        public double? ChordLength { get; set; }

        [JsonPropertyName("tangent_length")]
        public double? TangentLength { get; set; }

        [JsonPropertyName("external_distance")]
        public double? ExternalDistance { get; set; }

        [JsonPropertyName("middle_ordinate")]
        public double? MiddleOrdinate { get; set; }

        [JsonPropertyName("pc_point")]
        public double[]? PcPoint { get; set; }

        [JsonPropertyName("pt_point")]
        public double[]? PtPoint { get; set; }

        // Spiral specific
        [JsonPropertyName("spiral_type")]
        public string? SpiralType { get; set; }

        [JsonPropertyName("a_value")]
        public double? AValue { get; set; }

        [JsonPropertyName("start_radius")]
        public double? StartRadius { get; set; }

        [JsonPropertyName("end_radius")]
        public double? EndRadius { get; set; }

        [JsonPropertyName("theta")]
        public double? Theta { get; set; }

        // SCS specific
        [JsonPropertyName("entry_spiral")]
        public ApiSpiralDetails? EntrySpiral { get; set; }

        [JsonPropertyName("central_arc")]
        public ApiArcDetails? CentralArc { get; set; }

        [JsonPropertyName("exit_spiral")]
        public ApiSpiralDetails? ExitSpiral { get; set; }
    }

    public class ApiSpiralDetails
    {
        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("a_value")]
        public double? AValue { get; set; }

        [JsonPropertyName("start_radius")]
        public double? StartRadius { get; set; }

        [JsonPropertyName("end_radius")]
        public double? EndRadius { get; set; }
    }

    public class ApiArcDetails
    {
        [JsonPropertyName("radius")]
        public double Radius { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("delta_angle")]
        public double? DeltaAngle { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }
    }

    public class ApiSuperelevation
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("left_slope_percent")]
        public double LeftSlopePercent { get; set; }

        [JsonPropertyName("right_slope_percent")]
        public double RightSlopePercent { get; set; }

        [JsonPropertyName("pivot_method")]
        public string? PivotMethod { get; set; }

        [JsonPropertyName("transition_type")]
        public string? TransitionType { get; set; }

        [JsonPropertyName("critical_station_type")]
        public string? CriticalStationType { get; set; }
    }

    public class ApiAlignmentStatistics
    {
        [JsonPropertyName("tangent_count")]
        public int TangentCount { get; set; }

        [JsonPropertyName("total_tangent_length")]
        public double TotalTangentLength { get; set; }

        [JsonPropertyName("curve_count")]
        public int CurveCount { get; set; }

        [JsonPropertyName("arc_count")]
        public int ArcCount { get; set; }

        [JsonPropertyName("spiral_count")]
        public int SpiralCount { get; set; }

        [JsonPropertyName("scs_count")]
        public int ScsCount { get; set; }

        [JsonPropertyName("min_radius")]
        public double? MinRadius { get; set; }

        [JsonPropertyName("max_radius")]
        public double? MaxRadius { get; set; }

        [JsonPropertyName("total_curve_length")]
        public double TotalCurveLength { get; set; }

        [JsonPropertyName("min_spiral_length")]
        public double? MinSpiralLength { get; set; }

        [JsonPropertyName("max_spiral_length")]
        public double? MaxSpiralLength { get; set; }
    }

    public class ApiDesignViolation
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("actual_value")]
        public double ActualValue { get; set; }

        [JsonPropertyName("required_value")]
        public double RequiredValue { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "Warning";
    }

    public class ApiProfileDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("alignment_name")]
        public string AlignmentName { get; set; } = "";

        [JsonPropertyName("profile_type")]
        public string Type { get; set; } = "design";

        [JsonPropertyName("pvi_count")]
        public int PviCount { get; set; }

        [JsonPropertyName("elevation_min")]
        public double? ElevationMin { get; set; }

        [JsonPropertyName("elevation_max")]
        public double? ElevationMax { get; set; }

        [JsonPropertyName("min_grade_percent")]
        public double? MinGradePercent { get; set; }

        [JsonPropertyName("max_grade_percent")]
        public double? MaxGradePercent { get; set; }

        [JsonPropertyName("vertical_curve_count")]
        public int VerticalCurveCount { get; set; }

        [JsonPropertyName("min_k_value")]
        public double? MinKValue { get; set; }

        [JsonPropertyName("max_k_value")]
        public double? MaxKValue { get; set; }

        [JsonPropertyName("geometry")]
        public ApiProfileGeometry? Geometry { get; set; }

        [JsonPropertyName("pvi_points")]
        public System.Collections.Generic.List<ApiPviPoint> PviPoints { get; set; } = new();

        [JsonPropertyName("vertical_curves")]
        public System.Collections.Generic.List<ApiVerticalCurveInfo> VerticalCurves { get; set; } = new();

        [JsonPropertyName("elements")]
        public System.Collections.Generic.List<ApiProfileElement>? Elements { get; set; }

        [JsonPropertyName("statistics")]
        public ApiProfileStatistics? Statistics { get; set; }

        [JsonPropertyName("design_violations")]
        public System.Collections.Generic.List<ApiDesignViolation>? DesignViolations { get; set; }

        [JsonPropertyName("flagged_grades")]
        public System.Collections.Generic.List<ApiFlaggedGrade>? FlaggedGrades { get; set; }
    }

    public class ApiVerticalCurveInfo
    {
        [JsonPropertyName("pvi_station")]
        public double PviStation { get; set; }

        [JsonPropertyName("pvi_elevation")]
        public double PviElevation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("k_value")]
        public double KValue { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("grade_in_percent")]
        public double GradeInPercent { get; set; }

        [JsonPropertyName("grade_out_percent")]
        public double GradeOutPercent { get; set; }
    }

    public class ApiProfileGeometry
    {
        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("start_elevation")]
        public double StartElevation { get; set; }

        [JsonPropertyName("end_elevation")]
        public double EndElevation { get; set; }

        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("total_elevation_change")]
        public double TotalElevationChange { get; set; }
    }

    public class ApiPviPoint
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("elevation")]
        public double Elevation { get; set; }

        [JsonPropertyName("grade_in_percent")]
        public double? GradeInPercent { get; set; }

        [JsonPropertyName("grade_out_percent")]
        public double? GradeOutPercent { get; set; }
    }

    public class ApiProfileElement
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        // Tangent specific
        [JsonPropertyName("grade_percent")]
        public double? GradePercent { get; set; }

        [JsonPropertyName("start_elevation")]
        public double? StartElevation { get; set; }

        [JsonPropertyName("end_elevation")]
        public double? EndElevation { get; set; }

        // Parabolic curve specific
        [JsonPropertyName("pvi_station")]
        public double? PviStation { get; set; }

        [JsonPropertyName("pvi_elevation")]
        public double? PviElevation { get; set; }

        [JsonPropertyName("grade_in_percent")]
        public double? GradeInPercent { get; set; }

        [JsonPropertyName("grade_out_percent")]
        public double? GradeOutPercent { get; set; }

        [JsonPropertyName("curve_type")]
        public string? CurveType { get; set; }

        [JsonPropertyName("k_value")]
        public double? KValue { get; set; }

        [JsonPropertyName("a_value")]
        public double? AValue { get; set; }

        [JsonPropertyName("high_low_point_station")]
        public double? HighLowPointStation { get; set; }

        [JsonPropertyName("high_low_point_elevation")]
        public double? HighLowPointElevation { get; set; }
    }

    public class ApiProfileStatistics
    {
        [JsonPropertyName("min_grade_percent")]
        public double MinGradePercent { get; set; }

        [JsonPropertyName("max_grade_percent")]
        public double MaxGradePercent { get; set; }

        [JsonPropertyName("min_elevation")]
        public double MinElevation { get; set; }

        [JsonPropertyName("max_elevation")]
        public double MaxElevation { get; set; }

        [JsonPropertyName("tangent_count")]
        public int TangentCount { get; set; }

        [JsonPropertyName("vertical_curve_count")]
        public int VerticalCurveCount { get; set; }

        [JsonPropertyName("crest_curve_count")]
        public int CrestCurveCount { get; set; }

        [JsonPropertyName("sag_curve_count")]
        public int SagCurveCount { get; set; }

        [JsonPropertyName("min_k_value")]
        public double? MinKValue { get; set; }

        [JsonPropertyName("max_k_value")]
        public double? MaxKValue { get; set; }
    }

    public class ApiCorridor
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("corridor_type")]
        public string CorridorType { get; set; } = "Road";

        [JsonPropertyName("total_length")]
        public double TotalLength { get; set; }

        [JsonPropertyName("baseline_count")]
        public int BaselineCount { get; set; }

        [JsonPropertyName("region_count")]
        public int RegionCount { get; set; }

        [JsonPropertyName("surface_count")]
        public int SurfaceCount { get; set; }

        [JsonPropertyName("alignment_names")]
        public System.Collections.Generic.List<string> AlignmentNames { get; set; } = new();

        [JsonPropertyName("assemblies_used")]
        public System.Collections.Generic.List<string> AssembliesUsed { get; set; } = new();

        [JsonPropertyName("baseline_alignment")]
        public string? BaselineAlignment { get; set; }

        [JsonPropertyName("baseline_profile")]
        public string? BaselineProfile { get; set; }

        [JsonPropertyName("regions")]
        public System.Collections.Generic.List<ApiCorridorRegion>? Regions { get; set; }

        [JsonPropertyName("assemblies")]
        public System.Collections.Generic.List<ApiAssembly>? Assemblies { get; set; }

        [JsonPropertyName("sample_cross_sections")]
        public System.Collections.Generic.List<ApiCrossSection>? SampleCrossSections { get; set; }

        [JsonPropertyName("surfaces_created")]
        public System.Collections.Generic.List<string>? SurfacesCreated { get; set; }

        [JsonPropertyName("volumes")]
        public ApiCorridorVolumes? Volumes { get; set; }

        [JsonPropertyName("lane_widths")]
        public System.Collections.Generic.List<ApiLaneWidth>? LaneWidths { get; set; }

        [JsonPropertyName("max_left_width")]
        public double? MaxLeftWidth { get; set; }

        [JsonPropertyName("max_right_width")]
        public double? MaxRightWidth { get; set; }
    }

    public class ApiCorridorRegion
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("start_station")]
        public double StartStation { get; set; }

        [JsonPropertyName("end_station")]
        public double EndStation { get; set; }

        [JsonPropertyName("assembly_name")]
        public string? AssemblyName { get; set; }

        [JsonPropertyName("frequency")]
        public double? Frequency { get; set; }
    }

    public class ApiAssembly
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("subassemblies")]
        public System.Collections.Generic.List<ApiSubassembly>? Subassemblies { get; set; }
    }

    public class ApiSubassembly
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("side")]
        public string? Side { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    public class ApiCrossSection
    {
        [JsonPropertyName("station")]
        public double Station { get; set; }

        [JsonPropertyName("left_offset_max")]
        public double? LeftOffsetMax { get; set; }

        [JsonPropertyName("right_offset_max")]
        public double? RightOffsetMax { get; set; }

        [JsonPropertyName("links")]
        public System.Collections.Generic.List<ApiCrossSectionLink>? Links { get; set; }

        [JsonPropertyName("daylight")]
        public ApiDaylightInfo? Daylight { get; set; }
    }

    public class ApiCrossSectionLink
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("start_offset")]
        public double StartOffset { get; set; }

        [JsonPropertyName("end_offset")]
        public double EndOffset { get; set; }

        [JsonPropertyName("start_elevation")]
        public double StartElevation { get; set; }

        [JsonPropertyName("end_elevation")]
        public double EndElevation { get; set; }

        [JsonPropertyName("slope_percent")]
        public double? SlopePercent { get; set; }
    }

    public class ApiDaylightInfo
    {
        [JsonPropertyName("left_offset")]
        public double? LeftOffset { get; set; }

        [JsonPropertyName("right_offset")]
        public double? RightOffset { get; set; }

        [JsonPropertyName("left_type")]
        public string? LeftType { get; set; }

        [JsonPropertyName("right_type")]
        public string? RightType { get; set; }
    }

    public class ApiCorridorVolumes
    {
        [JsonPropertyName("cut_volume")]
        public double CutVolume { get; set; }

        [JsonPropertyName("fill_volume")]
        public double FillVolume { get; set; }

        [JsonPropertyName("net_volume")]
        public double NetVolume { get; set; }
    }

    public class ApiPipeNetwork
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("network_type")]
        public string? NetworkType { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("structure_count")]
        public int StructureCount { get; set; }

        [JsonPropertyName("pipe_count")]
        public int PipeCount { get; set; }

        [JsonPropertyName("total_pipe_length")]
        public double TotalPipeLength { get; set; }

        [JsonPropertyName("parts_list")]
        public string? PartsList { get; set; }

        [JsonPropertyName("structures")]
        public System.Collections.Generic.List<ApiPipeStructure>? Structures { get; set; }

        [JsonPropertyName("pipes")]
        public System.Collections.Generic.List<ApiPipe>? Pipes { get; set; }

        [JsonPropertyName("catchments")]
        public System.Collections.Generic.List<ApiCatchment>? Catchments { get; set; }

        [JsonPropertyName("analysis_results")]
        public ApiPipeAnalysisResults? AnalysisResults { get; set; }
    }

    public class ApiPipeStructure
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("location")]
        public double[]? Location { get; set; }

        [JsonPropertyName("rim_elevation")]
        public double? RimElevation { get; set; }

        [JsonPropertyName("sump_elevation")]
        public double? SumpElevation { get; set; }

        [JsonPropertyName("diameter")]
        public double? Diameter { get; set; }

        [JsonPropertyName("connected_pipes")]
        public System.Collections.Generic.List<string>? ConnectedPipes { get; set; }
    }

    public class ApiPipe
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("start_structure")]
        public string? StartStructure { get; set; }

        [JsonPropertyName("end_structure")]
        public string? EndStructure { get; set; }

        [JsonPropertyName("shape")]
        public string? Shape { get; set; }

        [JsonPropertyName("material")]
        public string? Material { get; set; }

        [JsonPropertyName("inner_diameter")]
        public double? InnerDiameter { get; set; }

        [JsonPropertyName("wall_thickness")]
        public double? WallThickness { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("start_invert")]
        public double? StartInvert { get; set; }

        [JsonPropertyName("end_invert")]
        public double? EndInvert { get; set; }

        [JsonPropertyName("slope_percent")]
        public double? SlopePercent { get; set; }

        [JsonPropertyName("manning_coefficient")]
        public double? ManningCoefficient { get; set; }

        [JsonPropertyName("cover_start")]
        public double? CoverStart { get; set; }

        [JsonPropertyName("cover_end")]
        public double? CoverEnd { get; set; }

        [JsonPropertyName("flow_direction")]
        public string? FlowDirection { get; set; }

        [JsonPropertyName("design_flow")]
        public double? DesignFlow { get; set; }

        [JsonPropertyName("capacity")]
        public double? Capacity { get; set; }

        [JsonPropertyName("velocity")]
        public double? Velocity { get; set; }
    }

    public class ApiCatchment
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("outlet_structure")]
        public string? OutletStructure { get; set; }

        [JsonPropertyName("area")]
        public double? Area { get; set; }

        [JsonPropertyName("runoff_coefficient")]
        public double? RunoffCoefficient { get; set; }

        [JsonPropertyName("time_of_concentration")]
        public double? TimeOfConcentration { get; set; }

        [JsonPropertyName("flow_path_length")]
        public double? FlowPathLength { get; set; }

        [JsonPropertyName("flow_path_slope")]
        public double? FlowPathSlope { get; set; }
    }

    public class ApiPipeAnalysisResults
    {
        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("design_storm")]
        public string? DesignStorm { get; set; }

        [JsonPropertyName("peak_flow_total")]
        public double? PeakFlowTotal { get; set; }

        [JsonPropertyName("flooding_structures")]
        public System.Collections.Generic.List<string>? FloodingStructures { get; set; }

        [JsonPropertyName("surcharge_pipes")]
        public System.Collections.Generic.List<string>? SurchargePipes { get; set; }
    }

    public class ApiPointsCollection
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("groups")]
        public System.Collections.Generic.List<ApiPointGroup>? Groups { get; set; }

        [JsonPropertyName("sample_points")]
        public System.Collections.Generic.List<ApiPoint>? SamplePoints { get; set; }

        [JsonPropertyName("statistics")]
        public ApiPointStatistics? Statistics { get; set; }
    }

    public class ApiPointGroup
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("point_count")]
        public int PointCount { get; set; }

        [JsonPropertyName("elevation_range")]
        public ApiElevationRange? ElevationRange { get; set; }
    }

    public class ApiElevationRange
    {
        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; }
    }

    public class ApiPoint
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("northing")]
        public double Northing { get; set; }

        [JsonPropertyName("easting")]
        public double Easting { get; set; }

        [JsonPropertyName("elevation")]
        public double Elevation { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("raw_description")]
        public string? RawDescription { get; set; }
    }

    public class ApiPointStatistics
    {
        [JsonPropertyName("min_elevation")]
        public double MinElevation { get; set; }

        [JsonPropertyName("max_elevation")]
        public double MaxElevation { get; set; }

        [JsonPropertyName("mean_elevation")]
        public double? MeanElevation { get; set; }

        [JsonPropertyName("std_dev_elevation")]
        public double? StdDevElevation { get; set; }
    }

    public class ApiFeatureLine
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("layer")]
        public string? Layer { get; set; }

        [JsonPropertyName("style")]
        public string? Style { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }

        [JsonPropertyName("vertex_count")]
        public int VertexCount { get; set; }

        [JsonPropertyName("elevation_range")]
        public ApiElevationRange? ElevationRange { get; set; }

        [JsonPropertyName("is_3d")]
        public bool Is3d { get; set; }

        [JsonPropertyName("is_closed")]
        public bool IsClosed { get; set; }

        [JsonPropertyName("is_row_boundary")]
        public bool IsRowBoundary { get; set; }
    }

    public class ApiLaneWidth
    {
        [JsonPropertyName("side")]
        public string Side { get; set; } = "";

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("slope_percent")]
        public double? SlopePercent { get; set; }

        [JsonPropertyName("subassembly_name")]
        public string SubassemblyName { get; set; } = "";
    }

    public class ApiParcel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("site_name")]
        public string? SiteName { get; set; }

        [JsonPropertyName("area")]
        public double? Area { get; set; }

        [JsonPropertyName("perimeter")]
        public double? Perimeter { get; set; }
    }

    public class ApiLayerDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("color_aci")]
        public int ColorAci { get; set; }

        [JsonPropertyName("is_on")]
        public bool IsOn { get; set; }

        [JsonPropertyName("is_frozen")]
        public bool IsFrozen { get; set; }

        [JsonPropertyName("is_locked")]
        public bool IsLocked { get; set; }

        [JsonPropertyName("is_plottable")]
        public bool IsPlottable { get; set; }

        [JsonPropertyName("entity_count")]
        public int EntityCount { get; set; }

        [JsonPropertyName("entity_types")]
        public System.Collections.Generic.Dictionary<string, int>? EntityTypes { get; set; }
    }

    public class ApiLayerStatistics
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("on_count")]
        public int OnCount { get; set; }

        [JsonPropertyName("off_count")]
        public int OffCount { get; set; }

        [JsonPropertyName("frozen_count")]
        public int FrozenCount { get; set; }

        [JsonPropertyName("empty_count")]
        public int EmptyCount { get; set; }
    }

    public class ApiXrefDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Attach";

        [JsonPropertyName("is_loaded")]
        public bool IsLoaded { get; set; }

        [JsonPropertyName("is_resolved")]
        public bool IsResolved { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
    }

    public class ApiBlockDetailed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("insert_count")]
        public int InsertCount { get; set; }

        [JsonPropertyName("has_attributes")]
        public bool HasAttributes { get; set; }

        [JsonPropertyName("attribute_tags")]
        public System.Collections.Generic.List<string>? AttributeTags { get; set; }

        [JsonPropertyName("layers_used")]
        public System.Collections.Generic.List<string>? LayersUsed { get; set; }
    }

    #endregion

    #region Exceptions

    /// <summary>
    /// Exception thrown by MahodAgentService operations.
    /// </summary>
    public class MahodAgentException : Exception
    {
        public MahodAgentException(string message) : base(message) { }
        public MahodAgentException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion
}
