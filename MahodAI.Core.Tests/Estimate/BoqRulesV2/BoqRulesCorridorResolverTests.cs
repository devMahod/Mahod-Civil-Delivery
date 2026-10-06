using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Estimate.CorridorBoq;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// The corridor source of the combined bill, on a private temporary runs root (never the user's run store): the latest
/// published corridor measurement run of the profile, taken only when its measures file, rules identity, raw receipt and
/// drawing all verify; a refused latest run throws before export and is never replaced by an older one.
/// </summary>
public sealed class BoqRulesCorridorResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boq-corridor-runs-" + Guid.NewGuid().ToString("N"));
    private readonly CorridorBoqRuleset _rules = CorridorBoqRuleset.LoadEmbedded6422();
    private readonly string _sha = CorridorBoqMeasuresFile.EmbeddedRulesetSha256();
    private readonly string _drawing;

    public BoqRulesCorridorResolverTests()
    {
        Directory.CreateDirectory(Runs);
        Directory.CreateDirectory(Path.Combine(_root, "drawings"));
        _drawing = Path.Combine(_root, "drawings", "6422-CIVIL-WEST-2026-08-04-UT.dwg");
        File.WriteAllText(_drawing, "civil west");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Runs => Path.Combine(_root, "runs");

    private const string Raw = "corridor_boq_raw_inputs.json";

    /// <summary>A published corridor run: the r9 fixture measures re-headed for this drawing, raw receipt and rules.</summary>
    private string Publish(string runId, DateTime completed, string profile = "6422", bool measures = true, string? rulesSha = null,
        DeliveryStatus status = DeliveryStatus.Blocked, string? headerProfile = "use-manifest")
    {
        var dir = Path.Combine(Runs, runId);
        Directory.CreateDirectory(dir);
        var rawPath = Path.Combine(dir, Raw);
        File.WriteAllText(rawPath, "{\"schema\":\"mahod-corridor-boq-raw/1\",\"run\":\"" + runId + "\"}");
        var drawingHash = ArtifactHash.Sha256OfFile(_drawing);
        var manifest = new RunManifest
        {
            RunId = runId, Feature = "estimate", Operation = BoqRulesSourceResolver.CorridorOperation, CompletedAtUtc = completed,
            ProjectProfileId = profile, ResultStatus = status,
            InputDrawings = { _drawing },
            InputHashesByPath = { [_drawing] = drawingHash },
            ArtifactHashes = { [rawPath] = ArtifactHash.Sha256OfFile(rawPath) },
        };
        if (measures)
        {
            var node = JsonNode.Parse(File.ReadAllText(BoqRulesV2GoldenAcceptanceTests.FixturePath("corridor_measures_r9_west.json")))!;
            var header = node["Header"]!;
            header["RulesetSha256"] = rulesSha ?? _sha;
            header["DrawingPath"] = _drawing;
            header["DrawingSha256"] = drawingHash;
            header["RawReceiptFile"] = Raw;
            header["RawReceiptSha256"] = ArtifactHash.Sha256OfFile(rawPath);
            header["ProfileId"] = headerProfile == "use-manifest" ? profile : headerProfile;
            var measuresPath = Path.Combine(dir, CorridorBoqMeasuresFile.FileName);
            File.WriteAllText(measuresPath, node.ToJsonString());
            manifest.ArtifactHashes[measuresPath] = ArtifactHash.Sha256OfFile(measuresPath);
        }
        File.WriteAllText(Path.Combine(dir, "run_manifest.json"), JsonSerializer.Serialize(manifest, RunManifestWriter.JsonOptions));
        return dir;
    }

    private BoqRulesSourceResolver.CorridorResolution Resolve(string profile = "6422") =>
        BoqRulesSourceResolver.ResolveCorridor(Runs, profile, _rules, _sha);

    [Fact]
    public void TakesTheLatestVerifiedRunOfTheProfile()
    {
        Publish("estimate-corridor-boq-old", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));
        var latest = Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc));
        Publish("estimate-corridor-boq-other-profile", new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc), profile: "7000");
        Directory.CreateDirectory(Path.Combine(Runs, ".pending-x"));

        var resolution = Resolve();
        resolution.Input.Should().NotBeNull();
        resolution.Input!.RunId.Should().Be("estimate-corridor-boq-new");
        resolution.Input.RulesetSha256.Should().Be(_sha);
        resolution.Input.Context.RawReceiptPath.Should().Be(Path.Combine(latest, Raw));
        resolution.Input.Export.Measures.Should().HaveCount(2);
        resolution.Evidence.Select(e => e.Path).Should().Equal(_drawing, Path.Combine(latest, CorridorBoqMeasuresFile.FileName), Path.Combine(latest, Raw));
        resolution.Input.MeasuresSha256.Should().Be(ArtifactHash.Sha256OfFile(Path.Combine(latest, CorridorBoqMeasuresFile.FileName)));
        resolution.Notes.Should().ContainSingle().Which.Should().Contain("estimate-corridor-boq-new");
    }

    [Fact]
    public void WithoutARunOfTheProfileThereAreNoCorridorChapters()
    {
        Publish("estimate-corridor-boq-other-profile", new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc), profile: "7000");
        var resolution = Resolve();
        resolution.Input.Should().BeNull();
        resolution.Evidence.Should().BeEmpty();
        resolution.Notes.Should().ContainSingle().Which.Should().Contain("לא נמצאה מדידת קורידורים");
        BoqRulesSourceResolver.ResolveCorridor(Runs, null, _rules, _sha).Input.Should().BeNull("a scan without a profile has no corridor run");
    }

    [Fact]
    public void ALatestRunWithoutAMeasuresFileIsReportedNotReplacedByAnOlderRun()
    {
        Publish("estimate-corridor-boq-old", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));
        Publish("estimate-corridor-boq-r9", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc), measures: false);
        var exception = Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve());
        exception.Message.Should().Contain("estimate-corridor-boq-r9").And.Contain("לא שמרה קובץ מדידה");
    }

    [Fact]
    public void AChangedDrawingIsRefused()
    {
        Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(_drawing, "civil west, edited after the measurement");
        var exception = Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve());
        exception.Message.Should().Contain("השתנה מאז המדידה");

        File.Delete(_drawing);
        Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve())
            .Message.Should().Contain("אינו נגיש כעת");
    }

    [Fact]
    public void AChangedMeasuresFileOrRawReceiptIsRefused()
    {
        var dir = Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc));
        File.AppendAllText(Path.Combine(dir, Raw), " ");
        Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve())
            .Message.Should().Contain("נתוני הגלם");

        dir = Publish("estimate-corridor-boq-newer", new DateTime(2026, 9, 30, 16, 0, 0, DateTimeKind.Utc));
        File.AppendAllText(Path.Combine(dir, CorridorBoqMeasuresFile.FileName), " ");
        Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve())
            .Message.Should().Contain("estimate-corridor-boq-newer").And.Contain("השתנה מאז שפורסם");
    }

    [Theory]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Discovered)]
    [InlineData((DeliveryStatus)999)]
    public void ARunThatIsNotAPublishedDraftIsRefused(DeliveryStatus status)
    {
        Publish("estimate-corridor-boq-old", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc));
        Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc), status: status);
        var exception = Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve());
        exception.Message.Should().Contain("estimate-corridor-boq-new").And.Contain("מצב הריצה");
    }

    [Fact]
    public void BothPublishedDraftStatesAreTaken()
    {
        Publish("estimate-corridor-boq-partial", new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), status: DeliveryStatus.Blocked);
        Resolve().Input!.RunId.Should().Be("estimate-corridor-boq-partial");
        Publish("estimate-corridor-boq-complete", new DateTime(2026, 9, 30, 11, 0, 0, DateTimeKind.Utc), status: DeliveryStatus.ReviewRequired);
        Resolve().Input!.RunId.Should().Be("estimate-corridor-boq-complete");
    }

    [Theory]
    [InlineData("7000")]
    [InlineData(null)]
    public void AMeasuresFileOfAnotherProfileIsRefused(string? headerProfile)
    {
        Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc), headerProfile: headerProfile);
        var exception = Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve());
        exception.Message.Should().Contain("שייך לפרופיל");
    }

    [Fact]
    public void AMeasurementUnderOtherCorridorRulesIsRefused()
    {
        Publish("estimate-corridor-boq-new", new DateTime(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc), rulesSha: new string('0', 64));
        var exception = Assert.Throws<BoqRulesSourceResolver.CorridorSourceSelectionException>(() => Resolve());
        exception.Message.Should().Contain("בכללי קורידורים");
    }
}
