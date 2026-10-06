using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Services.Audio
{
    /// <summary>
    /// Converts recorded audio (WAV bytes) into text. Implementations decide
    /// where the speech-to-text actually happens (MahodAI agent, OpenAI Whisper,
    /// Azure Speech, …) so the recording/UI layer stays backend-agnostic.
    /// </summary>
    public interface ITranscriptionService
    {
        /// <summary>
        /// Transcribes the given WAV audio.
        /// </summary>
        /// <param name="wavBytes">Complete WAV (RIFF) audio buffer.</param>
        /// <param name="language">Optional BCP-47 / ISO language hint. Null or
        /// empty means auto-detect, which is what dictation should use so the
        /// spoken language decides the script.</param>
        /// <param name="sessionId">Optional agent session id for correlation.</param>
        /// <returns>The transcribed text, or an empty string if nothing was recognized.</returns>
        Task<string> TranscribeAsync(
            byte[] wavBytes,
            string? language = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default);
    }
}
