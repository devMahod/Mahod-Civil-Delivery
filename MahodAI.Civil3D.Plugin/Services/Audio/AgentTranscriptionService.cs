using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Config;

namespace MahodAI.Civil3D.Plugin.Services.Audio
{
    /// <summary>
    /// Sends recorded audio to the MahodAI Agent API for server-side
    /// speech-to-text, keeping any STT provider key off the client.
    ///
    /// Contract — the Python agent must expose:
    ///   POST {host}/api/v1/transcribe   (multipart/form-data)
    ///     headers: X-API-Key
    ///     fields : file        — the WAV audio (audio/wav)
    ///              language    — language hint, e.g. "he"
    ///              session_id  — (optional) originating chat session
    ///     response (200, application/json): { "text": "<transcribed text>" }
    ///
    /// The endpoint host is derived from MAHOD_AGENT_API_URL the same way file
    /// uploads are (a trailing "/api" is stripped, then "/api/v1/transcribe" is
    /// appended), so it lines up with the existing /api/v1/upload route.
    /// </summary>
    public sealed class AgentTranscriptionService : ITranscriptionService
    {
        private const string DefaultApiKey = "your-secure-api-key-here";

        // A single shared HttpClient for all transcription requests. Allocating
        // one per call would churn sockets (TIME_WAIT) because live transcription
        // polls roughly every 2s while the user dictates.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        public async Task<string> TranscribeAsync(
            byte[] wavBytes,
            string? language = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            if (wavBytes == null || wavBytes.Length == 0)
                return string.Empty;

            var agentUrl = PluginConstants.ResolveAgentApiUrl();
            var apiKey = Environment.GetEnvironmentVariable("MAHOD_AGENT_API_KEY") ?? DefaultApiKey;

            string baseHost = agentUrl.TrimEnd('/');
            if (baseHost.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                baseHost = baseHost.Substring(0, baseHost.Length - 4);
            var url = $"{baseHost}/api/v1/transcribe";

            using var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wavBytes);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audio, "file", "recording.wav");
            // Only pin a language when the caller explicitly asks. Sending
            // "he" unconditionally made Whisper render ENGLISH speech in
            // Hebrew letters ("hey test one two three" -> "היי טסט וואן טו טרי").
            // With no hint the agent lets Whisper auto-detect.
            if (!string.IsNullOrWhiteSpace(language))
                form.Add(new StringContent(language!), "language");
            if (!string.IsNullOrEmpty(sessionId))
                form.Add(new StringContent(sessionId!), "session_id");

            // X-API-Key is set per-request rather than on the shared client's
            // DefaultRequestHeaders, so it always reflects the current env value
            // and we never mutate shared state across concurrent calls.
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Add("X-API-Key", apiKey);

            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"שגיאת תמלול ({(int)response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Accept a few common field names so the agent contract is forgiving.
                foreach (var key in new[] { "text", "transcript", "transcription" })
                {
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString()?.Trim() ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }
}
