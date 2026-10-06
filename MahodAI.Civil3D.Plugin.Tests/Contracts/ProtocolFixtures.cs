using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MahodAI.Civil3D.Plugin.Tests.Contracts
{
    /// <summary>
    /// One golden protocol fixture: the self-describing <c>$contract</c> block plus
    /// the realistic <c>message</c> envelope it documents.
    /// See <c>&lt;workspace&gt;/contracts/README.md</c>.
    /// </summary>
    public sealed class ProtocolFixture
    {
        private readonly JsonDocument _doc;

        private ProtocolFixture(string relativePath, string fullPath, string rawText, JsonDocument doc)
        {
            RelativePath = relativePath;
            FullPath = fullPath;
            RawText = rawText;
            _doc = doc;
        }

        /// <summary>Path relative to the fixtures root, forward slashes (matches manifest.json).</summary>
        public string RelativePath { get; }

        public string FullPath { get; }

        /// <summary>File content as read from disk (line endings NOT normalized).</summary>
        public string RawText { get; }

        public JsonElement Root => _doc.RootElement;

        public JsonElement Contract => Root.GetProperty("$contract");

        public JsonElement Message => Root.GetProperty("message");

        public JsonElement Payload => Message.GetProperty("payload");

        public string ProtocolVersion => Contract.GetProperty("protocol_version").GetString()!;

        public string Type => Contract.GetProperty("type").GetString()!;

        public string Direction => Contract.GetProperty("direction").GetString()!;

        public string Description => Contract.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

        public IReadOnlyList<string> Audience => StringArray("audience");

        public IReadOnlyList<string> RequiredPayloadFields => StringArray("required_payload_fields");

        public IReadOnlyList<string> OptionalPayloadFields => StringArray("optional_payload_fields");

        public bool IsForPlugin => Audience.Contains("plugin");

        public bool IsServerToClient => Direction == "server_to_client";

        /// <summary>Raw JSON text of the payload, exactly as it arrives on the wire.</summary>
        public string PayloadJson => Payload.GetRawText();

        private IReadOnlyList<string> StringArray(string name)
        {
            if (!Contract.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        }

        public override string ToString() => RelativePath;

        internal static ProtocolFixture Load(string fixturesRoot, string fullPath)
        {
            var relative = Path.GetRelativePath(fixturesRoot, fullPath).Replace('\\', '/');
            var text = File.ReadAllText(fullPath);
            var doc = JsonDocument.Parse(text);
            return new ProtocolFixture(relative, fullPath, text, doc);
        }
    }

    /// <summary>One entry of the mirrored <c>manifest.json</c>.</summary>
    public sealed class ManifestEntry
    {
        public string File { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Direction { get; init; } = string.Empty;
        public IReadOnlyList<string> Audience { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Loads the mirrored protocol fixtures out of the test output directory.
    ///
    /// The fixtures are copied by an MSBuild glob (see the .csproj) so a fixture
    /// added upstream needs no test-project edit — it simply starts appearing in
    /// every theory below.
    /// </summary>
    public static class ProtocolFixtures
    {
        private static readonly Lazy<string> RootLazy = new(ResolveRoot);
        private static readonly Lazy<IReadOnlyList<ProtocolFixture>> AllLazy = new(LoadAll);
        private static readonly Lazy<JsonDocument> ManifestLazy = new(LoadManifestDocument);
        private static readonly Lazy<IReadOnlyList<ManifestEntry>> ManifestEntriesLazy = new(LoadManifestEntries);

        /// <summary>Absolute path of the fixtures root inside the test output directory.</summary>
        public static string Root => RootLazy.Value;

        public static string ManifestPath => Path.Combine(Root, "manifest.json");

        public static IReadOnlyList<ProtocolFixture> All => AllLazy.Value;

        public static IReadOnlyList<ManifestEntry> ManifestEntries => ManifestEntriesLazy.Value;

        public static string ManifestProtocolVersion =>
            ManifestLazy.Value.RootElement.GetProperty("protocol_version").GetString()!;

        public static int ManifestFixtureCount =>
            ManifestLazy.Value.RootElement.GetProperty("fixture_count").GetInt32();

        public static ProtocolFixture Get(string relativePath) =>
            All.FirstOrDefault(f => f.RelativePath == relativePath)
            ?? throw new InvalidOperationException(
                $"Fixture '{relativePath}' not found under '{Root}'. " +
                "Did the MSBuild copy glob in MahodAI.Civil3D.Plugin.Tests.csproj stop matching?");

        /// <summary>
        /// SHA-256 exactly as <c>contracts/tools/build_fixtures.py</c> computes it:
        /// over the UTF-8 bytes of the LF-normalized text.
        ///
        /// The generator hashes the string it is about to write, then writes it
        /// through Python's text layer; on a Windows checkout (<c>* text=auto</c>
        /// in .gitattributes) the file lands with CRLF, so hashing the raw bytes
        /// off disk would mismatch every entry. The agent's contract suite reads
        /// with universal newlines for the same reason
        /// (<c>ai_agent/tests/contracts/conftest.py::sha256_file</c>).
        /// </summary>
        public static string Sha256OfNormalizedText(string text)
        {
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ResolveRoot()
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "Contracts", "Fixtures");
            if (Directory.Exists(candidate)) return candidate;

            throw new DirectoryNotFoundException(
                $"Protocol fixtures were not copied to the test output directory ('{candidate}'). " +
                "Expected the <None Include=\"Contracts\\Fixtures\\**\\*\" CopyToOutputDirectory=\"PreserveNewest\" /> " +
                "item in MahodAI.Civil3D.Plugin.Tests.csproj to place them there.");
        }

        private static IReadOnlyList<ProtocolFixture> LoadAll()
        {
            var files = Directory
                .GetFiles(Root, "*.json", SearchOption.AllDirectories)
                .Where(p => !string.Equals(Path.GetFileName(p), "manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            return files.Select(f => ProtocolFixture.Load(Root, f)).ToList();
        }

        private static JsonDocument LoadManifestDocument() =>
            JsonDocument.Parse(File.ReadAllText(ManifestPath));

        private static IReadOnlyList<ManifestEntry> LoadManifestEntries()
        {
            var entries = new List<ManifestEntry>();
            foreach (var e in ManifestLazy.Value.RootElement.GetProperty("fixtures").EnumerateArray())
            {
                entries.Add(new ManifestEntry
                {
                    File = e.GetProperty("file").GetString()!.Replace('\\', '/'),
                    Sha256 = e.GetProperty("sha256").GetString()!,
                    Type = e.GetProperty("type").GetString()!,
                    Direction = e.GetProperty("direction").GetString()!,
                    Audience = e.TryGetProperty("audience", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                        : Array.Empty<string>()
                });
            }
            return entries;
        }
    }
}
