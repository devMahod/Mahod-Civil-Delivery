using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Service for communicating with the MahodAI Python Agent API.
    /// Uses session-based REST API with unified message endpoint.
    /// </summary>
    public class MahodAgentService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Default API base URL. The compiled-in default comes from
        /// <see cref="Config.PluginConstants.DefaultAgentApiUrl"/>, which is what
        /// /p:AgentEnvironment=prod switches — this file used to repeat the literal,
        /// so a production build still sent REST traffic to the dev server.
        /// </summary>
        public static readonly string DEFAULT_API_URL =
            Config.PluginConstants.ResolveAgentApiUrl();

        /// <summary>
        /// Initializes the MahodAI Agent service.
        /// </summary>
        /// <param name="baseUrl">API base URL (default: https://ai.mahodeng.co.il:8088/api)</param>
        /// <param name="apiKey">Optional API key for authentication</param>
        public MahodAgentService(string? baseUrl = null, string? apiKey = null)
        {
            _baseUrl = (baseUrl ?? DEFAULT_API_URL).TrimEnd('/');
            _apiKey = apiKey ?? string.Empty;

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Set API key header if provided
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            }

            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        #region Session Management

        /// <summary>
        /// Creates a new analysis session with the drawing summary.
        /// </summary>
        /// <param name="drawingSummaryJson">JSON string from DrawingAnalyzer.AnalyzeActiveDrawingJson()</param>
        /// <param name="drawingId">Optional unique drawing identifier (auto-generated if not provided)</param>
        /// <param name="drawingName">Drawing file name</param>
        /// <returns>Session creation response with session ID</returns>
        public async Task<SessionResponse> CreateSessionAsync(
            string drawingSummaryJson,
            string? drawingId = null,
            string? drawingName = null)
        {
            // Parse the drawing summary to extract data
            var summary = JsonSerializer.Deserialize<JsonElement>(drawingSummaryJson);

            // Generate IDs
            var actualDrawingId = drawingId ?? Guid.NewGuid().ToString("N");
            var actualDrawingName = drawingName ?? GetJsonString(summary, "FileName") ?? "unknown.dwg";

            var apiSummary = BuildApiSummaryFromNewFormat(summary, actualDrawingId, actualDrawingName);

            // Progressive reduction: try full payload first, then strip non-critical
            // data in stages until it fits. Flagged items (analysis-critical) are preserved
            // as long as possible.
            var reductionLevel = ReductionLevel.None;
            ApiSummary? strippedData = null;

            while (true)
            {
                var request = new SessionCreateRequest
                {
                    DrawingId = actualDrawingId,
                    DrawingName = actualDrawingName,
                    Summary = apiSummary
                };

                var jsonString = JsonSerializer.Serialize(request, JsonOptions);
                var content = CreateGzipContent(jsonString);

                System.Diagnostics.Debug.WriteLine(
                    $"Session create attempt (reduction={reductionLevel}, payload={jsonString.Length} bytes)");

                var response = await _httpClient.PostAsync("/api/v1/sessions", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<SessionResponse>(responseContent, JsonOptions)
                        ?? throw new MahodAgentException("Invalid session response");

                    // If we stripped data, upload it incrementally in the background
                    if (strippedData != null)
                    {
                        var sessionId = result.SessionId;
                        var stripped = strippedData;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await UploadStrippedDetailsAsync(sessionId, stripped);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"Background detail upload failed: {ex.Message}");
                            }
                        });
                    }

                    return result;
                }

                if (response.StatusCode != System.Net.HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new MahodAgentException(
                        $"Failed to create session: {response.StatusCode} - {responseContent}");
                }

                // Move to next reduction level
                reductionLevel++;
                if (reductionLevel > ReductionLevel.Minimal)
                {
                    throw new MahodAgentException(
                        $"Failed to create session: payload too large even after maximum reduction ({jsonString.Length} bytes)");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"Session creation returned 413 (payload {jsonString.Length} bytes). Reducing to level {reductionLevel}.");

                strippedData ??= new ApiSummary();
                ApplyReduction(apiSummary, strippedData, reductionLevel);
            }
        }

        /// <summary>
        /// Reduction levels for progressive payload shrinking.
        /// Each level strips more data while preserving analysis-critical items.
        /// </summary>
        private enum ReductionLevel
        {
            /// <summary>Full payload, no reduction.</summary>
            None = 0,
            /// <summary>Strip conforming curves, keep flagged curves and all PVIs.</summary>
            StripConformingCurves = 1,
            /// <summary>Strip all curves and superelevation, keep PVIs and vertical curves.</summary>
            StripAllCurves = 2,
            /// <summary>Strip everything heavy: curves, PVIs, vertical curves, superelevation, speed segments.</summary>
            Minimal = 3
        }

        /// <summary>
        /// Applies progressive reduction to the summary, moving stripped data to a separate object
        /// so it can be uploaded incrementally after session creation.
        /// </summary>
        private static void ApplyReduction(ApiSummary summary, ApiSummary strippedData, ReductionLevel level)
        {
            switch (level)
            {
                case ReductionLevel.StripConformingCurves:
                    // Keep flagged curves, strip conforming ones
                    // Keep PVIs (small, critical for grade analysis)
                    // Keep superelevation (needed for cross-slope checks)
                    summary.Alignments?.ForEach(a =>
                    {
                        if (a.Curves == null || a.Curves.Count == 0) return;

                        var flaggedStations = new System.Collections.Generic.HashSet<double>();
                        if (a.FlaggedCurves != null)
                        {
                            foreach (var fc in a.FlaggedCurves)
                                flaggedStations.Add(fc.StartStation);
                        }

                        // Save all curves for later upload, keep only flagged ones
                        var stripped = new ApiAlignment { Name = a.Name, Curves = new(a.Curves) };
                        strippedData.Alignments ??= new();
                        strippedData.Alignments.Add(stripped);

                        if (flaggedStations.Count > 0)
                        {
                            a.Curves = a.Curves.FindAll(c => flaggedStations.Contains(c.StartStation));
                        }
                        else
                        {
                            // No flagged curves — keep first and last for context
                            var kept = new System.Collections.Generic.List<ApiCurveInfo>();
                            if (a.Curves.Count > 0) kept.Add(a.Curves[0]);
                            if (a.Curves.Count > 1) kept.Add(a.Curves[a.Curves.Count - 1]);
                            a.Curves = kept;
                        }
                    });
                    break;

                case ReductionLevel.StripAllCurves:
                    // Strip all remaining curves and superelevation
                    // Keep PVIs and vertical curves (critical for grade/K-value analysis)
                    summary.Alignments?.ForEach(a =>
                    {
                        // Only save to stripped if not already saved in previous level
                        var existing = strippedData.Alignments?.Find(s => s.Name == a.Name);
                        if (existing != null)
                        {
                            existing.Superelevation = new(a.Superelevation);
                        }
                        else
                        {
                            strippedData.Alignments ??= new();
                            strippedData.Alignments.Add(new ApiAlignment
                            {
                                Name = a.Name,
                                Curves = new(a.Curves),
                                Superelevation = new(a.Superelevation)
                            });
                        }

                        a.Curves.Clear();
                        a.Superelevation.Clear();
                    });
                    break;

                case ReductionLevel.Minimal:
                    // Strip everything: curves, PVIs, vertical curves, superelevation, speed segments
                    summary.Alignments?.ForEach(a =>
                    {
                        a.Curves.Clear();
                        a.Superelevation.Clear();
                        a.SpeedSegments = null;
                    });

                    summary.Profiles?.ForEach(p =>
                    {
                        // Save PVIs and vertical curves for later upload
                        strippedData.Profiles ??= new();
                        strippedData.Profiles.Add(new ApiProfileDetailed
                        {
                            Name = p.Name,
                            AlignmentName = p.AlignmentName,
                            PviPoints = new(p.PviPoints),
                            VerticalCurves = new(p.VerticalCurves)
                        });

                        p.PviPoints.Clear();
                        p.VerticalCurves.Clear();
                    });
                    break;
            }
        }

        /// <summary>
        /// Uploads stripped entity details as incremental PATCH requests after session creation.
        /// Each entity's data is sent separately to stay under proxy size limits.
        /// </summary>
        private async Task UploadStrippedDetailsAsync(string sessionId, ApiSummary strippedData)
        {
            // Upload alignment details (curves, superelevation)
            if (strippedData.Alignments != null)
            {
                foreach (var alignment in strippedData.Alignments)
                {
                    if ((alignment.Curves == null || alignment.Curves.Count == 0) &&
                        (alignment.Superelevation == null || alignment.Superelevation.Count == 0))
                        continue;

                    var detail = new
                    {
                        detail_type = "alignment",
                        entity_name = alignment.Name,
                        data = new
                        {
                            curves = alignment.Curves,
                            superelevation = alignment.Superelevation
                        }
                    };

                    await SendDetailUploadAsync(sessionId, detail);
                }
            }

            // Upload profile details (PVIs, vertical curves)
            if (strippedData.Profiles != null)
            {
                foreach (var profile in strippedData.Profiles)
                {
                    if ((profile.PviPoints == null || profile.PviPoints.Count == 0) &&
                        (profile.VerticalCurves == null || profile.VerticalCurves.Count == 0))
                        continue;

                    var detail = new
                    {
                        detail_type = "profile",
                        entity_name = profile.Name,
                        data = new
                        {
                            alignment_name = profile.AlignmentName,
                            pvi_points = profile.PviPoints,
                            vertical_curves = profile.VerticalCurves
                        }
                    };

                    await SendDetailUploadAsync(sessionId, detail);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"Background detail upload completed for session {sessionId}");
        }

        /// <summary>
        /// Sends a single detail upload request to the session's detail endpoint.
        /// </summary>
        private async Task SendDetailUploadAsync(string sessionId, object detail)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(detail, JsonOptions);
                var content = CreateGzipContent(jsonString);

                var response = await _httpClient.PatchAsync(
                    $"/api/v1/sessions/{sessionId}/details",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine(
                        $"Detail upload failed: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Detail upload error: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates gzip-compressed HTTP content from a JSON string.
        /// </summary>
        private static HttpContent CreateGzipContent(string jsonString)
        {
            var jsonBytes = Encoding.UTF8.GetBytes(jsonString);

            // Only compress if payload is larger than 1KB
            if (jsonBytes.Length < 1024)
            {
                return new StringContent(jsonString, Encoding.UTF8, "application/json");
            }

            using var compressedStream = new MemoryStream();
            using (var gzip = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            }

            var content = new ByteArrayContent(compressedStream.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            return content;
        }

        /// <summary>
        /// Builds API summary from new DrawingSummaryExtractor format (already in agent schema).
        /// This extracts the key metadata/statistics and passes arrays as-is.
        /// </summary>
        private static ApiSummary BuildApiSummaryFromNewFormat(JsonElement source, string drawingId, string drawingName)
        {
            var summary = new ApiSummary
            {
                // Initialize all list properties to avoid null reference exceptions
                Surfaces = new System.Collections.Generic.List<ApiSurfaceDetailed>(),
                Alignments = new System.Collections.Generic.List<ApiAlignment>(),
                Profiles = new System.Collections.Generic.List<ApiProfileDetailed>(),
                Corridors = new System.Collections.Generic.List<ApiCorridor>(),
                PipeNetworks = new System.Collections.Generic.List<ApiPipeNetwork>(),
                Layers = new System.Collections.Generic.List<ApiLayerDetailed>()
            };

            // Get metadata
            if (source.TryGetProperty("metadata", out var metadata))
            {
                string unitsStr = GetJsonString(metadata, "units") ?? "Meters";
                var units = new ApiUnits { Linear = unitsStr };
                if (unitsStr == "Meters") { units.Area = "SquareMeters"; units.Volume = "CubicMeters"; }
                else if (unitsStr == "Feet") { units.Area = "SquareFeet"; units.Volume = "CubicFeet"; }

                summary.Metadata = new ApiMetadata
                {
                    DrawingId = drawingId,
                    DrawingName = GetJsonString(metadata, "drawing_name") ?? drawingName,
                    FilePath = GetJsonString(metadata, "file_path"),
                    Units = unitsStr,
                    UnitsDetail = units,
                    Scale = 1.0
                };
            }
            else
            {
                summary.Metadata = new ApiMetadata { DrawingId = drawingId, DrawingName = drawingName };
            }

            // Get statistics
            if (source.TryGetProperty("statistics", out var stats))
            {
                double? zMin = GetJsonDoubleNullable(stats, "z_min");
                double? zMax = GetJsonDoubleNullable(stats, "z_max");
                double? minX = GetJsonDoubleNullable(stats, "bounds_min_x");
                double? minY = GetJsonDoubleNullable(stats, "bounds_min_y");
                double? maxX = GetJsonDoubleNullable(stats, "bounds_max_x");
                double? maxY = GetJsonDoubleNullable(stats, "bounds_max_y");

                summary.Statistics = new ApiStatistics
                {
                    TotalEntities = GetJsonInt(stats, "total_entities"),
                    TotalPoints = GetJsonInt(stats, "total_points"),
                    TotalTriangles = GetJsonInt(stats, "total_triangles"),
                    ZMin = zMin ?? 0,
                    ZMax = zMax ?? 0
                };

                // Coordinate system from bounds
                if (minX.HasValue && minY.HasValue)
                {
                    summary.CoordinateSystem = new ApiCoordinateSystem
                    {
                        Units = summary.Metadata.Units ?? "Meters",
                        Bounds = new ApiBounds { MinX = minX ?? 0, MinY = minY ?? 0, MaxX = maxX ?? 0, MaxY = maxY ?? 0 }
                    };

                    // Detect Israel TM Grid
                    if (minX > 100000 && minX < 300000 && minY > 300000 && minY < 800000)
                    {
                        summary.CoordinateSystem.Name = "Israel TM Grid";
                        summary.CoordinateSystem.EpsgCode = 2039;
                        summary.CoordinateSystem.Projection = "Transverse Mercator";
                        summary.CoordinateSystem.Datum = "Israel 1993";
                    }
                }
            }
            else
            {
                summary.Statistics = new ApiStatistics();
            }

            // Extract surfaces from new format
            if (source.TryGetProperty("surfaces", out var surfacesArr) && surfacesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in surfacesArr.EnumerateArray())
                {
                    summary.Surfaces.Add(new ApiSurfaceDetailed
                    {
                        Name = GetJsonString(s, "name") ?? "",
                        Type = GetJsonString(s, "type") ?? "Unknown",
                        PointCount = GetJsonInt(s, "point_count"),
                        TriangleCount = GetJsonInt(s, "triangle_count"),
                        ElevationMin = GetJsonDoubleNullable(s, "elevation_min"),
                        ElevationMax = GetJsonDoubleNullable(s, "elevation_max"),
                        SlopeMinPercent = GetJsonDoubleNullable(s, "slope_min_percent"),
                        SlopeMaxPercent = GetJsonDoubleNullable(s, "slope_max_percent"),
                        SlopeMeanPercent = GetJsonDoubleNullable(s, "slope_mean_percent")
                    });
                }
            }

            // Extract alignments from new format
            if (source.TryGetProperty("alignments", out var alignsArr) && alignsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in alignsArr.EnumerateArray())
                {
                    var align = new ApiAlignment
                    {
                        Name = GetJsonString(a, "name") ?? "",
                        Description = GetJsonString(a, "description"),
                        Length = GetJsonDouble(a, "length"),
                        StartStation = GetJsonDouble(a, "start_station"),
                        EndStation = GetJsonDouble(a, "end_station"),
                        DesignSpeedKph = GetJsonDoubleNullable(a, "design_speed_kph"),
                        MinRadius = GetJsonDouble(a, "min_radius"),
                        MaxRadius = GetJsonDoubleNullable(a, "max_radius"),
                        TangentCount = GetJsonInt(a, "tangent_count"),
                        CurveCount = GetJsonInt(a, "curve_count"),
                        SpiralCount = GetJsonInt(a, "spiral_count"),
                        HasSuperelevation = GetJsonBool(a, "has_superelevation")
                    };

                    // Extract speed segments
                    if (a.TryGetProperty("speed_segments", out var speedSegs) && speedSegs.ValueKind == JsonValueKind.Array)
                    {
                        align.SpeedSegments = new System.Collections.Generic.List<ApiSpeedSegment>();
                        foreach (var seg in speedSegs.EnumerateArray())
                        {
                            align.SpeedSegments.Add(new ApiSpeedSegment
                            {
                                Station = GetJsonDouble(seg, "station"),
                                SpeedKph = GetJsonDouble(seg, "speed_kph")
                            });
                        }
                    }

                    if (a.TryGetProperty("profile_names", out var profileNames) && profileNames.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in profileNames.EnumerateArray())
                        {
                            if (p.ValueKind == JsonValueKind.String)
                                align.ProfileNames.Add(p.GetString() ?? "");
                        }
                    }

                    // Extract curves
                    if (a.TryGetProperty("curves", out var curvesArr) && curvesArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in curvesArr.EnumerateArray())
                        {
                            align.Curves.Add(new ApiCurveInfo
                            {
                                Type = GetJsonString(c, "type") ?? "",
                                StartStation = GetJsonDouble(c, "start_station"),
                                EndStation = GetJsonDouble(c, "end_station"),
                                Length = GetJsonDouble(c, "length"),
                                Radius = GetJsonDouble(c, "radius"),
                                Direction = GetJsonString(c, "direction") ?? "",
                                SpiralInLength = GetJsonDoubleNullable(c, "spiral_in_length"),
                                SpiralOutLength = GetJsonDoubleNullable(c, "spiral_out_length")
                            });
                        }
                    }

                    // Extract superelevation
                    if (a.TryGetProperty("superelevation", out var superArr) && superArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in superArr.EnumerateArray())
                        {
                            align.Superelevation.Add(new ApiSuperelevationInfo
                            {
                                Station = GetJsonDouble(s, "station"),
                                LeftSlopePercent = GetJsonDouble(s, "left_slope_percent"),
                                RightSlopePercent = GetJsonDouble(s, "right_slope_percent"),
                                PivotMethod = GetJsonString(s, "pivot_method") ?? ""
                            });
                        }
                    }

                    align.RoadTypeHint = GetJsonString(a, "road_type_hint");

                    // Extract flagged curves
                    if (a.TryGetProperty("flagged_curves", out var flaggedCurvesArr) && flaggedCurvesArr.ValueKind == JsonValueKind.Array)
                    {
                        align.FlaggedCurves = new System.Collections.Generic.List<ApiFlaggedCurve>();
                        foreach (var fc in flaggedCurvesArr.EnumerateArray())
                        {
                            align.FlaggedCurves.Add(new ApiFlaggedCurve
                            {
                                StartStation = GetJsonDouble(fc, "start_station"),
                                EndStation = GetJsonDouble(fc, "end_station"),
                                Radius = GetJsonDouble(fc, "radius"),
                                RequiredRadius = GetJsonDouble(fc, "required_radius"),
                                SpeedKph = GetJsonDouble(fc, "speed_kph")
                            });
                        }
                    }

                    summary.Alignments.Add(align);
                }
            }

            // Extract profiles from new format
            if (source.TryGetProperty("profiles", out var profilesArr) && profilesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in profilesArr.EnumerateArray())
                {
                    var profile = new ApiProfileDetailed
                    {
                        Name = GetJsonString(p, "name") ?? "",
                        AlignmentName = GetJsonString(p, "alignment_name") ?? "",
                        Type = GetJsonString(p, "profile_type") ?? "",
                        PviCount = GetJsonInt(p, "pvi_count"),
                        ElevationMin = GetJsonDoubleNullable(p, "elevation_min"),
                        ElevationMax = GetJsonDoubleNullable(p, "elevation_max"),
                        MinGradePercent = GetJsonDoubleNullable(p, "min_grade_percent"),
                        MaxGradePercent = GetJsonDoubleNullable(p, "max_grade_percent"),
                        VerticalCurveCount = GetJsonInt(p, "vertical_curve_count"),
                        MinKValue = GetJsonDoubleNullable(p, "min_k_value"),
                        MaxKValue = GetJsonDoubleNullable(p, "max_k_value")
                    };

                    // Extract PVI points
                    if (p.TryGetProperty("pvi_points", out var pviArr) && pviArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var pvi in pviArr.EnumerateArray())
                        {
                            profile.PviPoints.Add(new ApiPviPoint
                            {
                                Station = GetJsonDouble(pvi, "station"),
                                Elevation = GetJsonDouble(pvi, "elevation"),
                                GradeInPercent = GetJsonDoubleNullable(pvi, "grade_in_percent"),
                                GradeOutPercent = GetJsonDoubleNullable(pvi, "grade_out_percent")
                            });
                        }
                    }

                    // Extract vertical curves
                    if (p.TryGetProperty("vertical_curves", out var vcArr) && vcArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var vc in vcArr.EnumerateArray())
                        {
                            profile.VerticalCurves.Add(new ApiVerticalCurveInfo
                            {
                                PviStation = GetJsonDouble(vc, "pvi_station"),
                                PviElevation = GetJsonDouble(vc, "pvi_elevation"),
                                Length = GetJsonDouble(vc, "length"),
                                KValue = GetJsonDouble(vc, "k_value"),
                                Type = GetJsonString(vc, "type") ?? "",
                                GradeInPercent = GetJsonDouble(vc, "grade_in_percent"),
                                GradeOutPercent = GetJsonDouble(vc, "grade_out_percent")
                            });
                        }
                    }

                    // Extract flagged grades
                    if (p.TryGetProperty("flagged_grades", out var flaggedGradesArr) && flaggedGradesArr.ValueKind == JsonValueKind.Array)
                    {
                        profile.FlaggedGrades = new System.Collections.Generic.List<ApiFlaggedGrade>();
                        foreach (var fg in flaggedGradesArr.EnumerateArray())
                        {
                            profile.FlaggedGrades.Add(new ApiFlaggedGrade
                            {
                                FromStation = GetJsonDouble(fg, "from_station"),
                                ToStation = GetJsonDouble(fg, "to_station"),
                                GradePercent = GetJsonDouble(fg, "grade_percent"),
                                MaxAllowedGrade = GetJsonDouble(fg, "max_allowed_grade")
                            });
                        }
                    }

                    summary.Profiles.Add(profile);
                }
            }

            // Extract corridors from new format
            if (source.TryGetProperty("corridors", out var corridorsArr) && corridorsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in corridorsArr.EnumerateArray())
                {
                    var corr = new ApiCorridor
                    {
                        Name = GetJsonString(c, "name") ?? "",
                        CorridorType = GetJsonString(c, "corridor_type") ?? "Road",
                        BaselineAlignment = GetJsonString(c, "baseline_alignment"),
                        BaselineProfile = GetJsonString(c, "baseline_profile"),
                        TotalLength = GetJsonDouble(c, "total_length"),
                        BaselineCount = GetJsonInt(c, "baseline_count"),
                        RegionCount = GetJsonInt(c, "region_count"),
                        SurfaceCount = GetJsonInt(c, "surface_count")
                    };

                    if (c.TryGetProperty("alignment_names", out var alignNames) && alignNames.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var n in alignNames.EnumerateArray())
                        {
                            if (n.ValueKind == JsonValueKind.String)
                                corr.AlignmentNames.Add(n.GetString() ?? "");
                        }
                    }

                    if (c.TryGetProperty("assemblies_used", out var assemblies) && assemblies.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var n in assemblies.EnumerateArray())
                        {
                            if (n.ValueKind == JsonValueKind.String)
                                corr.AssembliesUsed.Add(n.GetString() ?? "");
                        }
                    }

                    // Parse lane widths
                    if (c.TryGetProperty("lane_widths", out var laneArr) && laneArr.ValueKind == JsonValueKind.Array)
                    {
                        corr.LaneWidths = new System.Collections.Generic.List<ApiLaneWidth>();
                        foreach (var lw in laneArr.EnumerateArray())
                        {
                            corr.LaneWidths.Add(new ApiLaneWidth
                            {
                                Side = GetJsonString(lw, "side") ?? "",
                                Width = GetJsonDouble(lw, "width"),
                                SlopePercent = GetJsonDoubleNullable(lw, "slope_percent"),
                                SubassemblyName = GetJsonString(lw, "subassembly_name") ?? ""
                            });
                        }
                    }

                    // Parse max corridor widths
                    corr.MaxLeftWidth = GetJsonDoubleNullable(c, "max_left_width");
                    corr.MaxRightWidth = GetJsonDoubleNullable(c, "max_right_width");

                    summary.Corridors.Add(corr);
                }
            }

            // Extract feature lines from new format
            if (source.TryGetProperty("feature_lines", out var featureLinesArr) && featureLinesArr.ValueKind == JsonValueKind.Array)
            {
                summary.FeatureLines ??= new System.Collections.Generic.List<ApiFeatureLine>();
                foreach (var fl in featureLinesArr.EnumerateArray())
                {
                    var featureLine = new ApiFeatureLine
                    {
                        Name = GetJsonString(fl, "name") ?? "",
                        Layer = GetJsonString(fl, "layer"),
                        Length = GetJsonDouble(fl, "length"),
                        IsClosed = fl.TryGetProperty("is_closed", out var isClosed) && isClosed.ValueKind == JsonValueKind.True,
                        IsRowBoundary = fl.TryGetProperty("is_row_boundary", out var isRow) && isRow.ValueKind == JsonValueKind.True
                    };

                    if (fl.TryGetProperty("elevation_min", out var elevMin) && elevMin.ValueKind == JsonValueKind.Number &&
                        fl.TryGetProperty("elevation_max", out var elevMax) && elevMax.ValueKind == JsonValueKind.Number)
                    {
                        featureLine.ElevationRange = new ApiElevationRange
                        {
                            Min = elevMin.GetDouble(),
                            Max = elevMax.GetDouble()
                        };
                    }

                    summary.FeatureLines.Add(featureLine);
                }
            }

            // Extract pipe networks from new format
            if (source.TryGetProperty("pipe_networks", out var networksArr) && networksArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in networksArr.EnumerateArray())
                {
                    summary.PipeNetworks.Add(new ApiPipeNetwork
                    {
                        Name = GetJsonString(n, "name") ?? "",
                        Type = GetJsonString(n, "type") ?? "",
                        StructureCount = GetJsonInt(n, "structure_count"),
                        PipeCount = GetJsonInt(n, "pipe_count"),
                        TotalPipeLength = GetJsonDouble(n, "total_pipe_length")
                    });
                }
            }

            // Extract layers from new format
            if (source.TryGetProperty("layers", out var layersArr) && layersArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in layersArr.EnumerateArray())
                {
                    summary.Layers.Add(new ApiLayerDetailed
                    {
                        Name = GetJsonString(l, "name") ?? "",
                        EntityCount = GetJsonInt(l, "entity_count")
                    });
                }
            }

            return summary;
        }


        #endregion

        #region Message API

        /// <summary>
        /// Sends an analysis request to analyze the drawing.
        /// </summary>
        public async Task<MessageResponse> AnalyzeAsync(string sessionId, string? instructions = null)
        {
            return await SendMessageAsync(sessionId, new MessageRequest
            {
                Type = "analyze",
                Content = instructions ?? "נתח את השרטוט והשווה לתקנים"
            });
        }

        /// <summary>
        /// Sends a chat message to ask questions about the drawing.
        /// </summary>
        public async Task<MessageResponse> ChatAsync(string sessionId, string question, CancellationToken cancellationToken = default)
        {
            return await SendMessageAsync(sessionId, new MessageRequest
            {
                Type = "chat",
                Content = question
            }, cancellationToken);
        }

        /// <summary>
        /// Provides additional details requested by the agent.
        /// </summary>
        public async Task<MessageResponse> ProvideDetailsAsync(string sessionId, object details)
        {
            return await SendMessageAsync(sessionId, new MessageRequest
            {
                Type = "provide_details",
                Details = details
            });
        }

        /// <summary>
        /// Sends a tool execution request (Stage 2).
        /// </summary>
        public async Task<MessageResponse> ExecuteToolAsync(string sessionId, string toolName, object? toolParams = null)
        {
            return await SendMessageAsync(sessionId, new MessageRequest
            {
                Type = "tool",
                ToolName = toolName,
                ToolParams = toolParams
            });
        }

        /// <summary>
        /// Sends a message to the session endpoint.
        /// </summary>
        private async Task<MessageResponse> SendMessageAsync(string sessionId, MessageRequest request, CancellationToken cancellationToken = default)
        {
            EnsureSession(sessionId);
            var content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"/api/v1/sessions/{sessionId}/message",
                content,
                cancellationToken);

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new MahodAgentException(
                    $"Message failed: {response.StatusCode} - {responseContent}");
            }

            return JsonSerializer.Deserialize<MessageResponse>(responseContent, JsonOptions)
                ?? throw new MahodAgentException("Invalid message response");
        }

        #endregion

        #region Health Check

        /// <summary>
        /// Checks if the API is healthy and accessible.
        /// </summary>
        /// <returns>True if API is healthy</returns>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/v1/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private static void EnsureSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new MahodAgentException(
                    "No active session. Call CreateSessionAsync first.");
            }
        }

        private static string? GetJsonString(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
            return null;
        }

        private static int GetJsonInt(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
                return val.GetInt32();
            return 0;
        }

        private static double GetJsonDouble(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
                return val.GetDouble();
            return 0;
        }

        private static double? GetJsonDoubleNullable(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val))
            {
                if (val.ValueKind == JsonValueKind.Number)
                    return val.GetDouble();
                if (val.ValueKind == JsonValueKind.Null)
                    return null;
            }
            return null;
        }

        private static bool GetJsonBool(JsonElement el, string prop)
        {
            if (el.TryGetProperty(prop, out var val))
            {
                if (val.ValueKind == JsonValueKind.True) return true;
                if (val.ValueKind == JsonValueKind.False) return false;
            }
            return false;
        }

        /// <summary>
        /// Converts various date formats to ISO 8601 format.
        /// Handles formats like "07.01.2026 10:35" or "01/07/2026 10:35:00"
        /// </summary>
        private static string? ConvertToIsoDate(string? dateStr)
        {
            if (string.IsNullOrEmpty(dateStr))
                return null;

            // Try common date formats
            string[] formats = new[]
            {
                "dd.MM.yyyy HH:mm",
                "dd.MM.yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm:ss",
                "MM/dd/yyyy HH:mm",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd HH:mm",
                "dd/MM/yyyy HH:mm:ss",
                "dd/MM/yyyy HH:mm"
            };

            if (DateTime.TryParseExact(dateStr, formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm:ss");
            }

            // Try general parse as fallback
            if (DateTime.TryParse(dateStr, out dt))
            {
                return dt.ToString("yyyy-MM-ddTHH:mm:ss");
            }

            // Return null if can't parse - better than invalid format
            return null;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
