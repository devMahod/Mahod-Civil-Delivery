using System;
using System.IO;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Proposed plugin tests: not executed by CODEX_TimestampProbe (which tests the core resolver and adapter only).
public sealed class EstimateScanTimestampTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OriginalMeasurementTimeSurvivesSerializationAndDecisionRebase(bool known)
    {
        DateTime? at = known ? new DateTime(2026, 9, 1, 7, 12, 43, DateTimeKind.Utc) : null;
        var testRoot = Path.Combine(Path.GetTempPath(), "mcd-scan-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var profile = EstimateFixtures.Profile();
            var saved = ProfileCasTest.Save(profile, Path.Combine(testRoot, "profile.yaml"), "synthetic timestamp test", "test");
            var scan = EstimateWorkflowService.AssembleScan("timestamp-test", profile.ProfileId,
                @"C:\local\host.dwg", new string('b', 64), Array.Empty<NeutralQuantityRecord>(),
                Array.Empty<DeliveryFinding>(), 0, true, scannedAtUtc: at);
            Assert.Equal(at, scan.ScannedAtUtc);
            var json = JsonSerializer.Serialize(scan, SectionsWorkflowService.Json);
            Assert.Equal(known, json.Contains("\"ScannedAtUtc\"", StringComparison.Ordinal));
            var reopened = JsonSerializer.Deserialize<EstimateWorkflowService.ScanResult>(json, SectionsWorkflowService.Json)!;
            Assert.Equal(at, reopened.ScannedAtUtc);
            if (known) Assert.Equal(DateTimeKind.Utc, reopened.ScannedAtUtc!.Value.Kind);
            var rebased = EstimateWorkflowService.RebaseAfterProfileDecision(reopened, profile, saved);
            Assert.Equal(at, rebased.ScannedAtUtc);
            Assert.Empty(rebased.Records);
        }
        finally { Directory.Delete(testRoot, recursive: true); }
    }
}
