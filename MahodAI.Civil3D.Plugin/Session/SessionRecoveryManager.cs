using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Session
{
    /// <summary>
    /// Manages session state persistence for recovery after reconnection.
    /// Stores session info per drawing for quick restoration.
    /// </summary>
    public class SessionRecoveryManager
    {
        private readonly string _storagePath;
        private readonly Dictionary<string, SessionState> _activeSessions = new();
        private readonly object _lock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Creates a new session recovery manager.
        /// </summary>
        public SessionRecoveryManager(string? storagePath = null)
        {
            _storagePath = storagePath ?? GetDefaultStoragePath();
            EnsureStorageDirectory();
            LoadPersistedSessions();
        }

        private static string GetDefaultStoragePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, Constants.AppConstants.AppDataFolder, "sessions");
        }

        private void EnsureStorageDirectory()
        {
            try
            {
                Directory.CreateDirectory(_storagePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create session storage: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves a session state for a drawing.
        /// </summary>
        public void SaveSession(string drawingId, string sessionId, SessionMetadata metadata)
        {
            lock (_lock)
            {
                var state = new SessionState
                {
                    DrawingId = drawingId,
                    SessionId = sessionId,
                    Metadata = metadata,
                    CreatedAt = DateTime.UtcNow,
                    LastActiveAt = DateTime.UtcNow
                };

                _activeSessions[drawingId] = state;
                _ = PersistSessionAsync(state);
            }
        }

        /// <summary>
        /// Updates the last active timestamp for a session.
        /// </summary>
        public void TouchSession(string drawingId)
        {
            lock (_lock)
            {
                if (_activeSessions.TryGetValue(drawingId, out var state))
                {
                    state.LastActiveAt = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// Gets the session state for a drawing.
        /// </summary>
        public SessionState? GetSession(string drawingId)
        {
            lock (_lock)
            {
                _activeSessions.TryGetValue(drawingId, out var state);
                return state;
            }
        }

        /// <summary>
        /// Checks if a session exists for a drawing and is still valid.
        /// </summary>
        public bool HasValidSession(string drawingId, TimeSpan maxAge)
        {
            lock (_lock)
            {
                if (_activeSessions.TryGetValue(drawingId, out var state))
                {
                    return DateTime.UtcNow - state.LastActiveAt < maxAge;
                }
                return false;
            }
        }

        /// <summary>
        /// Removes a session for a drawing.
        /// </summary>
        public void RemoveSession(string drawingId)
        {
            lock (_lock)
            {
                _activeSessions.Remove(drawingId);
                DeletePersistedSession(drawingId);
            }
        }

        /// <summary>
        /// Gets all active sessions.
        /// </summary>
        public IEnumerable<SessionState> GetAllSessions()
        {
            lock (_lock)
            {
                return _activeSessions.Values.ToList();
            }
        }

        /// <summary>
        /// Cleans up expired sessions.
        /// </summary>
        public void CleanupExpiredSessions(TimeSpan maxAge)
        {
            lock (_lock)
            {
                var expired = _activeSessions
                    .Where(kvp => DateTime.UtcNow - kvp.Value.LastActiveAt > maxAge)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expired)
                {
                    _activeSessions.Remove(key);
                    DeletePersistedSession(key);
                }
            }
        }

        private async Task PersistSessionAsync(SessionState state)
        {
            try
            {
                var filePath = GetSessionFilePath(state.DrawingId);
                var json = JsonSerializer.Serialize(state, JsonOptions);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist session: {ex.Message}");
            }
        }

        private void DeletePersistedSession(string drawingId)
        {
            try
            {
                var filePath = GetSessionFilePath(drawingId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete session file: {ex.Message}");
            }
        }

        private void LoadPersistedSessions()
        {
            try
            {
                if (!Directory.Exists(_storagePath)) return;

                foreach (var file in Directory.GetFiles(_storagePath, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
                        if (state != null && !string.IsNullOrEmpty(state.DrawingId))
                        {
                            _activeSessions[state.DrawingId] = state;
                        }
                    }
                    catch
                    {
                        // Skip invalid files
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load sessions: {ex.Message}");
            }
        }

        private string GetSessionFilePath(string drawingId)
        {
            // Create safe filename from drawing ID
            var safeId = string.Join("_", drawingId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storagePath, $"{safeId}.json");
        }
    }

    /// <summary>
    /// Persisted session state.
    /// </summary>
    public class SessionState
    {
        /// <summary>
        /// Drawing identifier.
        /// </summary>
        public string DrawingId { get; set; } = string.Empty;

        /// <summary>
        /// Session ID from the agent.
        /// </summary>
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Session metadata.
        /// </summary>
        public SessionMetadata? Metadata { get; set; }

        /// <summary>
        /// When the session was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Last activity timestamp.
        /// </summary>
        public DateTime LastActiveAt { get; set; }

        /// <summary>
        /// Chat history summary (for context restoration).
        /// </summary>
        public List<ChatHistoryItem>? ChatHistory { get; set; }
    }

    /// <summary>
    /// Session metadata.
    /// </summary>
    public class SessionMetadata
    {
        /// <summary>
        /// Drawing file name.
        /// </summary>
        public string DrawingName { get; set; } = string.Empty;

        /// <summary>
        /// Drawing file path.
        /// </summary>
        public string? DrawingPath { get; set; }

        /// <summary>
        /// WebSocket connection ID.
        /// </summary>
        public string? ConnectionId { get; set; }

        /// <summary>
        /// Custom metadata.
        /// </summary>
        public Dictionary<string, string>? Custom { get; set; }
    }

    /// <summary>
    /// Chat history item for context restoration.
    /// </summary>
    public class ChatHistoryItem
    {
        /// <summary>
        /// Message role (user, assistant).
        /// </summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Message content (summarized).
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Message timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; }
    }
}
