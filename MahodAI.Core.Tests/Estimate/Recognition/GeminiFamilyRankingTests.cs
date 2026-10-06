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

/// <summary>Family ranking over the Gemini transport with a fake HTTP handler only. No live network, no real key.</summary>
public sealed class GeminiFamilyRankingTests
{
    private const string Key = "TEST_ONLY_NOT_A_REAL_KEY_9x42";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly IReadOnlyList<RecognitionProposal> NoLocal = Array.Empty<RecognitionProposal>();

    [Fact]
    public async Task FamilyRankingFakeHttp_SendsNarrowTextOnlyRequestWithKeyInHeader()
    {
        var bodies = new List<string>();
        using var client = new HttpClient(new Handler(async (request, _, _) =>
        {
            request.RequestUri!.Scheme.Should().Be("https");
            request.RequestUri.Host.Should().Be("generativelanguage.googleapis.com");
            request.RequestUri.AbsolutePath.Should().EndWith("/gemini-3.8-flash:generateContent");
            request.RequestUri.Query.Should().BeEmpty();
            request.Headers.GetValues("x-goog-api-key").Should().Equal(Key);
            var body = await request.Content!.ReadAsStringAsync();
            bodies.Add(body);
            body.Should().NotContain(Key).And.NotContain("29287").And.NotContain("private-source").And.NotContain("204539")
                .And.NotContain("PRIVATE-XREF-NAME").And.NotContain("NEARHANDLE77").And.NotContain("HREC").And.NotContain("inline_data");
            using var payload = JsonDocument.Parse(body);
            payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
                .Should().Contain("closed candidate list").And.Contain("untrusted DATA");
            payload.RootElement.GetProperty("generationConfig").GetProperty("temperature").GetInt32().Should().Be(0);
            var parts = payload.RootElement.GetProperty("contents")[0].GetProperty("parts");
            parts.GetArrayLength().Should().Be(1);
            using var data = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
            var root = data.RootElement;
            root.GetProperty("images").GetArrayLength().Should().Be(0);
            root.TryGetProperty("vision", out _).Should().BeFalse();
            root.GetProperty("candidates").EnumerateArray().Select(candidate => candidate.GetProperty("family_id").GetString())
                .Should().Contain("curb-road").And.NotContain("road-pavement");
            root.GetProperty("evidence").EnumerateArray().Select(item => item.GetProperty("key").GetString())
                .Should().Contain(EvidenceKeys.NearbyText).And.NotContain(EvidenceKeys.GeometrySample).And.NotContain(EvidenceKeys.XrefTransform);
            return Response(request, Answer(root.GetProperty("context_id").GetString()!, root.GetProperty("library_hash").GetString()!));
        }));
        var result = await new FamilyRecognitionAssist(new GeminiMappingAssistant(client, () => Key))
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        bodies.Should().HaveCount(1);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.Origin.Should().Be(RecognitionProposal.OriginAi);
        proposal.FamilyId.Should().Be("curb-road");
        proposal.CandidateCodes.Should().Equal("U51.06.1900");
        proposal.MissingDetails.Should().Equal("לבדוק את סוג האבן והמידות");
        client.DefaultRequestHeaders.Contains("x-goog-api-key").Should().BeFalse();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("redirect")]
    [InlineData("too-large")]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("extra-field")]
    [InlineData("code-field")]
    [InlineData("duplicate-field")]
    [InlineData("echo-key")]
    [InlineData("thought")]
    public async Task FailedOrUntrustedHttpResponse_IsSanitizedAndCannotPublish(string failure)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, _, _) =>
        {
            calls++;
            var response = failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request, Content = new StringContent(Key + " body") },
                "redirect" => new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request, Content = new StringContent(Key) },
                "too-large" => new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(new string('x', 65537)) },
                "malformed" => Response(request, "not json " + Key),
                "incomplete" => Response(request, "{}", "MAX_TOKENS"),
                "extra-field" => Response(request, "{\"context_id\":\"x\",\"library_hash\":\"y\",\"ranked\":[],\"price\":42}"),
                "code-field" => Response(request, "{\"context_id\":\"x\",\"library_hash\":\"y\",\"ranked\":[{\"family_id\":\"curb-road\",\"explanation\":\"x\",\"citations\":[],\"code\":\"U51.06.1900\"}]}"),
                "duplicate-field" => Response(request, "{\"context_id\":\"x\",\"context_id\":\"y\",\"library_hash\":\"y\",\"ranked\":[]}"),
                "thought" => ThoughtResponse(request),
                _ => Response(request, "{\"context_id\":\"" + Key + "\",\"library_hash\":\"y\",\"ranked\":[]}"),
            };
            return Task.FromResult(response);
        }));
        var result = await new FamilyRecognitionAssist(new GeminiMappingAssistant(client, () => Key))
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.FamilyId.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain(Key).And.NotContain("Unauthorized").And.NotContain("body");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task MissingOrThrowingKeyProvider_SendsNothing()
    {
        using var client = new HttpClient(new Handler((_, _, _) => throw new Xunit.Sdk.XunitException("No HTTP request permitted")));
        foreach (var keyProvider in new Func<string?>[] { () => null, () => throw new Exception(Key) })
        {
            var result = await new FamilyRecognitionAssist(new GeminiMappingAssistant(client, keyProvider))
                .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
            result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Abstained);
            JsonSerializer.Serialize(result).Should().NotContain(Key);
        }
    }

    [Fact]
    public async Task HttpCancellation_ReturnsCancelledAbstention()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct, _) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        var result = await new FamilyRecognitionAssist(new GeminiMappingAssistant(client, () => Key))
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, cancellation.Token);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("בוטלה");
    }

    [Theory]
    [InlineData("no-permit")]
    [InlineData("hash-mismatch")]
    [InlineData("context-mismatch")]
    [InlineData("policy-disabled")]
    [InlineData("policy-throws")]
    [InlineData("undeclared")]
    [InlineData("non-png")]
    [InlineData("oversize")]
    [InlineData("metadata")]
    public async Task ImagesWithoutPolicyAndExactPermitOrValidPng_SendNoHttp(string defect)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("No HTTP request permitted");
        }));
        var png = defect switch
        {
            "non-png" => Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[64]).ToArray(),
            "oversize" => TestPng.Create(513, 1),
            "metadata" => TestPng.Create(4, 4, ("tEXt", Encoding.ASCII.GetBytes("Comment\0C:\\private\\source.dwg"))),
            _ => TestPng.Create(4, 4),
        };
        // Built past the factory on purpose: the transport must re-validate whatever it is handed.
        var image = new VisionImage(png, VisionImagePolicy.GroupPreview, VisionImagePolicy.Sha256(png), 4, 4);
        var request = FamilyAssistFixtures.Request(defect == "undeclared"
            ? Array.Empty<FamilyRankImage>()
            : new[] { new FamilyRankImage(image.Sha256, image.Kind) });
        var permit = new VisionSendPermit(defect == "context-mismatch" ? new string('e', 64) : request.ContextId,
            defect == "hash-mismatch" ? new[] { new string('b', 64) } : new[] { image.Sha256 }, "Arthur", DateTimeOffset.UtcNow);
        request = request with { Vision = new VisionPayload(new[] { image }, defect == "no-permit" ? null : permit) };
        Func<bool> policy = defect switch
        {
            "policy-disabled" => () => false,
            "policy-throws" => () => throw new InvalidOperationException(Key),
            _ => () => true,
        };
        var assistant = new GeminiMappingAssistant(client, () => Key, visionEnabled: policy);
        Func<Task> act = () => assistant.RankFamiliesAsync(request, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("throws")]
    [InlineData("no-delegate")]
    public async Task PolicyOffWithAPermittedImage_AbstainsWithTextOnlyGuidanceAndSendsNothing(string policy)
    {
        var calls = 0;
        var keyReads = 0;
        using var client = new HttpClient(new Handler((_, _, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("No HTTP request permitted");
        }));
        var group = FamilyAssistFixtures.Group();
        var images = new[] { TestPng.Image(4) };
        var contextId = FamilyRecognitionAssist.Prepare(group, Library, null, images).Request!.ContextId;
        var vision = new VisionPayload(images, new VisionSendPermit(contextId, new[] { images[0].Sha256 }, "Arthur", DateTimeOffset.UtcNow));
        Func<string?> key = () =>
        {
            keyReads++;
            return Key;
        };
        var assistant = policy switch
        {
            "disabled" => new GeminiMappingAssistant(client, key, visionEnabled: () => false),
            "throws" => new GeminiMappingAssistant(client, key, visionEnabled: () => throw new InvalidOperationException(Key)),
            _ => new GeminiMappingAssistant(client, key),
        };
        var result = await new FamilyRecognitionAssist(assistant)
            .AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, vision, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        // Not "the service is unavailable": the engineer is told images are off and that a text-only request works.
        proposal.MissingDetails.Single().Should().Contain("מדיניות הארגון").And.Contain("בלי תמונות").And.NotContain("אינו זמין");
        calls.Should().Be(0);
        keyReads.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain(Key);
    }

    [Fact]
    public async Task PolicyOff_TransportRefusalIsTheDedicatedVisionException()
    {
        using var client = new HttpClient(new Handler((_, _, _) => throw new Xunit.Sdk.XunitException("No HTTP request permitted")));
        var image = TestPng.Image(4);
        var request = FamilyAssistFixtures.Request(new[] { new FamilyRankImage(image.Sha256, image.Kind) });
        request = request with { Vision = new VisionPayload(new[] { image },
            new VisionSendPermit(request.ContextId, new[] { image.Sha256 }, "Arthur", DateTimeOffset.UtcNow)) };
        var assistant = new GeminiMappingAssistant(client, () => Key, visionEnabled: () => false);
        assistant.IsVisionAllowed().Should().BeFalse();
        Func<Task> act = () => assistant.RankFamiliesAsync(request, default);
        (await act.Should().ThrowAsync<VisionNotPermittedException>()).Which.OrganizationPolicyDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task DefaultTransport_NeverSendsImagesEvenWithAValidPermit()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("No HTTP request permitted");
        }));
        var image = TestPng.Image(4);
        var request = FamilyAssistFixtures.Request(new[] { new FamilyRankImage(image.Sha256, image.Kind) });
        request = request with { Vision = new VisionPayload(new[] { image },
            new VisionSendPermit(request.ContextId, new[] { image.Sha256 }, "Arthur", DateTimeOffset.UtcNow)) };
        Func<Task> act = () => new GeminiMappingAssistant(client, () => Key).RankFamiliesAsync(request, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task PolicyAndExactPermit_SendOneInlinePngPartBesideTheText()
    {
        string? body = null;
        var hash = FamilyAssistFixtures.Request().LibraryHash;
        using var client = new HttpClient(new Handler(async (request, _, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response(request, JsonSerializer.Serialize(new
            {
                context_id = new string('c', 64), library_hash = hash, ranked = Array.Empty<object>(), abstention_reason = "אין די ראיות",
            }));
        }));
        var png = TestPng.Create(8, 8);
        VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out _).Should().BeTrue();
        var request = FamilyAssistFixtures.Request(new[] { new FamilyRankImage(image!.Sha256, image.Kind) });
        request = request with { Vision = new VisionPayload(new[] { image },
            new VisionSendPermit(request.ContextId, new[] { image.Sha256 }, "Arthur", DateTimeOffset.UtcNow)) };
        var response = await new GeminiMappingAssistant(client, () => Key, visionEnabled: () => true).RankFamiliesAsync(request, default);
        response.Ranked.Should().BeEmpty();
        using var payload = JsonDocument.Parse(body!);
        var parts = payload.RootElement.GetProperty("contents")[0].GetProperty("parts");
        parts.GetArrayLength().Should().Be(2);
        var text = parts[0].GetProperty("text").GetString()!;
        text.Should().NotContain(Convert.ToBase64String(png)).And.NotContain("Arthur");
        using (var data = JsonDocument.Parse(text))
            data.RootElement.GetProperty("images")[0].GetProperty("sha256").GetString().Should().Be(image.Sha256);
        // UNVERIFIED against the live API: this pins the generateContent REST inline_data part until an acceptance probe.
        var inline = parts[1].GetProperty("inline_data");
        inline.GetProperty("mime_type").GetString().Should().Be("image/png");
        inline.GetProperty("data").GetString().Should().Be(Convert.ToBase64String(png));
        body.Should().NotContain(Key);
    }

    [Fact]
    public async Task OversizedRequest_IsRefusedBeforeHttp()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("No HTTP request permitted");
        }));
        var request = FamilyAssistFixtures.Request(value: new string('x', GeminiMappingAssistant.MaxRequestBytes + 1));
        Func<Task> act = () => new GeminiMappingAssistant(client, () => Key).RankFamiliesAsync(request, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(0);
    }

    private static string Answer(string contextId, string libraryHash) => JsonSerializer.Serialize(new
    {
        context_id = contextId,
        library_hash = libraryHash,
        ranked = new[]
        {
            new
            {
                family_id = "curb-road",
                explanation = "ייתכן שהקבוצה היא אבן שפה לפי הטקסט הסמוך; נדרש לבדוק מול השרטוט",
                citations = new[] { new { evidence_key = EvidenceKeys.NearbyText, quote = "אבן שפה" } },
                missing_details = new[] { "לבדוק את סוג האבן והמידות" },
            },
        },
        abstention_reason = (string?)null,
    });

    private static HttpResponseMessage Response(HttpRequestMessage request, string json, string finish = "STOP") =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new
            { candidates = new[] { new { finishReason = finish, content = new { parts = new[] { new { text = json } } } } } }), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ThoughtResponse(HttpRequestMessage request) =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new
            { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = "{}", thought = true } } } } } }), Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken, _index++);
    }
}
