using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// The 505 BUS stop group of the int-r native scan (run estimate-extract-20260928-213627-008c6be5), rebuilt from SYNTHETIC
/// records that carry exactly its evidence (Probe505). A supporting citation of a non-subject key needs only an exact public
/// quote of a read value that was sent; a meaningful subject citation stays mandatory; a rejected answer names a fixed class
/// and never the model's text. The prompt states the text rules the validator enforces. No network, Civil or real key.
/// </summary>
public sealed class FamilySupportingCitationContractTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private const string Subject = "505 BUS stop";
    private const string Canary = "CANARYMODELTEXT";
    private const string Explanation = "הבלוק מציין תחנת אוטובוס; השערה לבדיקה בלבד";

    [Fact]
    public void TheRebuiltRequestIsTheProbedRequest()
    {
        var group = Bus505.Group();
        var prepared = FamilyRecognitionAssist.Prepare(group, Library, null);
        prepared.AbstentionMessage.Should().BeNull();
        var request = prepared.Request!;
        request.Evidence.Select(e => (e.Key, e.Status, e.Value, e.Coverage, e.Total)).Should().Equal(
            (FamilyRecognitionAssist.LayerKey, "identifier", "505", 100, 100),
            (EvidenceKeys.BlockAttributes, "absent", "", 0, 100),
            (EvidenceKeys.BlockProps, "read", "Visibility1=505 BUS stop (100%)", 100, 100),
            (EvidenceKeys.PsetComponent, "absent", "", 0, 100),
            (EvidenceKeys.Hatch, "absent", "", 0, 100),
            (EvidenceKeys.LegendRow, "absent", "", 0, 100),
            (EvidenceKeys.NearbyText, "read", "505 (10%) | 506 (10%) | 12 (10%)", 10, 100),
            (EvidenceKeys.ColorEffective, "read", "255,255,255 (100%)", 100, 100),
            (EvidenceKeys.Closed, "absent", "", 0, 100),
            (EvidenceKeys.BlockNameEffective, "read", "505(506) (100%)", 100, 100),
            (EvidenceKeys.EntityLinetype, "read", "ByLayer (100%)", 100, 100),
            (EvidenceKeys.LayerLinetype, "read", "Continuous (100%)", 100, 100),
            (EvidenceKeys.EntityColorIndex, "read", "256 (100%)", 100, 100));
        request.WithheldKeys.Should().BeEmpty();
        request.Candidates.Should().HaveCount(18);
        request.Candidates.Select(c => c.FamilyId).Should().Contain(new[] { "bus-stop-signs", "bus-shelters", "bus-stop-blocks" });
        JsonSerializer.Serialize(request).Should().NotContain("SYNTHETIC-XREF").And.NotContain("SYNHANDLE");
        var local = LocalFamilyClassifier.Instance.Classify(group, Library, null);
        local.Should().ContainSingle().Which.AbstainKind.Should().Be("ambiguous");
        FamilyRecognitionAssist.NeedsAssistance(group.GroupId, local).Should().BeTrue();
    }

    public static TheoryData<string, string> Supports => new()
    {
        { EvidenceKeys.BlockNameEffective, "505(506)" },
        { EvidenceKeys.ColorEffective, "255,255,255" },
        { EvidenceKeys.LayerLinetype, "Continuous" },
        { EvidenceKeys.EntityLinetype, "ByLayer" },
        { EvidenceKeys.EntityColorIndex, "256" },
    };

    [Theory]
    [MemberData(nameof(Supports))]
    public async Task ASubjectCitationWithANonMeaningfulSupportIsOnlyProposed(string key, string quote)
    {
        var proposal = await Ask(r => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockProps, Subject), Cite(key, quote))));

        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.FamilyId.Should().Be("bus-stop-signs");
        proposal.Origin.Should().Be(RecognitionProposal.OriginAi);
        proposal.AssistRejection.Should().BeNull();
        proposal.Inferred.Should().Contain(FamilyRecognitionAssist.ReviewWarning);
        // No price list here: a family hypothesis only, never an item, price or approval.
        proposal.CandidateCodes.Should().BeEmpty();
        proposal.EvidenceRefs.Select(r => r.Key).Should().BeEquivalentTo(new[] { EvidenceKeys.BlockProps, key });
        proposal.EvidenceRefs.Should().OnlyContain(r => r.RecordIds.Count == Bus505.Count);
        FamilyRecognitionAssist.PublicOutcomeCode(proposal).Should().Be("proposal_requires_review");
        FamilyRecognitionAssist.PublicRejectionCode(proposal).Should().BeNull();
    }

    [Fact]
    public async Task TheProbedAnswerShapeWithEverySupportAndAnAlternativeIsOnlyProposed()
    {
        var proposal = await Ask(r => Answer(r,
            Choice("bus-stop-signs", Cite(EvidenceKeys.BlockProps, "Visibility1=505 BUS stop"), Cite(EvidenceKeys.BlockNameEffective, "505(506)"),
                Cite(EvidenceKeys.ColorEffective, "255,255,255"), Cite(EvidenceKeys.LayerLinetype, "Continuous")),
            Choice("bus-shelters", Cite(EvidenceKeys.BlockProps, "BUS stop"))));

        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.FamilyId.Should().Be("bus-stop-signs");
        proposal.Alternatives.Should().ContainSingle().Which.FamilyId.Should().Be("bus-shelters");
        proposal.Observed.Should().Contain(o => o.StartsWith(EvidenceKeys.BlockNameEffective + ": \"505(506)\"", StringComparison.Ordinal) &&
                                                o.Contains(Bus505.Count.ToString(CultureInfo.InvariantCulture) + " מתוך"));
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "support-only-block-name", FamilyRankRejection.NoSubjectCitation },
        { "support-only-colour", FamilyRankRejection.NoSubjectCitation },
        { "support-only-continuous", FamilyRankRejection.NoSubjectCitation },
        { "layer-505-alone", FamilyRankRejection.SupportingCitationRefused },
        { "layer-505-beside-subject", FamilyRankRejection.SupportingCitationRefused },
        { "support-not-verbatim", FamilyRankRejection.SupportingCitationRefused },
        { "support-slash", FamilyRankRejection.SupportingCitationRefused },
        { "support-unsent-key", FamilyRankRejection.SupportingCitationRefused },
        { "support-unsent-text", FamilyRankRejection.SupportingCitationRefused },
        { "subject-case-changed", FamilyRankRejection.SubjectCitationRefused },
        { "subject-with-share-suffix", FamilyRankRejection.SubjectCitationRefused },
        { "subject-tag-only", FamilyRankRejection.SubjectCitationRefused },
        { "subject-number-only", FamilyRankRejection.SubjectCitationRefused },
        { "subject-absent-key", FamilyRankRejection.SubjectCitationRefused },
        { "context", FamilyRankRejection.ContextMismatch },
        { "library-hash", FamilyRankRejection.ContextMismatch },
        { "family-other-basis", FamilyRankRejection.FamilyNotCandidate },
        { "family-invented", FamilyRankRejection.FamilyNotCandidate },
        { "prose-slash", FamilyRankRejection.ProseRefused },
        { "prose-quantity-word", FamilyRankRejection.ProseRefused },
        { "prose-path", FamilyRankRejection.ProseRefused },
        { "prose-alternative-slash", FamilyRankRejection.ProseRefused },
        { "reason-slash", FamilyRankRejection.ReasonRefused },
        { "reason-overlong", FamilyRankRejection.ReasonRefused },
        { "reason-empty", FamilyRankRejection.ReasonMissing },
        { "ranked-and-reason", FamilyRankRejection.RankingMalformed },
        { "four-families", FamilyRankRejection.RankingMalformed },
        { "no-citation", FamilyRankRejection.CitationsMalformed },
        { "null-response", FamilyRankRejection.ResponseMissing },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EveryOtherAnswerIsStillRefusedAndNamesOnlyItsFixedClass(string defect, string expected)
    {
        var subject = Cite(EvidenceKeys.BlockProps, Subject);
        var proposal = await Ask(r => defect switch
        {
            "support-only-block-name" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockNameEffective, "505(506)"))),
            "support-only-colour" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.ColorEffective, "255,255,255"))),
            "support-only-continuous" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.LayerLinetype, "Continuous"))),
            "layer-505-alone" => Answer(r, Choice("bus-stop-signs", Cite(FamilyRecognitionAssist.LayerKey, "505"))),
            "layer-505-beside-subject" => Answer(r, Choice("bus-stop-signs", subject, Cite(FamilyRecognitionAssist.LayerKey, "505"))),
            "support-not-verbatim" => Answer(r, Choice("bus-stop-signs", subject, Cite(EvidenceKeys.BlockNameEffective, "505 (506)"))),
            "support-slash" => Answer(r, Choice("bus-stop-signs", subject, Cite(EvidenceKeys.BlockNameEffective, "505/506"))),
            "support-unsent-key" => Answer(r, Choice("bus-stop-signs", subject, Cite(EvidenceKeys.GeometrySample, "204539"))),
            "support-unsent-text" => Answer(r, Choice("bus-stop-signs", subject, Cite(EvidenceKeys.ColorEffective, "255,0,0"))),
            "subject-case-changed" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockProps, "505 bus stop"))),
            "subject-with-share-suffix" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockProps, "505 BUS stop (100%)"))),
            "subject-tag-only" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockProps, "Visibility1"))),
            "subject-number-only" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.NearbyText, "506"),
                Cite(EvidenceKeys.BlockNameEffective, "505(506)"))),
            "subject-absent-key" => Answer(r, Choice("bus-stop-signs", Cite(EvidenceKeys.BlockAttributes, "BUS stop"))),
            "context" => Answer(r, Choice("bus-stop-signs", subject)) with { ContextId = new string('e', 64) },
            "library-hash" => Answer(r, Choice("bus-stop-signs", subject)) with { LibraryHash = new string('f', 64) },
            "family-other-basis" => Answer(r, Choice("road-pavement", subject)),
            "family-invented" => Answer(r, Choice("bus-stops", subject)),
            "prose-slash" => Answer(r, Choice("bus-stop-signs", subject) with { Explanation = Canary + " שלט/סככה" }),
            "prose-quantity-word" => Answer(r, Choice("bus-stop-signs", subject) with { MissingDetails = new[] { Canary + " לוודא כמות" } }),
            "prose-path" => Answer(r, Choice("bus-stop-signs", subject) with { Explanation = Canary + " C:\\private\\source.dwg" }),
            "prose-alternative-slash" => Answer(r, Choice("bus-stop-signs", subject),
                Choice("bus-stop-blocks", Cite(EvidenceKeys.BlockProps, "BUS stop")) with { Explanation = Canary + " בלוק/רחבה" }),
            "reason-slash" => new FamilyRankResponse(r.ContextId, r.LibraryHash, Array.Empty<FamilyRankChoice>(), Canary + " שלט/סככה/בלוק"),
            "reason-overlong" => new FamilyRankResponse(r.ContextId, r.LibraryHash, Array.Empty<FamilyRankChoice>(),
                Canary + new string('א', FamilyRecognitionAssist.MaxAbstentionReasonChars)),
            "reason-empty" => new FamilyRankResponse(r.ContextId, r.LibraryHash, Array.Empty<FamilyRankChoice>(), "  "),
            "ranked-and-reason" => Answer(r, Choice("bus-stop-signs", subject)) with { AbstentionReason = Canary },
            "four-families" => Answer(r, Choice("bus-stop-signs", subject), Choice("bus-shelters", subject),
                Choice("bus-stop-blocks", subject), Choice("sign-plates", subject)),
            "no-citation" => Answer(r, new FamilyRankChoice("bus-stop-signs", Explanation, Array.Empty<FamilyRankCitation>())),
            _ => null!,
        });

        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.FamilyId.Should().BeNull();
        proposal.CandidateCodes.Should().BeEmpty();
        proposal.EvidenceRefs.Should().BeEmpty();
        proposal.Alternatives.Should().BeEmpty();
        proposal.MissingDetails.Should().ContainSingle().Which.Should().StartWith("לא התקבלה הצעת משפחה מבוססת ראיות");
        // The existing public code is kept for its consumers; the fixed class is the new, separate diagnostic.
        FamilyRecognitionAssist.PublicOutcomeCode(proposal).Should().Be("response_not_grounded_or_unusable");
        FamilyRecognitionAssist.PublicRejectionCode(proposal).Should().Be(expected);
        FamilyRankRejection.All.Should().Contain(expected);
        JsonSerializer.Serialize(proposal).Should().NotContain(Canary).And.NotContain("שלט/סככה").And.NotContain("private");
    }

    [Fact]
    public async Task APunctuationOnlySupportIsNeverACitation()
    {
        var group = Bus505.Group(blockName: "SIGN--505");
        var provider = new FamilyRecognitionAssist(new Fake(r => Answer(r, Choice("bus-stop-signs",
            Cite(EvidenceKeys.BlockProps, Subject), Cite(EvidenceKeys.BlockNameEffective, "--")))));
        var proposal = (await provider.AssistAsync(group, Library, null, Array.Empty<RecognitionProposal>(), null, null, default)).Single();
        FamilyRecognitionAssist.PublicRejectionCode(proposal).Should().Be(FamilyRankRejection.SupportingCitationRefused);
    }

    [Fact]
    public async Task ABoundPublicAbstentionShowsItsReasonAndIsNotARejection()
    {
        const string reason = "הראיה מציינת תחנת אוטובוס אך אינה מבחינה בין שלט, סככה ובלוק";
        var proposal = await Ask(r => new FamilyRankResponse(r.ContextId, r.LibraryHash, Array.Empty<FamilyRankChoice>(), reason));

        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.MissingDetails.Should().ContainSingle().Which.Should().Contain(reason);
        FamilyRecognitionAssist.PublicOutcomeCode(proposal).Should().Be("provider_abstained");
        FamilyRecognitionAssist.PublicRejectionCode(proposal).Should().BeNull();
        proposal.AssistRejection.Should().BeNull();
    }

    [Fact]
    public void ARejectionClassOnANonAssistantOrProposedResultIsNeverReported()
    {
        var local = new RecognitionProposal("g", new[] { "r" }, RecognitionStatus.Abstained, null, Array.Empty<string>(),
            Array.Empty<RecognitionEvidenceRef>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RecognitionAlternative>(),
            new[] { "x" }, RecognitionProposal.OriginLocal) { AssistRejection = FamilyRankRejection.ProseRefused };
        FamilyRecognitionAssist.PublicRejectionCode(local).Should().BeNull();
        FamilyRecognitionAssist.PublicRejectionCode(local with { Origin = RecognitionProposal.OriginAi, AssistRejection = Canary })
            .Should().BeNull();
        FamilyRecognitionAssist.PublicRejectionCode(local with { Origin = RecognitionProposal.OriginAi }).Should().Be(FamilyRankRejection.ProseRefused);
    }

    [Theory]
    [InlineData("שלט/סככה")]
    [InlineData("שלט\\סככה")]
    [InlineData("ראו https://example")]
    [InlineData("C:\\private\\x.dwg")]
    [InlineData("מחיר השלט")]
    [InlineData("לוודא כמות")]
    [InlineData("price")]
    [InlineData("quantity")]
    [InlineData("token")]
    [InlineData("password")]
    [InlineData("api key")]
    [InlineData("עלות ₪")]
    [InlineData("123456")]
    [InlineData("שורה\nשנייה")]
    public void EveryTextTheFamilyPromptForbidsIsStillRefused(string text)
    {
        // SafeText is not relaxed: the prompt now states these rules instead of inviting text that is then discarded.
        FamilyTextGuards.SafeText(text, FamilyRecognitionAssist.MaxExplanationChars).Should().BeFalse();
    }

    [Fact]
    public async Task TheFamilyPromptStatesTheSupportingCitationAndTextRules()
    {
        string? instructions = null;
        using var client = new HttpClient(new Handler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            instructions = payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString();
            var data = JsonDocument.Parse(payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!).RootElement;
            var answer = JsonSerializer.Serialize(new
            {
                context_id = data.GetProperty("context_id").GetString(), library_hash = data.GetProperty("library_hash").GetString(),
                ranked = new[]
                {
                    new
                    {
                        family_id = "bus-stop-signs", explanation = Explanation,
                        citations = new[] { new { evidence_key = EvidenceKeys.BlockProps, quote = Subject },
                            new { evidence_key = EvidenceKeys.BlockNameEffective, quote = "505(506)" } },
                        missing_details = new[] { "לבדוק מול השרטוט אם מדובר בשלט, בסככה או בבלוק תחנה" },
                    },
                },
                abstention_reason = (string?)null,
            });
            var envelope = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = answer } } }, finishReason = "STOP" } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
        }));
        var result = await new FamilyRecognitionAssist(new GeminiMappingAssistant(client, () => "SYNTHETIC-NOT-A-KEY"))
            .AssistAsync(Bus505.Group(), Library, null, Array.Empty<RecognitionProposal>(), null, null, default);

        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Proposed);
        instructions.Should().Contain("closed candidate list").And.Contain("untrusted DATA")
            .And.Contain("without the record count in parentheses")
            .And.Contain("At least one citation per family must be a subject citation")
            .And.Contain("may quote a code or number exactly as listed").And.Contain("never carries a family alone")
            .And.Contain("Never cite the layer")
            .And.Contain("no slash or backslash anywhere").And.Contain("\"מחיר\"").And.Contain("\"כמות\"")
            .And.Contain("explanation at most 600").And.Contain("missing_details note at most 180").And.Contain("abstention_reason at most 200");
        // The old wording demanded subject meaning from every quote, supporting ones included.
        instructions.Should().NotContain("and must carry subject meaning, not an identifier or number alone");
    }

    private static async Task<RecognitionProposal> Ask(Func<FamilyRankRequest, FamilyRankResponse> reply)
    {
        var group = Bus505.Group();
        var local = LocalFamilyClassifier.Instance.Classify(group, Library, null);
        return (await new FamilyRecognitionAssist(new Fake(reply)).AssistAsync(group, Library, null, local, null, null, default)).Single();
    }

    private static FamilyRankResponse Answer(FamilyRankRequest request, params FamilyRankChoice[] ranked) =>
        new(request.ContextId, request.LibraryHash, ranked);

    private static FamilyRankChoice Choice(string family, params FamilyRankCitation[] citations) =>
        new(family, Explanation, citations, new[] { "לבדוק מול השרטוט את סוג הרכיב" });

    private static FamilyRankCitation Cite(string key, string quote) => new(key, quote);

    private sealed class Fake(Func<FamilyRankRequest, FamilyRankResponse> reply) : IFamilyRecognitionProvider
    {
        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken ct) => Task.FromResult(reply(request));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

