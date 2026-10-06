using System;
using System.IO;

namespace MahodAI.Civil3D.Plugin.Config
{
    /// <summary>
    /// Central location for plugin configuration constants.
    /// Previously hardcoded across multiple files.
    /// </summary>
    public static class PluginConstants
    {
        // API
        //
        // THE single source of the agent URL. Both other callers (MahodAgentService,
        // AgentCommunicationService) read this — they each used to carry their own
        // copy of the literal, so a build that pointed one at production still sent
        // REST and file uploads to dev.
        //
        // Selected at COMPILE time by /p:AgentEnvironment=prod (Directory.Build.props),
        // never by an installed env var: setx-ing MAHOD_AGENT_API_URL shadows this and
        // goes stale, which is the recurring "still uses localhost" bug. The env var
        // remains an override for developers, and nothing else.
        //
        // The host names are inverted — "dev.mahodeng.co.il" is PRODUCTION.
#if MAHOD_AGENT_PROD
        public const string DefaultAgentApiUrl = "https://dev.mahodeng.co.il/api";
#else
        public const string DefaultAgentApiUrl = "https://ai.mahodeng.co.il:8088/api";
#endif

        /// <summary>
        /// THE agent URL this process should use: the environment override when it
        /// holds a real value, otherwise the URL compiled into this build.
        /// </summary>
        /// <remarks>
        /// Blank counts as unset. The three call sites used <c>?? DefaultAgentApiUrl</c>,
        /// which only falls back on NULL — so a variable cleared the obvious way
        /// (<c>setx MAHOD_AGENT_API_URL ""</c>) left an EMPTY string that sailed past the
        /// null check and became the base URL, turning "points at the wrong server" into
        /// "points at nothing". Whitespace is trimmed for the same reason.
        /// </remarks>
        public static string ResolveAgentApiUrl()
        {
            var configured = Environment.GetEnvironmentVariable(AgentApiUrlVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? DefaultAgentApiUrl
                : configured.Trim();
        }

        /// <summary>
        /// True when an environment variable is overriding the compiled-in URL.
        /// Surfaced in the connection-failure message: a stale machine-wide
        /// MAHOD_AGENT_API_URL left behind by an old setup-server.bat is the single
        /// most common cause of "it talks to the wrong server", and it is invisible
        /// from inside Civil 3D unless the plugin says so.
        /// </summary>
        public static bool AgentApiUrlIsOverridden =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgentApiUrlVariable));

        public const string AgentApiUrlVariable = "MAHOD_AGENT_API_URL";
        public const int RestTimeoutSeconds = 30;

        // Payload limits
        public const int MaxPayloadSizeBytes = 500 * 1024; // 500KB
        public const int MaxPendingMessages = 1000;

        // WebSocket
        public const int HeartbeatIntervalMs = 30000;
        public const int HeartbeatTimeoutMs = 60000;
        public const int MaxReconnectAttempts = 10;
        public const int InitialReconnectDelayMs = 1000;
        public const int MaxReconnectDelayMs = 30000;

        // Tool execution
        public const int DefaultToolTimeoutSeconds = 30;
        public const int MaxCacheEntries = 1000;
        public const int CacheExpirationMinutes = 30;

        // Logging
        public const int DefaultLogLevel = 3; // Info
        public const long MaxLogFileSizeBytes = 10 * 1024 * 1024; // 10MB
        public const int MaxRotatedLogs = 5;

        // Paths
        public static readonly string LocalAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D");
        public static readonly string LogDir = Path.Combine(LocalAppDataDir, "logs");
        public static readonly string MemoryFilePath = Path.Combine(LocalAppDataDir, "memory.json");
        public static readonly string WebView2CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D_WebView2");
    }
}
