using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class GeminiMappingAssistantTests
{
    private const string Key = "TEST_ONLY_NOT_A_REAL_KEY_9x42";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task TwoStageFakeHttp_SendsOnlyNarrowDataWithKeyInHeaderAndReturnsUnapproved()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(async (request, cancellationToken, index) =>
        {
            request.RequestUri!.Scheme.Should().Be("https");
            request.RequestUri.Host.Should().Be("generativelanguage.googleapis.com");
            request.RequestUri.AbsolutePath.Should().EndWith("/gemini-3.8-flash:generateContent");
            request.RequestUri.Query.Should().BeEmpty();
            request.Headers.GetValues("x-goog-api-key").Should().Equal(Key);
            var body = await request.Content!.ReadAsStringAsync();
            requests.Add(body);
            body.Should().NotContain(Key).And.NotContain("29287").And.NotContain("private-source");
            using var payload = JsonDocument.Parse(body);
            var text = payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
            using var data = JsonDocument.Parse(text);
            var id = data.RootElement.GetProperty("context_id").GetString()!;
            if (index == 0)
            {
                data.RootElement.TryGetProperty("candidates", out _).Should().BeFalse();
                return Response(request, JsonSerializer.Serialize(new { context_id = id,
                    terms = new[] { new { text = "אבן שפה", evidence_key = "engineer_context", quote = "אבן שפה" } }, abstention_reason = (string?)null }));
            }
            data.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(1);
            return Response(request, JsonSerializer.Serialize(new { context_id = id, catalog_hash = Hash,
                ranked = new[] { new { code = "U40.01.0010", explanation = "השערה המתאימה לתיאור אבן השפה; נדרש לבדוק פרטים", evidence_keys = new[] { "engineer_context" } } }, abstention_reason = (string?)null }));
        }));
        var result = await new GeminiMappingAssistant(client, () => Key).AssistAsync(Group(), Catalog(), "אבן שפה לאורך המדרכה", default);
        result.IsAbstained.Should().BeFalse();
        requests.Should().HaveCount(2);
        result.Proposals.Should().OnlyContain(proposal => proposal.Status == "PROPOSED_UNAPPROVED" && proposal.EvidenceKind == "ai-semantic-v1");
        client.DefaultRequestHeaders.Contains("x-goog-api-key").Should().BeFalse();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("redirect")]
    [InlineData("too-large")]
    [InlineData("malformed")]
    [InlineData("incomplete")]
    [InlineData("extra-field")]
    [InlineData("duplicate-field")]
    [InlineData("echo-key")]
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
                "extra-field" => Response(request, "{\"context_id\":\"x\",\"terms\":[],\"price\":42}"),
                "duplicate-field" => Response(request, "{\"context_id\":\"x\",\"context_id\":\"y\",\"terms\":[]}"),
                _ => Response(request, "{\"context_id\":\"" + Key + "\",\"terms\":[]}")
            };
            return Task.FromResult(response);
        }));
        var result = await new GeminiMappingAssistant(client, () => Key).AssistAsync(Group(), Catalog(), "אבן שפה", default);
        result.Proposals.Should().BeEmpty();
        result.IsAbstained.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain(Key).And.NotContain("Unauthorized").And.NotContain("body");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task MissingOrThrowingKeyProvider_DoesNotSendAnythingOrSurfaceException()
    {
        var handler = new Handler((_, _, _) => throw new Xunit.Sdk.XunitException("No HTTP request permitted"));
        using var client = new HttpClient(handler);
        foreach (var keyProvider in new Func<string?>[] { () => null, () => throw new Exception(Key) })
        {
            var result = await new GeminiMappingAssistant(client, keyProvider).AssistAsync(Group(), Catalog(), "אבן שפה", default);
            result.IsAbstained.Should().BeTrue();
            JsonSerializer.Serialize(result).Should().NotContain(Key);
        }
    }

    [Fact]
    public async Task HttpCancellation_ReturnsUnchangedManualFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct, _) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        var result = await new GeminiMappingAssistant(client, () => Key)
            .AssistAsync(Group(), Catalog(), "אבן שפה", cancellation.Token);
        result.IsAbstained.Should().BeTrue();
        result.Message.Should().Contain("בוטלה");
    }

    [Theory]
    [InlineData("../gemini")]
    [InlineData("gemini-x?key=secret")]
    [InlineData("https://other-host/model")]
    public void ModelCannotOverrideFixedEndpointOrInjectQuery(string model)
    {
        using var client = new HttpClient(new Handler((_, _, _) => throw new Exception()));
        Action create = () => new GeminiMappingAssistant(client, () => Key, model);
        create.Should().Throw<ArgumentException>();
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, string json, string finish = "STOP") =>
        new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new
            { candidates = new[] { new { finishReason = finish, content = new { parts = new[] { new { text = json } } } } } }), Encoding.UTF8, "application/json") };
    private static MappingProposalEngine.DiscoveredGroup Group() => new("private-source|Q742|length", "Q742", "length", "m", 297, 29287.63);
    private static CatalogSnapshot Catalog()
    {
        var result = new CatalogSnapshot { SnapshotId = "local", FileHash = Hash };
        result.Items.Add("U40.01.0010", new CatalogItem { Code = "U40.01.0010", Description = "אבן שפה", UnitRaw = "m" });
        return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken, _index++);
    }
}
