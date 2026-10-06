using System;
using System.Threading;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

// Narrow local UI-to-Core seam (Codex proposal, 27.09): no new transport, capture system, persistent evidence schema,
// CAD assertion or organisation-policy override. The binding lives in memory for one dialog session only.

/// <summary>Where the image the engineer reviewed came from. Neither is a verified CAD capture.</summary>
public enum FamilyImageOrigin { SchematicGroup, UserSuppliedLegend }

/// <summary>
/// The engineer's review of one shown image for one group request: the exact validated PNG bytes and the request
/// <see cref="FamilyRankRequest.ContextId"/> (group, evidence, description, library and image hash) they were reviewed
/// with. <see cref="TryTakePayload"/> grants at most one send, and only while that same image and context are current.
/// It records the user's association only: it never asserts that a pasted legend belongs to the group or the drawing.
/// </summary>
public sealed class ReviewedFamilyImage
{
    /// <summary>The refusal token of <see cref="Bind"/> for an image whose kind or bytes do not fit the origin.</summary>
    public const string ImageRefusal = "image-kind-or-bytes";

    private readonly string _contextId;
    private int _sent;

    private ReviewedFamilyImage(VisionImage image, FamilyImageOrigin origin, FamilyRankRequest request)
    {
        Image = image;
        Origin = origin;
        _contextId = request.ContextId;
    }

    // UI renders these exact owned PNG bytes, not the original clipboard bitmap.
    public VisionImage Image { get; }
    public FamilyImageOrigin Origin { get; }
    public string ContextId => _contextId;
    public string Disclosure => DisclosureOf(Origin);

    /// <summary>What an image of this origin is, shown with the image before any consent.</summary>
    public static string DisclosureOf(FamilyImageOrigin origin) => origin == FamilyImageOrigin.SchematicGroup
        ? "צורה סכמטית מדגימות הקבוצה — לא צילום ולא מדידה"
        : "תמונת מקרא — המשתמש צירף; מקור ושיוך לא אומתו";

    // Call only after the selected image was displayed and the user explicitly
    // checked image consent. A failed preparation leaves the text/manual route intact.
    public static ReviewedFamilyImage? Bind(RecognitionGroupInput group, EngineerBoqLibrary library,
        string? engineerContext, VisionImage image, FamilyImageOrigin origin, out string? refusal)
    {
        refusal = null;
        var expectedKind = origin switch {
            FamilyImageOrigin.SchematicGroup => VisionImagePolicy.GroupPreview,
            FamilyImageOrigin.UserSuppliedLegend => VisionImagePolicy.LegendCrop,
            _ => null,
        };
        if (expectedKind == null || image == null || image.Kind != expectedKind || !VisionImagePolicy.IsValidImage(image))
        { refusal = ImageRefusal; return null; }
        var prepared = FamilyRecognitionAssist.Prepare(group, library, engineerContext, new[] { image });
        refusal = prepared.AbstentionMessage;
        return prepared.Request == null ? null : new ReviewedFamilyImage(image, origin, prepared.Request);
    }

    public bool IsCurrent(RecognitionGroupInput group, EngineerBoqLibrary library,
        string? engineerContext, VisionImage? currentlyDisplayed)
    {
        if (currentlyDisplayed == null || currentlyDisplayed.Sha256 != Image.Sha256 ||
            currentlyDisplayed.Kind != Image.Kind || !VisionImagePolicy.IsValidImage(currentlyDisplayed)) return false;
        var current = FamilyRecognitionAssist.Prepare(group, library, engineerContext, new[] { currentlyDisplayed }).Request;
        return current != null && string.Equals(current.ContextId, _contextId, StringComparison.Ordinal);
    }

    // One reviewed selection grants at most one send. Gemini still independently
    // re-checks the current organisation policy, bytes, hashes and request context.
    public bool TryTakePayload(RecognitionGroupInput group, EngineerBoqLibrary library, string? engineerContext,
        VisionImage? currentlyDisplayed, bool explicitConsent, string grantedBy, DateTimeOffset grantedAtUtc,
        out VisionPayload? payload)
    {
        payload = null;
        if (!explicitConsent || !IsCurrent(group, library, engineerContext, currentlyDisplayed)) return false;
        var request = FamilyRecognitionAssist.Prepare(group, library, engineerContext, new[] { Image }).Request!;
        var candidate = new VisionPayload(new[] { Image },
            new VisionSendPermit(_contextId, new[] { Image.Sha256 }, grantedBy, grantedAtUtc));
        if (!VisionImagePolicy.IsPermitted(request, candidate) || Interlocked.Exchange(ref _sent, 1) != 0) return false;
        payload = candidate;
        return true;
    }
}
