using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MahodAI.CivilDelivery.Estimate.Recognition;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Turns a bitmap the engineer chose (a pasted legend crop) into the PNG the family assistant may receive: at most
/// <see cref="VisionImagePolicy.MaxDimension"/> px on the longest edge, pixels copied into a fresh buffer so no metadata,
/// colour profile or thumbnail of the source survives, PNG only, within the <see cref="VisionImagePolicy"/> limits.
/// Memory only: no file, preview folder or chat attachment is written (unlike ImageAttachmentHelper, which is shared with
/// the platform chat and stays unchanged). It never reads the clipboard and never says what the image shows.
/// </summary>
internal static class VisionImageNormalizer
{
    /// <summary>Longest edges tried, largest first: a busy crop that is too large as PNG is retried smaller.</summary>
    private static readonly int[] Edges = { VisionImagePolicy.MaxDimension, 384, 256 };

    /// <summary>The normalised image as a validated <see cref="VisionImage"/> of <paramref name="kind"/>, or null with a Hebrew reason.</summary>
    public static VisionImage? TryNormalize(BitmapSource? source, string kind, out string? error)
    {
        var png = SemanticPng(source, out error);
        if (png == null) return null;
        if (!VisionImagePolicy.TryCreate(png, kind, out var image, out _) || image == null)
        {
            error = "התמונה אינה PNG מתאים לשליחה. לא נשלח ולא נשמר דבר.";
            return null;
        }
        return image;
    }

    /// <summary>The metadata-free PNG bytes that pass <see cref="VisionImagePolicy.Validate"/>, or null with a Hebrew reason.</summary>
    public static byte[]? SemanticPng(BitmapSource? source, out string? error)
    {
        error = null;
        try
        {
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                error = "אין תמונה זמינה.";
                return null;
            }
            foreach (var edge in Edges)
            {
                var bytes = Encode(source, edge);
                var check = VisionImagePolicy.Validate(bytes);
                if (check.IsValid) return bytes;
                if (check.Reason is not ("too-large" or "dimensions")) break;
            }
            error = "התמונה אינה PNG מתאים לשליחה גם אחרי הקטנה. יש לבחור קטע קטן וקריא יותר. לא נשלח ולא נשמר דבר.";
            return null;
        }
        catch (Exception)
        {
            error = "לא ניתן להכין את התמונה. לא נשלח ולא נשמר דבר.";
            return null;
        }
    }

    private static byte[] Encode(BitmapSource source, int edge)
    {
        var scale = Math.Min(1.0, (double)edge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource scaled = source;
        if (scale < 1.0)
        {
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            transformed.Freeze();
            scaled = transformed;
        }
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        // A fresh pixel buffer drops metadata and colour profiles: the original frame (which may carry clipboard or
        // file metadata) is never encoded.
        var clean = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        clean.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(clean));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
