using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MahodAI.Civil3D.Plugin.Services.Audio;

using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Voice-input (microphone) support for the chat composer.
    ///
    /// Click the mic button to start recording. While recording, the audio
    /// captured so far is transcribed every <see cref="LivePollIntervalMs"/> ms
    /// and the growing text is shown live in the input box ("gradual typing").
    /// Click again to stop: a final transcription pass leaves the complete text
    /// in the input box for the user to review, edit, and send — nothing is sent
    /// automatically.
    ///
    /// Transcription goes through <see cref="ITranscriptionService"/> (the agent's
    /// self-hosted Whisper endpoint by default), so this needs no paid STT service.
    /// True real-time streaming isn't available for free, so the live effect is
    /// achieved by re-transcribing the accumulated clip on a short interval.
    /// </summary>
    public partial class NewChatControl
    {
        private const int LivePollIntervalMs = 2000;

        private readonly AudioRecorder _audioRecorder = new();
        private readonly ITranscriptionService _transcriptionService = new AgentTranscriptionService();

        private DispatcherTimer? _liveTimer;
        private bool _isRecording;
        private bool _liveBusy;             // a live poll is currently in flight
        private string _liveBaseText = "";  // text already in the box when recording began
        private string _liveTranscript = "";

        // Idle vs recording fills for the mic button (transparent / error red),
        // and the line-icon stroke colors for each state.
        private static readonly WpfSolidColorBrush MicIdleBrush =
            new(WpfColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
        private static readonly WpfSolidColorBrush MicRecordingBrush =
            new(WpfColor.FromRgb(0xDC, 0x26, 0x26));
        private static readonly WpfSolidColorBrush MicIdleStroke =
            new(WpfColor.FromRgb(0x6B, 0x7C, 0x82));
        private static readonly WpfSolidColorBrush MicRecordingStroke =
            new(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));

        static NewChatControl()
        {
            MicIdleBrush.Freeze();
            MicRecordingBrush.Freeze();
            MicIdleStroke.Freeze();
            MicRecordingStroke.Freeze();
        }

        private async void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
                StartVoiceRecording();
            else
                await StopVoiceRecordingAndTranscribeAsync();
        }

        private void StartVoiceRecording()
        {
            try
            {
                _audioRecorder.Start();
                _isRecording = true;
                _liveBusy = false;
                _liveTranscript = "";
                _liveBaseText = InputTextBox?.Text ?? "";
                SetMicRecordingVisual(true);

                _liveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(LivePollIntervalMs)
                };
                _liveTimer.Tick += LiveTranscriptionTick;
                _liveTimer.Start();
            }
            catch (Exception ex)
            {
                _isRecording = false;
                SetMicRecordingVisual(false);
                AddSystemMessage($"לא ניתן להתחיל הקלטה: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires on the UI thread every poll interval: transcribes the audio
        /// captured so far and updates the input box. Errors are swallowed
        /// (logged only) so a missing/failing endpoint doesn't spam the chat —
        /// the user still gets one error from the final pass on stop.
        /// </summary>
        private async void LiveTranscriptionTick(object? sender, EventArgs e)
        {
            if (!_isRecording || _liveBusy) return;

            byte[] snapshot = _audioRecorder.SnapshotWav();
            if (snapshot.Length == 0) return;

            _liveBusy = true;
            try
            {
                string text = await _transcriptionService.TranscribeAsync(
                    snapshot,
                    language: null,
                    sessionId: _vm.ActiveTab?.SessionId,
                    cancellationToken: CancellationToken.None);

                // A late tick can resolve after Stop(); ignore it then.
                if (_isRecording && !string.IsNullOrWhiteSpace(text))
                {
                    _liveTranscript = text;
                    ApplyLiveTranscript();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Voice] live tick failed: {ex.Message}");
            }
            finally
            {
                _liveBusy = false;
            }
        }

        private async Task StopVoiceRecordingAndTranscribeAsync()
        {
            // Stop the live loop first so no tick races the final pass.
            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer.Tick -= LiveTranscriptionTick;
                _liveTimer = null;
            }

            byte[] wav;
            try
            {
                wav = _audioRecorder.Stop();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"שגיאה בעצירת ההקלטה: {ex.Message}");
                return;
            }
            finally
            {
                _isRecording = false;
                SetMicRecordingVisual(false);
            }

            if (wav.Length == 0)
            {
                AddSystemMessage("לא הוקלט אודיו.");
                return;
            }

            if (MicButton != null)
            {
                MicButton.IsEnabled = false;
            }
            SetTranscribingLoader(true);

            try
            {
                string text = await _transcriptionService.TranscribeAsync(
                    wav,
                    language: null,
                    sessionId: _vm.ActiveTab?.SessionId,
                    cancellationToken: CancellationToken.None);

                if (!string.IsNullOrWhiteSpace(text))
                    _liveTranscript = text;

                // Leave the final text in the box for the user to review/edit/send.
                if (string.IsNullOrWhiteSpace(_liveTranscript))
                    AddSystemMessage("לא זוהה דיבור בהקלטה.");
                else
                    ApplyLiveTranscript(focus: true);
            }
            catch (Exception ex)
            {
                // If live polls already produced text, keep it and just note the
                // final pass failed; otherwise surface the error.
                if (!string.IsNullOrWhiteSpace(_liveTranscript))
                    ApplyLiveTranscript(focus: true);
                else
                    AddSystemMessage($"שגיאה בתמלול ההקלטה: {ex.Message}");
            }
            finally
            {
                SetTranscribingLoader(false);
                if (MicButton != null)
                {
                    MicButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Writes base text + current transcript into the input box. Dictation
        /// builds on top of whatever the user had already typed.
        /// </summary>
        private void ApplyLiveTranscript(bool focus = false)
        {
            if (InputTextBox == null) return;

            string combined = string.IsNullOrWhiteSpace(_liveBaseText)
                ? _liveTranscript
                : _liveBaseText.TrimEnd() + " " + _liveTranscript;

            InputTextBox.Text = combined;
            InputTextBox.CaretIndex = combined.Length;
            if (focus) InputTextBox.Focus();
        }

        private void SetMicRecordingVisual(bool recording)
        {
            if (MicButton == null) return;
            if (MicIcon != null) MicIcon.Stroke = recording ? MicRecordingStroke : MicIdleStroke;
            MicButton.Background = recording ? MicRecordingBrush : MicIdleBrush;
            MicButton.ToolTip = recording ? "עצור הקלטה" : "הקלטה קולית";
        }

        /// <summary>
        /// Toggles the "מתמלל..." loader shown over the text box while the final
        /// transcription pass runs after the user stops recording.
        /// </summary>
        private void SetTranscribingLoader(bool show)
        {
            if (TranscribingOverlay != null)
                TranscribingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
