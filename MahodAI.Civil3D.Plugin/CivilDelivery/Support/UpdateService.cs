using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Support
{
    /// <summary>
    /// Reads Mahod's update channel — a network share or an HTTPS folder — and, when the
    /// engineer asks for it, downloads the published setup and verifies it against the
    /// SHA-256 in the manifest.
    ///
    /// It never runs the installer: an installer cannot execute while Civil 3D is open, and a
    /// tool that replaces itself behind an engineer's back is not one you would trust with a
    /// project. The file is placed in Downloads and shown to her; running it stays her call.
    /// </summary>
    public static class UpdateService
    {
        /// <summary>Where the channel is configured, next to the engineer's project profile.</summary>
        public static string ChannelSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "update-channel.json");

        private sealed class ChannelSettings
        {
            [System.Text.Json.Serialization.JsonPropertyName("channel")]
            public string? Channel { get; set; }
        }

        /// <summary>
        /// System.Text.Json matches property names case-sensitively by default, so a settings
        /// file written as {"channel": …} silently read as "no channel configured" (caught live
        /// 2026-08-20). Both spellings are accepted now, and the file is hand-editable.
        /// </summary>
        private static readonly JsonSerializerOptions SettingsJson = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>The configured channel root, or null when the package shipped without one.</summary>
        public static string? ReadChannel()
        {
            try
            {
                if (!File.Exists(ChannelSettingsPath)) return null;
                var s = JsonSerializer.Deserialize<ChannelSettings>(File.ReadAllText(ChannelSettingsPath), SettingsJson);
                return string.IsNullOrWhiteSpace(s?.Channel) ? null : s!.Channel!.Trim();
            }
            catch { return null; }
        }

        /// <summary>Installed package revision, from the receipt the installer wrote.</summary>
        public static string InstalledVersion()
        {
            try
            {
                var receipt = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D", "civil-delivery", "install_state.json");
                if (!File.Exists(receipt)) return "0.0.0";
                using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
                return doc.RootElement.TryGetProperty("package_revision", out var v)
                    ? v.GetString() ?? "0.0.0"
                    : "0.0.0";
            }
            catch { return "0.0.0"; }
        }

        /// <summary>Fetches the manifest text. Returns null when the channel cannot be read.</summary>
        public static string? FetchManifest(string channel, TimeSpan timeout)
        {
            var location = UpdateChannel.ManifestLocation(channel);
            try
            {
                // https only: a channel reachable over plain http could be redirected by
                // anyone on the network into handing the engineer a different installer.
                if (channel.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient { Timeout = timeout };
                    return http.GetStringAsync(location).GetAwaiter().GetResult();
                }
                if (channel.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(location) ? File.ReadAllText(location) : null;
            }
            catch { return null; }
        }

        public sealed record DownloadResult(bool Ok, string Path, string Message);

        /// <summary>
        /// Downloads the published setup into Downloads and checks its SHA-256 against the
        /// fingerprint in the manifest. A file whose hash does not match is deleted, not offered.
        ///
        /// This is an INTEGRITY check, not authenticity: it proves the file arrived intact and
        /// matches what the manifest says, not who produced it. The installer carries no
        /// Authenticode signature, so nothing here may be described to the engineer as a
        /// verified digital signature.
        /// </summary>
        public static DownloadResult Download(string channel, UpdateChannel.Manifest manifest)
        {
            var source = UpdateChannel.SetupLocation(channel, manifest);
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", manifest.SetupFile!);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (channel.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    var bytes = http.GetByteArrayAsync(source).GetAwaiter().GetResult();
                    File.WriteAllBytes(target, bytes);
                }
                else if (channel.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    return new DownloadResult(false, target, "ערוץ עדכונים לא מאובטח (http) — ההורדה נחסמה.");
                }
                else
                {
                    File.Copy(source, target, true);
                }
            }
            catch (Exception ex)
            {
                return new DownloadResult(false, target, "ההורדה נכשלה: " + ex.Message);
            }

            var actual = ArtifactHash.Sha256OfFile(target);
            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(target); } catch { }
                return new DownloadResult(false, target,
                    "הקובץ שהתקבל אינו תואם לטביעת ה-SHA-256 שפורסמה — ההורדה בוטלה ולא נשמר דבר.\n" +
                    $"צפוי {manifest.Sha256}\nבפועל {actual}");
            }

            return new DownloadResult(true, target,
                "הקובץ הורד ושלמותו אומתה מול טביעת ה-SHA-256 שפורסמה.\n\n" +
                "כדי להתקין: לסגור את Civil 3D ולהריץ אותו. ההגדרות והמיפויים שלך נשמרים.");
        }
    }
}
