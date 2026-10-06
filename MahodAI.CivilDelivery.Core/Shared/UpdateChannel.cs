using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// "Is there a newer version?" — the decision logic, with no network and no file system,
    /// so every branch is testable. The plugin fetches the manifest text (from a network
    /// share or an HTTPS URL) and hands it here.
    ///
    /// The product never installs an update by itself: an installer cannot run while Civil
    /// 3D is open, and silently replacing an engineer's tooling mid-project is not something
    /// a tool should do. The most this does is tell her what changed and put a verified file
    /// where she can find it.
    /// </summary>
    public static class UpdateChannel
    {
        /// <summary>What a published manifest says. Unknown fields are ignored on purpose.</summary>
        public sealed class Manifest
        {
            [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; } = 1;
            [JsonPropertyName("version")] public string? Version { get; set; }
            [JsonPropertyName("released_utc")] public string? ReleasedUtc { get; set; }
            [JsonPropertyName("setup_file")] public string? SetupFile { get; set; }
            [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
            [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
            [JsonPropertyName("min_civil")] public string? MinCivil { get; set; }
        }

        public enum State
        {
            /// <summary>No channel is configured — the engineer is told, not left guessing.</summary>
            NotConfigured,

            /// <summary>The channel could not be read (offline, no permission, wrong path).</summary>
            Unreachable,

            /// <summary>The channel answered with something this build cannot trust.</summary>
            Invalid,

            UpToDate,
            UpdateAvailable,
        }

        public sealed record Result(State State, string Headline, string Detail, Manifest? Manifest)
        {
            public bool CanDownload => State == State.UpdateAvailable
                                       && !string.IsNullOrWhiteSpace(Manifest?.SetupFile)
                                       && !string.IsNullOrWhiteSpace(Manifest?.Sha256);
        }

        /// <summary>Decides what to tell the engineer. <paramref name="manifestJson"/> null = unreachable.</summary>
        public static Result Evaluate(string installedVersion, string? channel, string? manifestJson)
        {
            if (string.IsNullOrWhiteSpace(channel))
                return new Result(State.NotConfigured,
                    "לא הוגדר ערוץ עדכונים",
                    "כשמהוד תפרסם ערוץ עדכונים, ייווצר כאן קובץ קטן בשם update-channel.json והכפתור " +
                    "יתחיל לעבוד. עד אז אפשר לקבל גרסאות חדשות ישירות מהתמיכה.",
                    null);

            if (manifestJson == null)
                return new Result(State.Unreachable,
                    "לא ניתן היה לבדוק עדכונים",
                    $"הערוץ אינו נגיש כרגע: {channel}. אפשר לנסות שוב מאוחר יותר — הכלי המותקן ממשיך לעבוד כרגיל.",
                    null);

            Manifest? m;
            try
            {
                m = JsonSerializer.Deserialize<Manifest>(manifestJson);
            }
            catch (Exception ex)
            {
                return new Result(State.Invalid, "תשובת ערוץ העדכונים אינה תקינה", ex.Message, null);
            }

            if (m == null || string.IsNullOrWhiteSpace(m.Version))
                return new Result(State.Invalid, "תשובת ערוץ העדכונים אינה תקינה",
                    "חסר מספר גרסה במניפסט.", null);

            if (m.SchemaVersion > 1)
                return new Result(State.Invalid, "ערוץ העדכונים חדש מהכלי המותקן",
                    "יש לעדכן ידנית — פנה לתמיכה של מהוד.", m);

            var cmp = CompareVersions(m.Version, installedVersion);
            if (cmp <= 0)
                return new Result(State.UpToDate,
                    $"הגרסה המותקנת ({installedVersion}) היא העדכנית ביותר",
                    m.ReleasedUtc is { Length: > 0 } r ? $"הגרסה שפורסמה: {m.Version} ({r})" : $"הגרסה שפורסמה: {m.Version}",
                    m);

            var notes = m.Notes.Count > 0
                ? string.Join(Environment.NewLine, m.Notes.Take(12).Select(n => "• " + n))
                : "לא פורסמו הערות גרסה.";
            return new Result(State.UpdateAvailable,
                $"קיימת גרסה חדשה: {m.Version} (מותקן: {installedVersion})",
                notes, m);
        }

        /// <summary>
        /// Dotted numeric comparison ("1.10.0" &gt; "1.9.0"). Non-numeric suffixes are ignored
        /// rather than guessed at: a version this build cannot parse never triggers an update.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            var pa = Parse(a);
            var pb = Parse(b);
            for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
            {
                var x = i < pa.Count ? pa[i] : 0;
                var y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;

            static List<int> Parse(string v) => (v ?? "")
                .Split('.', '-', '+')
                .Select(p => int.TryParse(p, out var n) ? n : (int?)null)
                .TakeWhile(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
        }

        /// <summary>Where the manifest lives for a given channel root (share path or URL).</summary>
        public static string ManifestLocation(string channel) =>
            channel.TrimEnd('/', '\\') + (channel.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "/" : "\\") + "latest.json";

        /// <summary>Where the setup file lives, given the channel and the manifest.</summary>
        public static string SetupLocation(string channel, Manifest m) =>
            channel.TrimEnd('/', '\\') + (channel.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "/" : "\\") + m.SetupFile;
    }
}