/// <summary>
/// SYNTHETIC records with the exact evidence of the native 505 group: 115 dynamic-block counts on layer 505, block 505(506),
/// Visibility1 = "505 BUS stop", white effective colour, ByLayer/Continuous, and nearby texts on five records (505+506 on
/// four, 12 from an XREF on one). Handles and the XREF name are canaries that must never travel.
/// </summary>
internal static class Bus505
{
    public const int Count = 115;
    public const string GroupId = "rg|(קובץ ראשי)|505|count|יח'|block|505(506)";

    public static RecognitionGroupInput Group(string blockName = "505(506)") =>
        new(GroupId, "(קובץ ראשי)", DraftSourceRole.Host, "505", "count", "יח'", "block", blockName,
            Enumerable.Range(1, Count).Select(index => Record(index, blockName)).ToArray());

    private static NeutralQuantityRecord Record(int index, string blockName)
    {
        var parameters = new Dictionary<string, string>
        {
            [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + "_status"] = "read",
            [EvidenceKeys.BlockProps] = "[{\"name\":\"Visibility1\",\"value\":\"505 BUS stop\",\"units\":\"NoUnits\"}]",
            [EvidenceKeys.BlockProps + "_status"] = "read",
            [EvidenceKeys.BlockAttributes + "_status"] = "absent",
            [EvidenceKeys.PsetComponent + "_status"] = "absent",
            [EvidenceKeys.Hatch + "_status"] = "absent",
            [EvidenceKeys.LegendRow + "_status"] = "absent",
            [EvidenceKeys.Closed + "_status"] = "absent",
            [EvidenceKeys.ColorEffective] = "255,255,255", [EvidenceKeys.ColorEffective + "_status"] = "read",
            [EvidenceKeys.NearbyText + "_status"] = "absent",
            [EvidenceKeys.BlockNameEffective] = blockName,
            [EvidenceKeys.EntityLinetype] = "ByLayer",
            [EvidenceKeys.LayerLinetype] = "Continuous",
            [EvidenceKeys.EntityColorIndex] = "256",
        };
        var nearby = index switch
        {
            48 => new[] { ("12", "SYNTHETIC-XREF") },
            60 or 61 or 65 or 68 => new[] { ("505", "host"), ("506", "host") },
            _ => null,
        };
        if (nearby != null)
        {
            parameters[EvidenceKeys.NearbyText] = JsonSerializer.Serialize(nearby.Select((n, i) => new
            {
                text = n.Item1, distance_m = 1.8 + i, handle = "SYNHANDLE" + index.ToString(CultureInfo.InvariantCulture), source = n.Item2, kind = "text",
            }));
            parameters[EvidenceKeys.NearbyText + "_status"] = "read";
        }
        var id = "r" + index.ToString("000", CultureInfo.InvariantCulture);
        return new NeutralQuantityRecord
        {
            RecordId = id, ProjectProfileId = "synthetic", RunId = "synthetic",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg", DrawingHash = new string('a', 64), Handle = "SYNHANDLE" + id, EntityType = "BLOCKREFERENCE", Layer = "505",
            },
            Measurement = new QuantityMeasurement { Kind = "count", Method = "block-count", RawValue = 1, Unit = "יח'", Parameters = parameters },
        };
    }
}
