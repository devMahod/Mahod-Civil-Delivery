using System;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace MahodAI.Civil3D.Plugin.Services.Audio
{
    /// <summary>
    /// Captures microphone input as raw 16 kHz mono 16-bit PCM using NAudio, and
    /// can produce a complete WAV (RIFF header + data) of everything captured so
    /// far at any moment — including mid-recording, which is what powers the
    /// live "transcribe while you speak" loop.
    ///
    /// The mono/16 kHz format is tuned for speech-to-text: small payload
    /// (~32 KB/s) while preserving intelligibility for engines like Whisper.
    ///
    /// Recording in the native WPF layer (rather than the WebView2) sidesteps the
    /// getUserMedia secure-context restriction — the chat HTML is loaded via
    /// NavigateToString, which has an opaque origin and blocks microphone access.
    ///
    /// Start/Stop are expected on the UI thread; <see cref="SnapshotWav"/> is
    /// safe to call from any thread (the PCM buffer is lock-guarded).
    /// </summary>
    public sealed class AudioRecorder : IDisposable
    {
        private readonly object _sync = new();
        private readonly WaveFormat _format = new(16000, 16, 1);
        private WaveInEvent? _waveIn;
        private MemoryStream? _pcm;   // accumulated raw PCM samples

        public bool IsRecording { get; private set; }

        /// <summary>True when at least one audio input (microphone) device is present.</summary>
        public static bool HasInputDevice => WaveInEvent.DeviceCount > 0;

        /// <summary>
        /// Begins capturing from the default microphone. Throws if no input device
        /// is available or the device cannot be opened.
        /// </summary>
        public void Start()
        {
            if (IsRecording) return;

            if (!HasInputDevice)
                throw new InvalidOperationException("לא נמצא מיקרופון מחובר.");

            lock (_sync) { _pcm = new MemoryStream(); }

            _waveIn = new WaveInEvent { WaveFormat = _format, BufferMilliseconds = 50 };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();

            IsRecording = true;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            lock (_sync) { _pcm?.Write(e.Buffer, 0, e.BytesRecorded); }
        }

        /// <summary>
        /// Returns a complete WAV byte[] of all audio captured so far. Safe to call
        /// while still recording (used for live interim transcription). Returns an
        /// empty array if nothing has been captured yet.
        /// </summary>
        public byte[] SnapshotWav()
        {
            byte[] pcm;
            lock (_sync) { pcm = _pcm?.ToArray() ?? Array.Empty<byte>(); }
            return pcm.Length == 0 ? Array.Empty<byte>() : WrapPcmInWav(pcm, _format);
        }

        /// <summary>
        /// Stops capture and returns the full recording as a WAV byte[].
        /// </summary>
        public byte[] Stop()
        {
            if (!IsRecording) return Array.Empty<byte>();
            IsRecording = false;

            if (_waveIn != null)
            {
                try { _waveIn.StopRecording(); } catch { /* device already gone */ }
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.Dispose();
                _waveIn = null;
            }

            var wav = SnapshotWav();
            lock (_sync) { _pcm?.Dispose(); _pcm = null; }
            return wav;
        }

        /// <summary>Wraps raw PCM samples in a canonical 44-byte PCM WAV header.</summary>
        private static byte[] WrapPcmInWav(byte[] pcm, WaveFormat fmt)
        {
            using var ms = new MemoryStream(pcm.Length + 44);
            var bw = new BinaryWriter(ms);   // not disposed — that would close ms before ToArray

            int bytesPerSample = fmt.BitsPerSample / 8;
            int byteRate = fmt.SampleRate * fmt.Channels * bytesPerSample;
            short blockAlign = (short)(fmt.Channels * bytesPerSample);

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcm.Length);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                 // PCM fmt chunk size
            bw.Write((short)1);           // audio format = PCM
            bw.Write((short)fmt.Channels);
            bw.Write(fmt.SampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)fmt.BitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcm.Length);
            bw.Write(pcm);
            bw.Flush();

            return ms.ToArray();
        }

        public void Dispose()
        {
            try { if (IsRecording) Stop(); } catch { /* best-effort */ }
            _waveIn?.Dispose();
            lock (_sync) { _pcm?.Dispose(); _pcm = null; }
        }
    }
}
