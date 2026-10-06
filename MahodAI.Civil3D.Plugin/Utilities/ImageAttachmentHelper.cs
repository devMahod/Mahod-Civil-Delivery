using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MahodAI.Civil3D.Plugin.Utilities
{
    /// <summary>
    /// One image the engineer attached to the next question (PROTOCOL v1.16).
    /// </summary>
    /// <remarks>
    /// Carries bytes rather than a file path on purpose: a pasted screenshot and a
    /// captured region have no path, and the previous attachment model
    /// (<c>List&lt;string&gt;</c> of paths) could not represent them at all.
    /// </remarks>
    public sealed class PendingImage
    {
        public string Name { get; set; } = "image.png";
        public string MimeType { get; set; } = "image/png";
        /// <summary>Base64 WITHOUT a data: URI prefix — the wire form.</summary>
        public string Base64 { get; set; } = string.Empty;
        /// <summary>On-disk copy used ONLY to show the thumbnail in the transcript.</summary>
        public string PreviewPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// One document attached to the next question (PROTOCOL v1.17).
    /// </summary>
    /// <remarks>
    /// Sent whole rather than extracted to text, so the model reads the PDF's
    /// tables and figures directly and a scanned PDF needs no OCR pass of ours.
    /// DOCX/XLSX are NOT here — no provider accepts them (measured: hard 400),
    /// so they keep using the extract-to-text upload route.
    /// </remarks>
    public sealed class PendingDocument
    {
        public string Name { get; set; } = "document.pdf";
        public string MimeType { get; set; } = "application/pdf";
        /// <summary>Base64 WITHOUT a data: URI prefix — the wire form.</summary>
        public string Base64 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Loads, downscales and encodes images for chat attachments.
    /// </summary>
    public static class ImageAttachmentHelper
    {
        /// <summary>
        /// Longest edge we send. Every vision provider downsamples above roughly
        /// this, so a larger upload costs latency and buys no extra detail.
        /// </summary>
        public const int MaxEdgePixels = 1568;

        /// <summary>Server cap (ATTACHMENT_MAX_BYTES_PER_IMAGE) is 4MB of base64.</summary>
        public const int MaxBase64Bytes = 4 * 1024 * 1024;

        /// <summary>Server cap (ATTACHMENT_MAX_PER_MESSAGE).</summary>
        public const int MaxImagesPerMessage = 4;

        public static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

        public static bool IsImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(ImageExtensions, ext) >= 0;
        }

        /// <summary>Folder the WebView2 virtual host serves thumbnails from.</summary>
        public static string PreviewFolder
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D", "attachments");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Reads an image file, downscales it and returns the attachment form.
        /// Returns null (with <paramref name="error"/> set, Hebrew) if the file
        /// cannot be decoded or stays too large after scaling.
        /// </summary>
        public static PendingImage? FromFile(string path, out string? error)
        {
            error = null;
            try
            {
                var source = LoadBitmap(File.ReadAllBytes(path));
                return Encode(source, Path.GetFileName(path), out error);
            }
            catch (Exception ex)
            {
                error = $"לא ניתן לקרוא את התמונה {Path.GetFileName(path)}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Builds an attachment from a clipboard/drop <see cref="BitmapSource"/>
        /// (a pasted screenshot has no file of its own).
        /// </summary>
        public static PendingImage? FromBitmapSource(BitmapSource source, string name, out string? error)
        {
            error = null;
            try
            {
                return Encode(source, name, out error);
            }
            catch (Exception ex)
            {
                error = $"לא ניתן לקרוא את התמונה שהודבקה: {ex.Message}";
                return null;
            }
        }

        private static BitmapSource LoadBitmap(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        private static PendingImage? Encode(BitmapSource source, string name, out string? error)
        {
            error = null;

            var scale = Math.Min(1.0,
                (double)MaxEdgePixels / Math.Max(source.PixelWidth, source.PixelHeight));

            BitmapSource scaled = source;
            if (scale < 1.0)
            {
                var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                transformed.Freeze();
                scaled = transformed;
            }

            // PNG first: CAD linework and small Hebrew text survive lossless
            // encoding far better than JPEG. Fall back only when PNG is too big.
            var bytes = EncodePng(scaled);
            var mime = "image/png";
            if (Base64Length(bytes.Length) > MaxBase64Bytes)
            {
                bytes = EncodeJpeg(scaled);
                mime = "image/jpeg";
                if (Base64Length(bytes.Length) > MaxBase64Bytes)
                {
                    error = $"התמונה {name} גדולה מדי גם לאחר הקטנה.";
                    return null;
                }
            }

            var safeName = MakeSafeName(name, mime);
            var previewPath = Path.Combine(PreviewFolder, $"{Guid.NewGuid():N}_{safeName}");
            try
            {
                File.WriteAllBytes(previewPath, bytes);
            }
            catch
            {
                // A thumbnail we cannot write is cosmetic — never block the send.
                previewPath = string.Empty;
            }

            return new PendingImage
            {
                Name = safeName,
                MimeType = mime,
                Base64 = Convert.ToBase64String(bytes),
                PreviewPath = previewPath
            };
        }

        /// <summary>Base64 length for a byte count, without allocating the string.</summary>
        private static int Base64Length(int byteCount) => ((byteCount + 2) / 3) * 4;

        private static byte[] EncodePng(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] EncodeJpeg(BitmapSource source)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// File name safe for both the transcript URL and the file system, with an
        /// extension matching what we actually encoded.
        /// </summary>
        private static string MakeSafeName(string name, string mime)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(stem)) stem = "image";
            foreach (var c in Path.GetInvalidFileNameChars())
                stem = stem.Replace(c, '_');
            // Spaces and '#' break the virtual-host URL the transcript uses.
            stem = stem.Replace(' ', '_').Replace('#', '_').Replace('%', '_');
            var ext = mime == "image/jpeg" ? ".jpg" : ".png";
            return stem + ext;
        }

        /// <summary>
        /// Deletes preview files older than a day. Called on startup — the folder
        /// is a cache, and without this it grows for the life of the install.
        /// </summary>
        public static void PrunePreviews()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var file in Directory.GetFiles(PreviewFolder))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch { /* in use by an open transcript — try again tomorrow */ }
                }
            }
            catch { /* the cache folder is optional */ }
        }

        /// <summary>Wire form for a batch of pending images.</summary>
        public static List<WebSocket.ChatAttachment> ToAttachments(IEnumerable<PendingImage> images)
        {
            var list = new List<WebSocket.ChatAttachment>();
            foreach (var img in images)
            {
                list.Add(new WebSocket.ChatAttachment
                {
                    Kind = "image",
                    Filename = img.Name,
                    MimeType = img.MimeType,
                    Data = img.Base64
                });
            }
            return list;
        }

        // ─── Documents (PROTOCOL v1.17) ──────────────────────────────────

        /// <summary>Server cap (ATTACHMENT_MAX_BYTES_PER_DOCUMENT).</summary>
        public const int MaxDocumentBytes = 6 * 1024 * 1024;

        /// <summary>Server cap (ATTACHMENT_MAX_DOCUMENTS_PER_MESSAGE).</summary>
        public const int MaxDocumentsPerMessage = 2;

        /// <summary>
        /// Extension → MIME for the formats a provider takes as a real file.
        /// DOCX/XLS/DOC deliberately absent — see <see cref="PendingDocument"/>.
        /// </summary>
        private static readonly Dictionary<string, string> DocumentMimeByExtension =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".pdf"] = "application/pdf",
                [".txt"] = "text/plain",
                [".md"] = "text/markdown",
                [".csv"] = "text/csv",
            };

        /// <summary>MIME for a natively attachable document, else null.</summary>
        public static string? DocumentMimeFor(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var ext = Path.GetExtension(path);
            return DocumentMimeByExtension.TryGetValue(ext ?? string.Empty, out var mime)
                ? mime
                : null;
        }

        /// <summary>
        /// True when this file should travel whole instead of being extracted.
        /// Oversized files return false so the caller keeps the upload route.
        /// </summary>
        public static bool IsNativeDocument(string path)
        {
            if (DocumentMimeFor(path) == null) return false;
            try
            {
                return new FileInfo(path).Length <= MaxDocumentBytes;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reads a document for attachment. Returns null (with a Hebrew
        /// <paramref name="error"/>) if it cannot be read or is too large.
        /// </summary>
        public static PendingDocument? DocumentFromFile(string path, out string? error)
        {
            error = null;
            var mime = DocumentMimeFor(path);
            if (mime == null)
            {
                error = $"סוג הקובץ {Path.GetFileName(path)} אינו נתמך לצירוף ישיר.";
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxDocumentBytes)
                {
                    error = $"המסמך {Path.GetFileName(path)} גדול מדי "
                          + $"(מקסימום {MaxDocumentBytes / (1024 * 1024)}MB).";
                    return null;
                }
                return new PendingDocument
                {
                    Name = Path.GetFileName(path),
                    MimeType = mime,
                    Base64 = Convert.ToBase64String(bytes)
                };
            }
            catch (Exception ex)
            {
                error = $"לא ניתן לקרוא את {Path.GetFileName(path)}: {ex.Message}";
                return null;
            }
        }

        /// <summary>Wire form for a batch of pending documents.</summary>
        public static List<WebSocket.ChatAttachment> ToAttachments(
            IEnumerable<PendingDocument> documents)
        {
            var list = new List<WebSocket.ChatAttachment>();
            foreach (var doc in documents)
            {
                list.Add(new WebSocket.ChatAttachment
                {
                    Kind = "document",
                    Filename = doc.Name,
                    MimeType = doc.MimeType,
                    Data = doc.Base64
                });
            }
            return list;
        }
    }
}
