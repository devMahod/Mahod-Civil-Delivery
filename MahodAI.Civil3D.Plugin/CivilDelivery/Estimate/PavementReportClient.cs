using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    internal static partial class MahodUsage
    {
        /// <summary>The credential the usage records use (installation key, else the products' key): the pavement-report
        /// route on the same server accepts exactly these, in both editions.</summary>
        internal static (string Header, string Value)? ServerCredential() => Credential();
    }
}

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Sends a pavement-structure report to MahodAI (<c>POST /api/v1/boq/pavement-report</c>), where the model reads it and
    /// the server verifies it. The file goes as it is; nothing is read or parsed here. A failure is a
    /// <see cref="PavementReportException"/> whose message is the server's Hebrew text, shown to the engineer as is.
    /// </summary>
    internal static class PavementReportClient
    {
        /// <summary>MahodAI production (dev.mahodeng.co.il IS production). <c>MAHOD_PAVEMENT_REPORT_URL</c> overrides it,
        /// https or loopback only, for testing the route itself.</summary>
        internal const string Endpoint = "https://dev.mahodeng.co.il/api/v1/boq/pavement-report";
        internal const long MaxBytes = 20L * 1024 * 1024;

        private static readonly Lazy<HttpClient> Http = new(() =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan });

        internal static Uri Target()
        {
            var url = Environment.GetEnvironmentVariable("MAHOD_PAVEMENT_REPORT_URL");
            return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.IsLoopback) ? uri : new Uri(Endpoint);
        }

        internal static string? MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => null,
        };

        /// <summary>Reads one report. Cancelled by <paramref name="cancel"/>; gives up after four minutes.</summary>
        internal static async Task<AttachedPavementReport> ReadAsync(string path, CancellationToken cancel)
        {
            var mime = MimeType(path) ?? throw new PavementReportException(
                Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase)
                    ? "קובץ Word — פתח אותו ושמור בשם PDF, ואז צרף את ה־PDF"
                    : "יש לצרף את הדו״ח כקובץ PDF (או תמונה PNG/JPG)");
            var info = new FileInfo(path);
            if (!info.Exists) throw new PavementReportException("הקובץ לא נמצא: " + path);
            if (info.Length == 0) throw new PavementReportException("הקובץ ריק");
            if (info.Length > MaxBytes) throw new PavementReportException($"הקובץ גדול מדי ({info.Length / 1048576d:N1} MB, עד 20 MB)");
            var data = await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false);
            var credential = MahodAI.Civil3D.Plugin.Utilities.MahodUsage.ServerCredential()
                ?? throw new PavementReportException("אין במחשב זה מפתח התקנה של MahodAI — לא ניתן לשלוח את הדו״ח לקריאה");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            using var request = new HttpRequestMessage(HttpMethod.Post, Target()) { Content = new ByteArrayContent(data) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
            request.Headers.TryAddWithoutValidation(credential.Header, credential.Value);
            request.Headers.TryAddWithoutValidation("X-File-Name", Uri.EscapeDataString(Path.GetFileName(path)));
            HttpResponseMessage response;
            try { response = await Http.Value.SendAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            { throw new PavementReportException("קריאת הדו״ח ארכה יותר מדי — נסה שוב"); }
            catch (HttpRequestException ex)
            { throw new PavementReportException("אין חיבור לשרת MahodAI — בדוק את החיבור לרשת ונסה שוב (" + ex.Message + ")"); }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                return Parse(response.StatusCode, body, path, data);
            }
        }

        /// <summary>The server's answer as an attached report, or the server's own message. Pure, for the tests.</summary>
        internal static AttachedPavementReport Parse(HttpStatusCode status, string body, string path, byte[] data)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                throw new PavementReportException(status == HttpStatusCode.OK ? "תשובת השרת אינה קריאה"
                    : $"שרת MahodAI השיב {(int)status} — שירות קריאת הדו״חות אינו זמין כעת");
            }
            using (document)
            {
                var root = document.RootElement;
                if (status != HttpStatusCode.OK || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                {
                    var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    if (message == null && status == HttpStatusCode.NotFound) message = "שרת MahodAI עדיין אינו תומך בקריאת דו״חות מבנה (נדרש עדכון שרת)";
                    throw new PavementReportException(message ?? $"שרת MahodAI השיב {(int)status}",
                        root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
                }
                PavementReportContent content;
                try { content = PavementReportJson.ParseContent(root.GetProperty("report")); }
                catch (InvalidDataException ex) { throw new PavementReportException(ex.Message, "no_structures"); }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                { throw new PavementReportException("תשובת השרת אינה בפורמט הצפוי — " + ex.Message); }
                string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                int? pages = root.TryGetProperty("pages", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
                var warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                    ? w.Deserialize<string[]>() ?? Array.Empty<string>() : Array.Empty<string>();
                return new AttachedPavementReport(Path.GetFileName(path), PavementReportJson.Sha256(data), DateTime.UtcNow,
                    Text("model"), Text("prompt_version"), pages, content, warnings);
            }
        }
    }

    internal sealed class PavementReportException : Exception
    {
        internal string? Code { get; }
        internal PavementReportException(string message, string? code = null) : base(message) => Code = code;
    }
}
