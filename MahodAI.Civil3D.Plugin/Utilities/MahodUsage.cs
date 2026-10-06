#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    /// <summary>
    /// Usage records for Mahod Impact: what engineers did with a desktop product, and the
    /// units of work it completed. They go to MahodAI (<c>POST /api/v1/usage</c>, ai_agent
    /// <c>src/api/routes/usage.py</c>), never to Impact: Impact reads them from MahodAI's
    /// database, so MahodAI is the tools' one door into it (devMahod/mahod-imapct
    /// docs/telemetry.md, "Desktop products").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of record:
    /// an <b>action</b> — one thing the engineer ran (a command, a palette step, a chat tool
    /// call), completed, cancelled or failed, with its run time — and a <b>unit</b> — one
    /// unit of work the product finished (a sheet scanned, a cross-section ruled, a culvert
    /// drawn). Impact prices units with the tool's time model; actions are counted, never
    /// priced. A unit carries a hashed identity of that piece of work (drawing + section, …),
    /// so the board prices a section checked three times in a month once.
    /// </para>
    /// <para>
    /// Nothing here may disturb the host: every call is fire-and-forget and swallows its own
    /// failures. Records go to a queue on the PC (<c>%LOCALAPPDATA%\Mahod\cad-usage</c>) and
    /// are uploaded later on a pool thread — never on AutoCAD's thread, never inside a
    /// command. Without a network or a credential, the queue simply waits (30 days, 5 MB at
    /// most) for a run that can send it.
    /// </para>
    /// <para>
    /// The credential: this PC's MahodAI installation key when it has one (the updater's own,
    /// revocable per PC; it opens the door and is never stored with the records), else the
    /// standalone products' key baked in at build time.
    /// </para>
    /// <para>
    /// What is sent: the product's catalog id, the action or feature name, status, run time,
    /// how the tool was reached and the product version. Never who ran it or on which PC (owner
    /// decision 2026-10-06: track the tools, not the people), and never a drawing name, a path,
    /// a layer, a value or anything typed — the unit identity is a one-way hash made on this PC.
    /// </para>
    /// <para>
    /// SHARED SOURCE, Autodesk-free on purpose: it is linked into the MahodAI plugin and the
    /// standalone Mahod Cross Section / Mahod Visual Scan products, and copied, byte for byte,
    /// into devMahod/Cross-Sections (<c>shared/</c>) and devMahod/MahodCulvert. Keep it BCL-only,
    /// and change it here first, then copy.
    /// </para>
    /// </remarks>
    internal static partial class MahodUsage
    {
        /// <summary>
        /// MahodAI production (the inverted host naming: <c>dev.mahodeng.co.il</c> IS production).
        /// Every build sends here, dev builds included, so test PCs never split the data — they
        /// opt out with <c>MAHOD_USAGE_OFF=1</c> instead. <c>MAHOD_USAGE_URL</c> overrides it
        /// for testing the route itself.
        /// </summary>
        internal const string Endpoint = "https://dev.mahodeng.co.il/api/v1/usage";

        internal const string InstallKeyHeader = "X-Install-Key";
        internal const string ProductKeyHeader = "X-Mahod-Product-Key";

        // How the engineer reached the tool (MahodAI's DOORS, ai_agent src/services/desktop_usage.py).
        internal const string Standalone = "standalone";
        internal const string Command = "command";
        internal const string Chat = "chat";
        internal const string Palette = "palette";

        internal const string Completed = "completed";
        internal const string Cancelled = "cancelled";
        internal const string Failed = "failed";

        internal const int MaxEventsPerBatch = 100;
        private const long MaxQueueBytes = 5L * 1024 * 1024;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(15);

        private static readonly object Gate = new();
        private static readonly string Session = Guid.NewGuid().ToString("N");
        private static int _flushing;
        private static DateTime _lastFlush = DateTime.MinValue;

        /// <summary>
        /// The door of the code running right now on this thread, when its caller knows better
        /// than the code itself: the plugin's chat executor sets <see cref="Chat"/> around a tool
        /// call, so a culvert drawn by <c>culvert_apply</c> is not counted as a palette draw.
        /// </summary>
        [ThreadStatic] internal static string? AmbientDoor;

        /// <summary><see cref="AmbientDoor"/> when set, else <paramref name="fallback"/>.</summary>
        internal static string DoorOr(string fallback) => AmbientDoor ?? fallback;

        /// <summary>Overridable for tests: where the queue lives.</summary>
        internal static string QueueDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mahod", "cad-usage");

        /// <summary>Overridable for tests: false keeps every record on disk.</summary>
        internal static bool UploadEnabled { get; set; } = true;

        /// <summary>One thing the engineer ran with <paramref name="tool"/>.</summary>
        /// <param name="tool">The Impact catalog id (crosssection, visualscan, culvert, …).</param>
        /// <param name="action">Lowercase id of what ran: a tool name or a command name.</param>
        /// <param name="status"><see cref="Completed"/>, <see cref="Cancelled"/> or <see cref="Failed"/>.</param>
        /// <param name="durationMs">How long it ran.</param>
        /// <param name="door">How the tool was reached.</param>
        /// <param name="units">How many units of work it completed (each also sent by <see cref="Unit"/>).</param>
        public static void Action(string tool, string action, string status, long durationMs, string door, int units = 0)
        {
            try
            {
                Append(ActionEvent(tool, action, status, durationMs, door, units, DateTime.UtcNow, Version()));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MahodUsage.Action: " + ex.Message);
            }
        }

        /// <summary>One unit of work <paramref name="tool"/> completed, priced in Impact by <paramref name="feature"/>.</summary>
        /// <param name="tool">The Impact catalog id.</param>
        /// <param name="feature">The catalog feature whose time model prices the unit.</param>
        /// <param name="unitKey">
        /// What makes this unit THIS unit — e.g. the drawing's fingerprint and the section
        /// number. Hashed here, never sent.
        /// </param>
        /// <param name="door">How the tool was reached.</param>
        public static void Unit(string tool, string feature, string unitKey, string door)
        {
            try
            {
                Append(UnitEvent(tool, feature, WorkKey(tool, unitKey), door, DateTime.UtcNow, Version()));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MahodUsage.Unit: " + ex.Message);
            }
        }

        // ── the records (pure, tested) ──────────────────────────────────────

        internal static JsonObject ActionEvent(
            string tool, string action, string status, long durationMs, string door, int units, DateTime utc, string? version)
        {
            var props = new JsonObject { ["door"] = door, ["units"] = Math.Clamp(units, 0, 10_000) };
            if (version != null) props["v"] = version;
            return new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["tool"] = tool,
                ["k"] = "cad_action",
                ["n"] = Name(action),
                ["s"] = status,
                ["ms"] = Math.Clamp(durationMs, 0, 86_400_000),
                ["t"] = Time(utc),
                ["p"] = props,
            };
        }

        internal static JsonObject UnitEvent(string tool, string feature, string workKey, string door, DateTime utc, string? version)
        {
            var props = new JsonObject { ["door"] = door, ["w"] = workKey };
            if (version != null) props["v"] = version;
            return new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["tool"] = tool,
                ["k"] = "cad_unit",
                ["n"] = feature,
                ["t"] = Time(utc),
                ["p"] = props,
            };
        }

        /// <summary>The unit's identity: the first 128 bits of sha256("tool|unitKey"), hex.</summary>
        internal static string WorkKey(string tool, string unitKey)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(tool + "|" + unitKey));
            return string.Concat(hash.Take(16).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// An action id MahodAI accepts (<c>^[a-z][a-z0-9_]{0,39}$</c>): lowercased, every
        /// other character an underscore, at most 40 long.
        /// </summary>
        internal static string Name(string action)
        {
            var chars = (action ?? string.Empty).Trim().ToLowerInvariant()
                .Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_').ToArray();
            var name = new string(chars).Trim('_');
            if (name.Length == 0 || name[0] < 'a' || name[0] > 'z') name = "a_" + name;
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        private static string Time(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The running product's version, e.g. "1.0.7" (the record's <c>v</c>).</summary>
        private static string? Version()
        {
            var v = typeof(MahodUsage).Assembly.GetName().Version;
            return v == null ? null : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }

        // ── the queue ───────────────────────────────────────────────────────

        /// <summary>One queue line: the batch envelope fields (a random id of this AutoCAD session) plus the event.</summary>
        internal static string Line(JsonObject ev, string session) =>
            new JsonObject { ["v"] = 1, ["sid"] = session, ["ev"] = ev }.ToJsonString();

        private static void Append(JsonObject ev)
        {
            // Developer and test machines opt out, so building and testing a tool is never
            // counted as an engineer using it.
            if (Environment.GetEnvironmentVariable("MAHOD_USAGE_OFF") == "1") return;
            lock (Gate)
            {
                Directory.CreateDirectory(QueueDir);
                var file = Path.Combine(QueueDir, $"q-{DateTime.UtcNow:yyyyMMdd}-{Session}.jsonl");
                File.AppendAllText(file, Line(ev, Session) + "\n", new UTF8Encoding(false));
            }
            MaybeFlush();
        }

        // ── the upload ──────────────────────────────────────────────────────

        private static void MaybeFlush()
        {
            if (!UploadEnabled || DateTime.UtcNow - _lastFlush < FlushEvery) return;
            if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
            _lastFlush = DateTime.UtcNow;
            Task.Run(() =>
            {
                try { Flush(); }
                catch (Exception ex) { Debug.WriteLine("MahodUsage.Flush: " + ex.Message); }
                finally { Interlocked.Exchange(ref _flushing, 0); }
            });
        }

        /// <summary>
        /// The standalone products' key: <c>MAHOD_CAD_KEY</c> from the environment, else the one
        /// baked in at build time from the build machine's own <c>MAHOD_CAD_KEY</c> (a generated
        /// <see cref="BakedKey"/>, see Directory.Build.props). It is a filter, not a secret — it
        /// ships inside every copy. Not an assembly attribute on purpose: reading one makes the
        /// CLR walk every assembly-level attribute, <c>[assembly: CommandClass]</c> included
        /// (see PluginBuildInfo).
        /// </summary>
        private static string? ProductKey()
        {
            var env = Environment.GetEnvironmentVariable("MAHOD_CAD_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            string? baked = null;
            BakedKey(ref baked);
            return string.IsNullOrWhiteSpace(baked) ? null : baked!.Trim();
        }

        /// <summary>The MahodAI updater's settings on this PC, which hold its installation key.</summary>
        private static string UpdaterConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MahodAI", "updater", "config.json");

        /// <summary>
        /// The header and value to send: this PC's MahodAI installation key when it has one
        /// (<paramref name="envInstallKey"/> = <c>MAHOD_INSTALL_KEY</c> first, as the updater
        /// reads it, then the updater's <c>installKey</c> in <paramref name="updaterConfigJson"/>),
        /// else the products' key; null when there is neither. Pure, for the tests.
        /// </summary>
        internal static (string Header, string Value)? Credential(string? envInstallKey, string? updaterConfigJson, string? productKey)
        {
            var install = envInstallKey;
            if (string.IsNullOrWhiteSpace(install) && !string.IsNullOrWhiteSpace(updaterConfigJson))
            {
                try { install = (JsonNode.Parse(updaterConfigJson!) as JsonObject)?["installKey"]?.GetValue<string>(); }
                catch (Exception) { install = null; }   // an unreadable config is a PC without a key
            }
            if (!string.IsNullOrWhiteSpace(install)) return (InstallKeyHeader, install!.Trim());
            if (!string.IsNullOrWhiteSpace(productKey)) return (ProductKeyHeader, productKey!.Trim());
            return null;
        }

        private static (string Header, string Value)? Credential()
        {
            string? config = null;
            try { if (File.Exists(UpdaterConfigPath)) config = File.ReadAllText(UpdaterConfigPath); }
            catch { /* locked or unreadable: try the products' key */ }
            return Credential(Environment.GetEnvironmentVariable("MAHOD_INSTALL_KEY"), config, ProductKey());
        }

        private static string Target()
        {
            var url = Environment.GetEnvironmentVariable("MAHOD_USAGE_URL");
            return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.IsLoopback)
                ? uri.ToString()
                : Endpoint;
        }

        /// <summary>Implemented only by a build that had <c>MAHOD_CAD_KEY</c> set; otherwise compiled away.</summary>
        static partial void BakedKey(ref string? key);

        private static readonly Lazy<HttpClient> Http = new(() =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) });

        /// <summary>
        /// Sends every queue file: claim it by renaming (so two AutoCAD sessions never send the
        /// same file), post it in batches, delete on success. A 400/413 batch is dropped —
        /// MahodAI will never accept it — and any other failure (offline, 401 for a key not yet
        /// issued, 429, 5xx) puts the rest back and stops.
        /// </summary>
        private static void Flush()
        {
            if (!Directory.Exists(QueueDir)) return;
            // A claim whose sender died with its AutoCAD session goes back in the queue.
            foreach (var stale in new DirectoryInfo(QueueDir).GetFiles("q-*.jsonl.sending"))
            {
                if (DateTime.UtcNow - stale.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                try { stale.MoveTo(stale.FullName.Substring(0, stale.FullName.Length - ".sending".Length) + ".back.jsonl"); }
                catch { /* still held: next time */ }
            }
            Prune();
            var credential = Credential();
            if (credential == null) return;   // keep the queue for a run that can send it
            var target = Target();

            var started = Stopwatch.StartNew();
            foreach (var file in Directory.GetFiles(QueueDir, "q-*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
            {
                if (started.Elapsed > TimeSpan.FromSeconds(120)) return;
                var claimed = file + ".sending";
                try
                {
                    lock (Gate) File.Move(file, claimed);
                    // A move keeps the old time; the stale-claim rule above reads this one.
                    File.SetLastWriteTimeUtc(claimed, DateTime.UtcNow);
                }
                catch
                {
                    continue;   // another session is writing or sending it
                }

                var lines = File.ReadAllLines(claimed).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                var unsent = new List<string>();
                bool stop = false;
                foreach (var batch in Batches(lines))
                {
                    if (stop)
                    {
                        unsent.AddRange(batch.Lines);
                        continue;
                    }
                    var status = Post(target, batch.Body, credential.Value);
                    if (status == HttpStatusCode.OK || status == HttpStatusCode.BadRequest || status == HttpStatusCode.RequestEntityTooLarge)
                        continue;
                    unsent.AddRange(batch.Lines);
                    stop = true;
                }

                if (unsent.Count > 0)
                {
                    lock (Gate) File.WriteAllText(file + ".retry-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".jsonl",
                        string.Join("\n", unsent) + "\n", new UTF8Encoding(false));
                }
                File.Delete(claimed);
                if (stop) return;
            }
        }

        private static HttpStatusCode? Post(string target, string body, (string Header, string Value) credential)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add(credential.Header, credential.Value);
                using var response = Http.Value.SendAsync(request).GetAwaiter().GetResult();
                return response.StatusCode;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A batch body and the queue lines it was built from.</summary>
        internal sealed class Batch
        {
            public string Body = string.Empty;
            public List<string> Lines = new();
        }

        /// <summary>
        /// Queue lines → request bodies: one body per session, at most
        /// <see cref="MaxEventsPerBatch"/> events each. An unreadable line is dropped. A login or
        /// PC id in a line queued by an earlier build is left out: it is never sent.
        /// </summary>
        internal static IEnumerable<Batch> Batches(IEnumerable<string> lines)
        {
            var groups = new Dictionary<string, (JsonObject Envelope, List<(string Line, JsonNode Ev)> Items)>();
            foreach (var line in lines)
            {
                JsonObject? parsed;
                try { parsed = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (parsed?["ev"] is not JsonObject ev) continue;

                var envelope = new JsonObject
                {
                    ["v"] = 1,
                    ["sid"] = parsed["sid"]?.GetValue<string>(),
                };
                var group = envelope.ToJsonString();
                if (!groups.TryGetValue(group, out var g))
                    groups[group] = g = (envelope, new List<(string, JsonNode)>());
                g.Items.Add((line, ev.DeepClone()));
            }

            foreach (var (envelope, items) in groups.Values)
            {
                for (int i = 0; i < items.Count; i += MaxEventsPerBatch)
                {
                    var chunk = items.Skip(i).Take(MaxEventsPerBatch).ToList();
                    var body = (JsonObject)envelope.DeepClone();
                    body["events"] = new JsonArray(chunk.Select(c => c.Ev.DeepClone()).ToArray());
                    yield return new Batch { Body = body.ToJsonString(), Lines = chunk.Select(c => c.Line).ToList() };
                }
            }
        }

        /// <summary>Drops queue files MahodAI would refuse (older than 30 days) and caps the folder at 5 MB.</summary>
        private static void Prune()
        {
            var files = new DirectoryInfo(QueueDir).GetFiles("q-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge || total > MaxQueueBytes)
                {
                    try { f.Delete(); } catch { /* in use: next time */ }
                }
            }
        }
    }
}
