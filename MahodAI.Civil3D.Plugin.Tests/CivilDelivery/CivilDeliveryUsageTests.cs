using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.Utilities;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Mahod Impact usage records (1.4.2): the recorder is the MahodAI plugin's, byte for byte; a priced unit is one
/// section read back Verified, keyed exactly as MahodAI keys it; the records carry no user, PC, drawing name, path,
/// layer or value. Linked into the standalone suite too, so both assemblies are checked. No network: the queue
/// tests point the recorder at a temporary folder with uploading off.
/// </summary>
public sealed class CivilDeliveryUsageTests
{
    /// <summary>
    /// SHA-256 of MahodAI.Civil3D.Plugin/Utilities/MahodUsage.cs in the MahodAI plugin at a259bed (release 1.8.3), LF
    /// line endings — the same bytes devMahod/MahodCulvert 1.4.9 carries. Change the recorder there first, then copy.
    /// </summary>
    private const string PluginRecorderSha256 = "ba8b236657dea66d543d824d3801a50a3bb742362baeec03ecdf12764b0d3995";

    private static string Source(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { TestPaths.PluginSourceDir }.Concat(relative).ToArray()));

    [Fact]
    public void TheRecorderIsTheMahodAiPluginsByteForByte()
    {
        var text = Source("Utilities", "MahodUsage.cs").Replace("\r\n", "\n");
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
            .Should().Be(PluginRecorderSha256);
    }

    [Fact]
    public void AUnitIsOneSectionReadBackVerified_KeyedByDrawingAndLogicalKey()
    {
        var result = new SectionVerifyResult
        {
            RunId = "sections-verify-test",
            Records =
            {
                new SectionVerifyRecordResult { RecordId = "rec-1", LogicalKey = "CL|STA-42676", Status = DeliveryStatus.Verified },
                new SectionVerifyRecordResult { RecordId = "rec-2", LogicalKey = "CL|STA-42700", Status = DeliveryStatus.Failed },
                new SectionVerifyRecordResult { RecordId = "rec-3", LogicalKey = null, Status = DeliveryStatus.Verified },
                new SectionVerifyRecordResult { RecordId = "rec-4", LogicalKey = "", Status = DeliveryStatus.Applied },
            },
        };

        CivilDeliveryUsage.UnitKeys("{FP}", result).Should().Equal("{FP}|section:CL|STA-42676", "{FP}|section:rec-3");
    }

    [Fact]
    public void TheCatalogNamesMatchWhatMahodAiRecordsAndImpactPrices()
    {
        CivilDeliveryUsage.Tool.Should().Be("civildelivery");
        CivilDeliveryUsage.Feature.Should().Be("section_delivery");
        var actions = new[]
        {
            CivilDeliveryUsage.PlanAction, CivilDeliveryUsage.PreviewAction, CivilDeliveryUsage.ApplyAction,
            CivilDeliveryUsage.VerifyAction, CivilDeliveryUsage.EstimateAction, CivilDeliveryUsage.OpenAction,
            CivilDeliveryUsage.SetupAction, CivilDeliveryUsage.SectionsAction,
        };
        actions.Should().Equal("civildelivery_plan", "civildelivery_preview", "civildelivery_apply", "civildelivery_verify",
            "civildelivery_estimate", "civildelivery_open", "civildelivery_setup", "civildelivery_sections");
        actions.Should().OnlyContain(a => System.Text.RegularExpressions.Regex.IsMatch(a, "^[a-z][a-z0-9_]{0,39}$"));
        actions.Should().OnlyContain(a => MahodUsage.Name(a) == a);
    }

    [Fact]
    public void TheWorkKeyIsAOneWayHashOfToolAndUnitKey_TheKeyItselfIsNeverSent()
    {
        const string unitKey = "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}|section:CL|STA-42676";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("civildelivery|" + unitKey)))
            .Substring(0, 32).ToLowerInvariant();
        var workKey = MahodUsage.WorkKey(CivilDeliveryUsage.Tool, unitKey);
        workKey.Should().Be(expected).And.MatchRegex("^[0-9a-f]{32}$");

        var unit = MahodUsage.UnitEvent(CivilDeliveryUsage.Tool, CivilDeliveryUsage.Feature, workKey,
            MahodUsage.Palette, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), "1.4.2");
        var json = unit.ToJsonString();
        json.Should().NotContain("STA-42676").And.NotContain("3F2504E0");
        unit.Select(p => p.Key).Should().BeEquivalentTo("id", "tool", "k", "n", "t", "p");
        ((JsonObject)unit["p"]!).Select(p => p.Key).Should().BeEquivalentTo("door", "w", "v");
        unit["tool"]!.GetValue<string>().Should().Be("civildelivery");
        unit["k"]!.GetValue<string>().Should().Be("cad_unit");
        unit["n"]!.GetValue<string>().Should().Be("section_delivery");
    }

    [Fact]
    public void AnActionCarriesWhatRanHowLongAndTheOutcome_NothingAboutWhoOrWhere()
    {
        var action = MahodUsage.ActionEvent(CivilDeliveryUsage.Tool, CivilDeliveryUsage.PlanAction, MahodUsage.Completed,
            1234, MahodUsage.Standalone, 0, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), "1.4.2");
        action.Select(p => p.Key).Should().BeEquivalentTo("id", "tool", "k", "n", "s", "ms", "t", "p");
        ((JsonObject)action["p"]!).Select(p => p.Key).Should().BeEquivalentTo("door", "units", "v");
        action["ms"]!.GetValue<long>().Should().Be(1234);
        action["s"]!.GetValue<string>().Should().Be("completed");

        var batch = MahodUsage.Batches(new[] { MahodUsage.Line(action, "session-1") }).Single();
        var body = JsonNode.Parse(batch.Body)!.AsObject();
        body.Select(p => p.Key).Should().BeEquivalentTo("v", "sid", "events");
        foreach (var who in new[] { Environment.UserName, Environment.MachineName }.Where(w => w.Length >= 5))
            body.ToJsonString().Should().NotContain(who);
    }

    [Fact]
    public void TheInstallationKeyWinsOverTheProductKey_AndNoKeyMeansTheQueueWaits()
    {
        MahodUsage.Credential("install-1", "{\"installKey\":\"cfg\"}", "product")
            .Should().Be((MahodUsage.InstallKeyHeader, "install-1"));
        MahodUsage.Credential(null, "{\"installKey\":\"cfg\"}", "product")
            .Should().Be((MahodUsage.InstallKeyHeader, "cfg"));
        MahodUsage.Credential(null, "not json", "product")
            .Should().Be((MahodUsage.ProductKeyHeader, "product"));
        MahodUsage.Credential(null, null, null).Should().BeNull();
    }

    [Fact]
    public void AStepIsTimedAndCompleted_AStepThatNeverRecordsIsSentAsFailed()
    {
        var name = "civildelivery_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var lines = WithTemporaryQueue(() =>
        {
            using (CivilDeliveryUsage.Begin(name))
            {
                System.Threading.Thread.Sleep(30);
                CivilDeliveryUsage.Step(name, ok: true);
            }
            using (CivilDeliveryUsage.Begin(name))
            {
                // returned or threw without its record
            }
            var outer = MahodUsage.AmbientDoor;
            MahodUsage.AmbientDoor = MahodUsage.Standalone;
            try
            {
                using (CivilDeliveryUsage.Begin(name))
                    CivilDeliveryUsage.Step(name, ok: true);
            }
            finally
            {
                MahodUsage.AmbientDoor = outer;
            }
        });

        var events = lines.Select(l => JsonNode.Parse(l)!["ev"]!.AsObject())
            .Where(e => e["n"]!.GetValue<string>() == name).ToList();
        events.Should().HaveCount(3);
        events.Should().OnlyContain(e => e["tool"]!.GetValue<string>() == "civildelivery" && e["k"]!.GetValue<string>() == "cad_action");
        events[0]["s"]!.GetValue<string>().Should().Be("completed");
        events[0]["ms"]!.GetValue<long>().Should().BeGreaterThanOrEqualTo(20);
        events[0]["p"]!["door"]!.GetValue<string>().Should().Be("palette");
        events[1]["s"]!.GetValue<string>().Should().Be("failed");
        events[2]["p"]!["door"]!.GetValue<string>().Should().Be("standalone");
    }

    [Fact]
    public void UnderTheChatDoorAStepRecordsNoAction()
    {
        var name = "civildelivery_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var lines = WithTemporaryQueue(() =>
        {
            var outer = MahodUsage.AmbientDoor;
            MahodUsage.AmbientDoor = MahodUsage.Chat;
            try
            {
                using (CivilDeliveryUsage.Begin(name))
                    CivilDeliveryUsage.Step(name, ok: true);
                using (CivilDeliveryUsage.Begin(name)) { }
            }
            finally
            {
                MahodUsage.AmbientDoor = outer;
            }
        });
        lines.Should().NotContain(l => l.Contains(name));
    }

    [Fact]
    public void EveryWorkflowStepIsHooked_AndTheUnitIsRecordedWhereEveryVerificationIsPersisted()
    {
        var workflow = Source("CivilDelivery", "Sections", "Services", "SectionsWorkflowService.cs");
        Count(workflow, "CivilDeliveryUsage.Begin(CivilDeliveryUsage.PlanAction)").Should().Be(1);
        Count(workflow, "CivilDeliveryUsage.Begin(CivilDeliveryUsage.PreviewAction)").Should().Be(1);
        Count(workflow, "CivilDeliveryUsage.Begin(CivilDeliveryUsage.ApplyAction)").Should().Be(3, "Apply, ApplySelected, RebuildSelected");
        Count(workflow, "CivilDeliveryUsage.Begin(CivilDeliveryUsage.VerifyAction)").Should().Be(3, "Verify, VerifySelected, VerifySelectedCurrent");
        workflow.Should().Contain("CivilDeliveryUsage.Step(CivilDeliveryUsage.PlanAction, ok: true);")
            .And.Contain("CivilDeliveryUsage.Step(CivilDeliveryUsage.PreviewAction, ok: true);");

        var applyEvidence = Body(workflow, "internal static bool PersistApplyEvidence(", "internal static bool PersistVerifyEvidence(");
        applyEvidence.Should().Contain("CivilDeliveryUsage.Step(CivilDeliveryUsage.ApplyAction, result.Committed);");
        var verifyEvidence = Body(workflow, "internal static bool PersistVerifyEvidence(", "internal static void PersistEvidenceBundle<T>(");
        verifyEvidence.Should().Contain("CivilDeliveryUsage.Verified(doc, result);");
        Count(workflow, "CivilDeliveryUsage.Verified(").Should().Be(1, "one place prices a section");

        var estimate = Source("CivilDelivery", "Estimate", "EstimateWorkflowService.cs");
        var scan = Body(estimate, "public ScanResult Scan(", "internal ScanResult ScanUnpublished(");
        scan.Should().Contain("CivilDeliveryUsage.Begin(CivilDeliveryUsage.EstimateAction)")
            .And.Contain("CivilDeliveryUsage.Step(CivilDeliveryUsage.EstimateAction, ok: true);");
    }

    [Fact]
    public void TheDoorIsNeverDecidedByAssemblyName()
    {
        // This repository's MahodAI fork is named MahodAI.Civil3D.Plugin like the real MahodAI plugin.
        var usage = Source("CivilDelivery", "Sections", "Services", "CivilDeliveryUsage.cs");
        usage.Should().NotContain("GetName()").And.NotContain("MahodAI.Civil3D.Plugin\"");
    }

    private static string Body(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, end);
        return text.Substring(from, to - from);
    }

    private static int Count(string text, string what)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
        return n;
    }

    /// <summary>Runs <paramref name="act"/> with the recorder writing to a fresh folder and never uploading; returns the queued lines.</summary>
    private static string[] WithTemporaryQueue(Action act)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mahod-cd-usage-test-" + Guid.NewGuid().ToString("N"));
        var queue = MahodUsage.QueueDir;
        var upload = MahodUsage.UploadEnabled;
        var off = Environment.GetEnvironmentVariable("MAHOD_USAGE_OFF");
        try
        {
            MahodUsage.QueueDir = dir;
            MahodUsage.UploadEnabled = false;
            Environment.SetEnvironmentVariable("MAHOD_USAGE_OFF", null);
            act();
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "q-*.jsonl").SelectMany(File.ReadAllLines).Where(l => l.Length > 0).ToArray()
                : Array.Empty<string>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAHOD_USAGE_OFF", off);
            MahodUsage.UploadEnabled = upload;
            MahodUsage.QueueDir = queue;
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
