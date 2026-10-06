using System;
using System.Collections.Generic;
using System.Text.Json;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Evidence;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

// Pure status tests only. No Autodesk object/getter or native transaction is simulated.
public sealed class LegendReadCompletenessTests
{
    private static Dictionary<string, string> Style() => new()
    {
        [EvidenceKeys.ColorEffective] = EffectiveColorPolicy.AciToRgbText(1)!,
        [EvidenceKeys.ColorEffective + EvidenceKeys.StatusSuffix] = "read",
        [EvidenceKeys.EntityLinetype] = "DASHED",
    };

    [Fact]
    public void CompleteReadKeepsBothPositiveMatchAndRealAbsence()
    {
        var rows = new[] { new LegendEntry("WATER", 1, "dashed", "A1", "MIKRA", true) };
        var read = LegendEvidence.ForRecord(rows, null, Style());
        Assert.Equal("read", read.Status);
        Assert.Equal(read, LegendEvidence.ApplyReadCompleteness(read, complete: true));
        var absent = LegendEvidence.ForRecord(Array.Empty<LegendEntry>(), null, Style());
        Assert.Equal("absent", absent.Status);
        Assert.Equal(absent, LegendEvidence.ApplyReadCompleteness(absent, complete: true));
    }

    [Fact]
    public void FailedOnlyCaptionCannotBecomeAbsenceAndSpecificFailureSurvives()
    {
        // Adapter must supply complete:false after the real getter catch; this test does not run that catch.
        var noCaption = LegendEvidence.ForRecord(Array.Empty<LegendEntry>(), null, Style());
        var value = LegendEvidence.ApplyReadCompleteness(noCaption, complete: false);
        Assert.Equal("unavailable:legend-read-incomplete", value.Status);
        Assert.Null(value.Json);
        var prior = EvidenceValue.Unavailable("legend-time-budget");
        Assert.Equal(prior, LegendEvidence.ApplyReadCompleteness(prior, complete: false));
    }

    [Fact]
    public void UnrelatedReadableCaptionsRemainIntactButCannotMasqueradeAsACompleteRead()
    {
        // The reader still returns this readable row beside an unread different caption.
        var rows = new List<LegendEntry> { new("WATER", 1, "dashed", "A1", "MIKRA", true) };
        var parameters = Style();
        var before = JsonSerializer.Serialize(new { rows, parameters });
        var read = LegendEvidence.ForRecord(rows, null, parameters);
        Assert.Equal("read", read.Status);
        Assert.Contains("WATER", read.Json!);
        Assert.Equal("unavailable:legend-read-incomplete",
            LegendEvidence.ApplyReadCompleteness(read, complete: false).Status);
        Assert.Equal("unavailable:legend-read-incomplete",
            LegendEvidence.ApplyReadCompleteness(EvidenceValue.Truncated(read.Json!, 9), complete: false).Status);
        Assert.Equal(before, JsonSerializer.Serialize(new { rows, parameters }));
        Assert.Equal(read, LegendEvidence.ForRecord(rows, null, parameters));
    }
}
