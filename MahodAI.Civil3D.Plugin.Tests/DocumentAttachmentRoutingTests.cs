using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Utilities;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Which files travel WHOLE to the model and which keep the older
    /// extract-to-text upload route (PROTOCOL v1.17).
    /// </summary>
    /// <remarks>
    /// The routing is easy to get subtly wrong and the failure is silent — a
    /// DOCX sent as a document attachment is refused by the provider with a hard
    /// 400, and an oversized PDF blows the inline-request ceiling. Both would
    /// surface to the engineer as "the assistant ignored my file".
    /// </remarks>
    public class DocumentAttachmentRoutingTests
    {
        [Theory]
        [InlineData("spec.pdf", "application/pdf")]
        [InlineData("notes.txt", "text/plain")]
        [InlineData("readme.md", "text/markdown")]
        [InlineData("table.csv", "text/csv")]
        [InlineData("SPEC.PDF", "application/pdf")]   // extension match is case-insensitive
        public void NativeDocumentTypes_MapToTheirMime(string name, string expected)
        {
            ImageAttachmentHelper.DocumentMimeFor(name).Should().Be(expected);
        }

        [Theory]
        [InlineData("programme.docx")]
        [InlineData("budget.xlsx")]
        [InlineData("old.doc")]
        [InlineData("sheet.xls")]
        [InlineData("plan.dwg")]
        [InlineData("noextension")]
        public void FormatsNoProviderAccepts_AreNotNativeDocuments(string name)
        {
            // Measured 2026-08-12: Gemini refuses each of these inline with a
            // hard 400 "Unsupported MIME type".
            ImageAttachmentHelper.DocumentMimeFor(name).Should().BeNull();
        }

        [Fact]
        public void AnImage_IsNotADocument()
        {
            ImageAttachmentHelper.DocumentMimeFor("sketch.png").Should().BeNull();
            ImageAttachmentHelper.IsImagePath("sketch.png").Should().BeTrue();
        }

        [Fact]
        public void ASmallPdf_IsAttachedWholeAndBase64Encoded()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
            var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };   // "%PDF"
            File.WriteAllBytes(path, bytes);

            try
            {
                ImageAttachmentHelper.IsNativeDocument(path).Should().BeTrue();

                var doc = ImageAttachmentHelper.DocumentFromFile(path, out var error);
                error.Should().BeNull();
                doc.Should().NotBeNull();
                doc!.MimeType.Should().Be("application/pdf");
                doc.Base64.Should().Be(System.Convert.ToBase64String(bytes));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AnOversizedDocument_FallsBackToTheUploadRoute()
        {
            // Over the cap it must NOT be inlined — the request would exceed the
            // provider's inline ceiling. IsNativeDocument returning false is what
            // sends it down the extract-to-text path instead.
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");
            File.WriteAllBytes(path, new byte[ImageAttachmentHelper.MaxDocumentBytes + 1024]);

            try
            {
                ImageAttachmentHelper.IsNativeDocument(path).Should().BeFalse();

                var doc = ImageAttachmentHelper.DocumentFromFile(path, out var error);
                doc.Should().BeNull();
                error.Should().NotBeNullOrEmpty();
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AMissingFile_IsNotNativeAndReportsAnError()
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".pdf");

            ImageAttachmentHelper.IsNativeDocument(path).Should().BeFalse();
            ImageAttachmentHelper.DocumentFromFile(path, out var error).Should().BeNull();
            error.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void ToAttachments_EmitsTheDocumentWireShape()
        {
            var docs = new System.Collections.Generic.List<PendingDocument>
            {
                new PendingDocument
                {
                    Name = "guidelines.pdf",
                    MimeType = "application/pdf",
                    Base64 = "JVBERg=="
                }
            };

            var wire = ImageAttachmentHelper.ToAttachments(docs);

            wire.Should().HaveCount(1);
            wire[0].Kind.Should().Be("document");
            wire[0].Filename.Should().Be("guidelines.pdf");
            wire[0].MimeType.Should().Be("application/pdf");
            wire[0].Data.Should().Be("JVBERg==");
        }

        [Fact]
        public void ToAttachments_EmitsTheImageWireShapeSeparately()
        {
            var images = new System.Collections.Generic.List<PendingImage>
            {
                new PendingImage { Name = "s.png", MimeType = "image/png", Base64 = "iVBOR" }
            };

            var wire = ImageAttachmentHelper.ToAttachments(images);

            wire.Should().HaveCount(1);
            wire[0].Kind.Should().Be("image");
        }
    }
}
