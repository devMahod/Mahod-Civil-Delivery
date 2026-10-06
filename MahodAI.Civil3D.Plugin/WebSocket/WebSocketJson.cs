using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahodAI.Civil3D.Plugin.WebSocket
{
    /// <summary>
    /// THE canonical System.Text.Json configuration for the MahodAI WebSocket
    /// protocol.
    ///
    /// PROTOCOL.md §Envelope: "snake_case field names everywhere (plugin uses
    /// <c>JsonNamingPolicy.SnakeCaseLower</c>)".
    ///
    /// Both wire paths use this single instance — <see cref="MahodWebSocketClient"/>
    /// (send) and <see cref="MessageDispatcher"/> (receive) — and so does the
    /// protocol contract test suite. A test that hand-rolled its own options could
    /// pass while production put something else on the wire; pointing the tests at
    /// the production object removes that failure mode.
    /// </summary>
    public static class WebSocketJson
    {
        /// <summary>Serializer options used for every WebSocket envelope and payload.</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }
}
