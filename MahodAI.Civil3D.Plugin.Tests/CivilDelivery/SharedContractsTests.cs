using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class DeliveryStatusTests
    {
        [Fact]
        public void Aggregate_Empty_IsDiscovered()
        {
            DeliveryStatusRules.Aggregate(Array.Empty<DeliveryStatus>())
                .Should().Be(DeliveryStatus.Discovered);
        }

        [Fact]
        public void Aggregate_ProblemStatesDominateProgress()
        {
            DeliveryStatusRules.Aggregate(new[]
            {
                DeliveryStatus.Verified, DeliveryStatus.Failed, DeliveryStatus.Ready,
            }).Should().Be(DeliveryStatus.Failed);

            DeliveryStatusRules.Aggregate(new[]
            {
                DeliveryStatus.Verified, DeliveryStatus.Blocked,
            }).Should().Be(DeliveryStatus.Blocked);

            DeliveryStatusRules.Aggregate(new[]
            {
                DeliveryStatus.Applied, DeliveryStatus.ReviewRequired, DeliveryStatus.Warning,
            }).Should().Be(DeliveryStatus.ReviewRequired);
        }

        [Fact]
        public void Aggregate_ProgressOnly_LeastAdvancedGoverns()
        {
            DeliveryStatusRules.Aggregate(new[]
            {
                DeliveryStatus.Verified, DeliveryStatus.Applied, DeliveryStatus.Ready,
            }).Should().Be(DeliveryStatus.Ready, "a batch is only as done as its least-done required record");
        }

        [Fact]
        public void CapByFindings_ErrorForcesFailed_ReviewForcesReview()
        {
            var error = new DeliveryFinding
            { Code = "X", Domain = "shared", Severity = FindingSeverity.Error, Title = "t" };
            var review = new DeliveryFinding
            { Code = "X", Domain = "shared", Severity = FindingSeverity.ReviewRequired, Title = "t" };
            var warn = new DeliveryFinding
            { Code = "X", Domain = "shared", Severity = FindingSeverity.Warning, Title = "t" };

            DeliveryStatusRules.CapByFindings(DeliveryStatus.Ready, new[] { error })
                .Should().Be(DeliveryStatus.Failed);
            DeliveryStatusRules.CapByFindings(DeliveryStatus.Ready, new[] { review })
                .Should().Be(DeliveryStatus.ReviewRequired);
            DeliveryStatusRules.CapByFindings(DeliveryStatus.Ready, new[] { warn })
                .Should().Be(DeliveryStatus.Ready, "a warning annotates but never silently changes the result");
        }
    }

    public class OwnershipMetadataTests
    {
        private static OwnershipMetadata Sample() => new()
        {
            Feature = "sections",
            Role = "section-view",
            ProjectProfileId = "6422",
            RunId = "run-1",
            SourceClDrawingHash = "abcd",
            SourceClHandle = "1F2A",
            LogicalKey = "MCD:0011223344556677",
            InputFingerprint = "ff00",
            CreatedByToolVersion = "1.0-test",
        };

        [Fact]
        public void Pairs_RoundTrip_PreservesIdentity()
        {
            var meta = Sample();
            var restored = OwnershipMetadata.FromPairs(meta.ToPairs());

            restored.Should().NotBeNull();
            restored!.LogicalKey.Should().Be(meta.LogicalKey);
            restored.Feature.Should().Be("sections");
            restored.ProjectProfileId.Should().Be("6422");
            restored.SourceClHandle.Should().Be("1F2A");
            restored.InputFingerprint.Should().Be("ff00");
        }

        [Fact]
        public void FromPairs_ForeignOwner_IsNotOurs()
        {
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("owner", "SomeOtherTool"),
                new("logical_key", "X"),
            };
            OwnershipMetadata.FromPairs(pairs).Should().BeNull(
                "objects not positively identified as tool-owned must never be treated as ours");
        }

        [Theory]
        [InlineData("schema_version", "99")]
        [InlineData("feature", "")]
        [InlineData("role", "   ")]
        [InlineData("created_at_utc", "not-a-timestamp")]
        public void FromPairs_OurMalformedRecord_FailsClosed(string field, string value)
        {
            var pairs = Sample().ToPairs().ToList();
            var index = pairs.FindIndex(pair =>
                string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase));
            pairs[index] = new KeyValuePair<string, string>(field, value);

            var act = () => OwnershipMetadata.FromPairs(pairs);
            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void FromPairs_OurIncompleteOrDuplicateRecord_FailsClosed()
        {
            var incomplete = Sample().ToPairs()
                .Where(pair => pair.Key != "input_fingerprint")
                .ToList();
            var duplicate = Sample().ToPairs().Concat(new[]
            {
                new KeyValuePair<string, string>("logical_key", "another"),
            }).ToList();

            ((Action)(() => OwnershipMetadata.FromPairs(incomplete)))
                .Should().Throw<InvalidOperationException>();
            ((Action)(() => OwnershipMetadata.FromPairs(duplicate)))
                .Should().Throw<ArgumentException>();
        }

        [Fact]
        public void LogicalKey_StableAndCaseNormalized()
        {
            var k1 = LogicalKeys.ForSectionObject("6422", "AABB", "1f2a", "Main", "section-view");
            var k2 = LogicalKeys.ForSectionObject("6422", "aabb", "1F2A", "Main", "SECTION-VIEW");
            var k3 = LogicalKeys.ForSectionObject("6422", "aabb", "1F2B", "Main", "section-view");
            var afterOrdinaryClSave = LogicalKeys.ForSectionObject(
                "6422", "COMPLETELY-DIFFERENT-FILE-SHA", "1F2A", "Main", "section-view");

            k1.Should().Be(k2, "handle and role case are normalized");
            k1.Should().Be(afterOrdinaryClSave,
                "saving the same CL drawing changes its content hash, not the identity of every section");
            k1.Should().NotBe(k3, "a different source handle is a different object");
            k1.Should().StartWith("MCD:");
        }

        [Fact]
        public void Fingerprint_OrderIndependent_CultureStable()
        {
            var f1 = LogicalKeys.Fingerprint(new Dictionary<string, object?>
            { ["b"] = 1.5, ["a"] = "x" });
            var f2 = LogicalKeys.Fingerprint(new Dictionary<string, object?>
            { ["a"] = "x", ["b"] = 1.5 });
            f1.Should().Be(f2);
        }
    }

    public class DeliveryFindingSerializationTests
    {
        [Fact]
        public void Finding_SerializesWithSnakeCaseContract()
        {
            var f = new DeliveryFinding
            {
                Code = "SEC-CL-NO-INTERSECTION",
                Domain = "sections",
                Severity = FindingSeverity.ReviewRequired,
                Title = "no intersection",
                ProjectProfileId = "6422",
            };

            var json = JsonSerializer.Serialize(f);
            json.Should().Contain("\"code\":\"SEC-CL-NO-INTERSECTION\"");
            json.Should().Contain("\"severity\":\"ReviewRequired\"");
            json.Should().Contain("\"project_profile_id\":\"6422\"");

            var back = JsonSerializer.Deserialize<DeliveryFinding>(json);
            back!.Severity.Should().Be(FindingSeverity.ReviewRequired);
            back.Code.Should().Be(f.Code);
        }
    }

    public class RunManifestTests
    {
        [Fact]
        public void RunId_EmbedsFeatureOperationAndTime()
        {
            var id = RunManifest.NewRunId("sections", "plan", new DateTime(2026, 8, 18, 10, 30, 0, DateTimeKind.Utc));
            id.Should().StartWith("sections-plan-20260818-103000-");
        }

        [Fact]
        public void Write_ProducesReadableManifest()
        {
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcd-tests", Guid.NewGuid().ToString("N"));
            var manifest = new RunManifest
            {
                RunId = "sections-plan-test-1",
                Feature = "sections",
                Operation = "plan",
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                PluginGitSha = "7df13b589764a2e5ae8bfc495e0df90a3b63d390",
                PluginBuildVersion = "1.3.4.0",
                PluginPackageRevision = "1.2.14",
                PluginAssemblySha256 = new string('a', 64),
                Scope = "selected-record",
                SelectedRecordId = "cl-7D08",
                ResultStatus = DeliveryStatus.Ready,
            };
            manifest.RecordCounts["cl_records"] = 3;
            manifest.CountFinding(new DeliveryFinding
            { Code = "SEC-ALIGNMENT-AMBIGUOUS", Domain = "sections", Severity = FindingSeverity.ReviewRequired, Title = "x" });

            var path = RunManifestWriter.Write(manifest, tmp);

            var text = System.IO.File.ReadAllText(path);
            text.Should().Contain("\"run_id\": \"sections-plan-test-1\"");
            text.Should().Contain("\"SEC-ALIGNMENT-AMBIGUOUS\": 1");
            text.Should().Contain("\"plugin_build_version\": \"1.3.4.0\"");
            text.Should().Contain("\"plugin_package_revision\": \"1.2.14\"");
            text.Should().Contain("\"plugin_assembly_sha256\": \"" + new string('a', 64) + "\"");
            text.Should().Contain("\"scope\": \"selected-record\"");
            text.Should().Contain("\"selected_record_id\": \"cl-7D08\"");
            System.IO.Directory.Delete(tmp, recursive: true);
        }

        [Fact]
        public void RuntimeIdentity_UsesAssemblyMetadataAndEnclosingBundleManifest()
        {
            var tmp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mcd-identity-tests", Guid.NewGuid().ToString("N"));
            var contents = System.IO.Path.Combine(tmp, "MahodAI.bundle", "Contents", "2026");
            System.IO.Directory.CreateDirectory(contents);
            var dll = System.IO.Path.Combine(contents, "MahodAI.Civil3D.Plugin.dll");
            System.IO.File.WriteAllBytes(dll, new byte[] { 1, 3, 4, 14 });
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(tmp, "MahodAI.bundle", "PackageContents.xml"),
                "<ApplicationPackage AppVersion=\"1.2.14\" />");

            try
            {
                var sha = "7df13b589764a2e5ae8bfc495e0df90a3b63d390";
                var identity = PluginRuntimeIdentityResolver.Resolve(
                    "1.3.4.0", "1.3.4+" + sha, dll);

                identity.BuildVersion.Should().Be("1.3.4.0");
                identity.GitSha.Should().Be(sha);
                identity.PackageRevision.Should().Be("1.2.14");
                identity.AssemblySha256.Should().Be(ArtifactHash.Sha256OfFile(dll));
            }
            finally
            {
                System.IO.Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void RuntimeIdentity_InAVersionedBundleNamesTheLoadedVersionNotTheNextOne()
        {
            // The updater staged 1.6.5 and rewrote the root manifest while 1.6.4 is loaded (int-b review, 28.09).
            var tmp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mcd-identity-tests", Guid.NewGuid().ToString("N"));
            var bundle = System.IO.Path.Combine(tmp, "MahodAI.bundle");
            var loaded = System.IO.Path.Combine(bundle, "Contents", "1.6.4", "2027");
            System.IO.Directory.CreateDirectory(loaded);
            var dll = System.IO.Path.Combine(loaded, "MahodAI.Civil3D.Plugin.dll");
            System.IO.File.WriteAllBytes(dll, new byte[] { 1, 6, 4 });
            System.IO.File.WriteAllText(System.IO.Path.Combine(bundle, "PackageContents.xml"),
                "<ApplicationPackage AppVersion=\"1.6.5\" />");
            try
            {
                PluginRuntimeIdentityResolver.Resolve("1.6.4.0", "1.6.4", dll).PackageRevision
                    .Should().Be("1.6.4", "without a stash the loaded folder names the release");
                System.IO.File.WriteAllText(System.IO.Path.Combine(bundle, "Contents", "1.6.4", "_PackageContents.xml"),
                    "<ApplicationPackage AppVersion=\"1.6.4\" />");
                PluginRuntimeIdentityResolver.Resolve("1.6.4.0", "1.6.4", dll).PackageRevision.Should().Be("1.6.4");
            }
            finally
            {
                System.IO.Directory.Delete(tmp, recursive: true);
            }
        }

        [Fact]
        public void RuntimeIdentity_FallsBackToInformationalVersionWithoutInventingGitSha()
        {
            var identity = PluginRuntimeIdentityResolver.Resolve(
                null, "2.7.1+not-a-source-revision", null);

            identity.BuildVersion.Should().Be("2.7.1");
            identity.GitSha.Should().BeNull();
            identity.PackageRevision.Should().BeNull();
            identity.AssemblySha256.Should().BeNull();
        }
    }
}
