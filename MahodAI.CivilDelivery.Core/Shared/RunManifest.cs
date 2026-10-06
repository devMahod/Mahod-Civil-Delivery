using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Machine-readable evidence for every Plan/Preview/Apply/Verify/Extract run
    /// (locked plan §5.5). Evidence, not telemetry.
    /// </summary>
    public sealed class RunManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; } = 2;

        [JsonPropertyName("run_id")]
        public required string RunId { get; init; }

        /// <summary>sections | estimate</summary>
        [JsonPropertyName("feature")]
        public required string Feature { get; init; }

        /// <summary>discover | plan | preview | apply | verify | extract | submit</summary>
        [JsonPropertyName("operation")]
        public required string Operation { get; init; }

        /// <summary>Explicit workflow scope, for example batch or selected-record.</summary>
        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        /// <summary>The only proven record when Scope is selected-record.</summary>
        [JsonPropertyName("selected_record_id")]
        public string? SelectedRecordId { get; set; }

        [JsonPropertyName("started_at_utc")]
        public DateTime StartedAtUtc { get; init; }

        [JsonPropertyName("completed_at_utc")]
        public DateTime CompletedAtUtc { get; set; }

        [JsonPropertyName("user")]
        public string User { get; init; } = Environment.UserName;

        [JsonPropertyName("machine")]
        public string Machine { get; init; } = Environment.MachineName;

        [JsonPropertyName("civil_version")]
        public string? CivilVersion { get; set; }

        [JsonPropertyName("plugin_git_sha")]
        public string? PluginGitSha { get; set; }

        [JsonPropertyName("plugin_build_version")]
        public string? PluginBuildVersion { get; set; }

        /// <summary>
        /// Autodesk bundle AppVersion (the employee package revision), read from the
        /// PackageContents.xml that encloses the running plug-in assembly.
        /// </summary>
        [JsonPropertyName("plugin_package_revision")]
        public string? PluginPackageRevision { get; set; }

        /// <summary>
        /// SHA-256 of the exact plug-in DLL that emitted this manifest.  This remains
        /// authoritative when a build came from a dirty working tree and the source
        /// revision alone cannot uniquely identify its bytes.
        /// </summary>
        [JsonPropertyName("plugin_assembly_sha256")]
        public string? PluginAssemblySha256 { get; set; }

        [JsonPropertyName("project_profile_id")]
        public string? ProjectProfileId { get; set; }

        [JsonPropertyName("project_profile_hash")]
        public string? ProjectProfileHash { get; set; }

        [JsonPropertyName("input_drawings")]
        public List<string> InputDrawings { get; init; } = new();

        [JsonPropertyName("input_hashes")]
        public List<string> InputHashes { get; init; } = new();

        /// <summary>
        /// Exact binding between every input path and its SHA-256. The parallel v1
        /// lists above remain for compatibility, but only this map is authoritative.
        /// </summary>
        [JsonPropertyName("input_hashes_by_path")]
        public Dictionary<string, string> InputHashesByPath { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("landq_version_or_contract_hash")]
        public string? LandQVersionOrContractHash { get; set; }

        [JsonPropertyName("result_status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DeliveryStatus ResultStatus { get; set; } = DeliveryStatus.Discovered;

        [JsonPropertyName("record_counts")]
        public Dictionary<string, int> RecordCounts { get; init; } = new();

        [JsonPropertyName("finding_counts")]
        public Dictionary<string, int> FindingCounts { get; init; } = new();

        [JsonPropertyName("artifacts")]
        public List<string> Artifacts { get; init; } = new();

        /// <summary>
        /// SHA-256 of every artifact named above, keyed by its authoritative published
        /// path. A path without a digest is not provenance and must not be published.
        /// </summary>
        [JsonPropertyName("artifact_hashes")]
        public Dictionary<string, string> ArtifactHashes { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static string NewRunId(string feature, string operation, DateTime? nowUtc = null)
        {
            var t = (nowUtc ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss");
            return $"{feature}-{operation}-{t}-{Guid.NewGuid().ToString("N")[..8]}";
        }

        public void CountFinding(DeliveryFinding finding)
        {
            FindingCounts.TryGetValue(finding.Code, out var n);
            FindingCounts[finding.Code] = n + 1;
        }
    }

    /// <summary>Writes run manifests under a runs root that stays out of version control.</summary>
    public static class RunManifestWriter
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// Default runs root: %LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\runs. A
        /// repo checkout is not assumed at runtime on an engineer's machine.
        /// </summary>
        public static string DefaultRunsRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MahodAI_Civil3D", "civil-delivery", "runs");

        public static string Write(RunManifest manifest, string? runsRoot = null)
        {
            var root = runsRoot ?? DefaultRunsRoot;
            var dir = Path.Combine(root, manifest.RunId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "run_manifest.json");
            AtomicTextFile.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
            return path;
        }
    }

    /// <summary>
    /// Replaces one evidence file only after the complete new payload reached a sibling
    /// temporary file. A disk/full/permission failure can leave a disposable .tmp file,
    /// never a truncated green result at the authoritative path.
    /// </summary>
    public static class AtomicTextFile
    {
        public static void WriteAllText(string path, string contents)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(contents);
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ??
                            throw new InvalidOperationException("Evidence path has no parent directory.");
            Directory.CreateDirectory(directory);
            var pending = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           81920, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(pending, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(pending)) File.Delete(pending); } catch { }
            }
        }
    }
}
