using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate.Recognition;

/// <summary>Content identity, not a CAD claim, image-origin verification, or engineering approval.</summary>
public sealed class FamilyVisualImage
{
    public string? Sha256 { get; set; }
    public string? Kind { get; set; }
    public string? Origin { get; set; }
}

/// <summary>A persisted exact-scope visual interpretation. Run/record IDs are deliberately not stable membership keys.</summary>
public sealed class FamilyVisualBinding
{
    public string Schema { get; set; } = FamilyVisualBindingPolicy.Schema;
    public string? SourceFingerprint { get; set; }
    public string? MemberFingerprint { get; set; }
    public string? GeometryFingerprint { get; set; }
    public int MemberCount { get; set; }
    public List<FamilyVisualImage> Images { get; set; } = new();
}

public static class FamilyVisualBindingPolicy
{
    public const string Schema = "mahod-family-visual-binding/1";
    public const int ProfileSchemaVersion = 4;
    public const string StaleReason = "visual-source-members-or-geometry-changed";
    private const int MaxMembers = 100000;

    public static FamilyVisualBinding Capture(RecognitionGroupInput group, IReadOnlyList<FamilyRankImage> images)
    {
        ArgumentNullException.ThrowIfNull(group);
        var snapshot = Snapshot(group.Records);
        var binding = new FamilyVisualBinding {
            SourceFingerprint = snapshot.Source, MemberFingerprint = snapshot.Members,
            GeometryFingerprint = snapshot.Geometry, MemberCount = group.Records.Count,
            Images = images.Select(i => new FamilyVisualImage { Sha256 = i.Sha256, Kind = i.Kind,
                Origin = i.Kind == VisionImagePolicy.GroupPreview ? "schematic-group" : "user-supplied-legend" }).ToList()
        };
        if (!IsValid(binding)) throw new InvalidOperationException("Visual binding is incomplete.");
        return binding;
    }

    public static bool IsValid(FamilyVisualBinding? binding) => binding != null && binding.Schema == Schema &&
        CatalogIdentity.IsValidSha256(binding.SourceFingerprint) && CatalogIdentity.IsValidSha256(binding.MemberFingerprint) &&
        CatalogIdentity.IsValidSha256(binding.GeometryFingerprint) && binding.MemberCount is > 0 and <= MaxMembers &&
        binding.Images is { Count: > 0 and <= VisionImagePolicy.MaxImages } &&
        binding.Images.All(i => i != null && CatalogIdentity.IsValidSha256(i.Sha256) &&
            ((i.Kind == VisionImagePolicy.GroupPreview && i.Origin == "schematic-group") ||
             (i.Kind == VisionImagePolicy.LegendCrop && i.Origin == "user-supplied-legend"))) &&
        binding.Images.Select(i => i.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() == binding.Images.Count;

    public static bool IsCurrent(FamilyVisualBinding? binding, IReadOnlyList<NeutralQuantityRecord> records)
    {
        if (!IsValid(binding) || records.Count != binding!.MemberCount) return false;
        try {
            var now = Snapshot(records);
            return now.Source == binding.SourceFingerprint && now.Members == binding.MemberFingerprint && now.Geometry == binding.GeometryFingerprint;
        } catch (Exception) { return false; }
    }

    public static FamilyVisualBinding? Clone(FamilyVisualBinding? value) => value == null ? null : new FamilyVisualBinding {
        Schema = value.Schema, SourceFingerprint = value.SourceFingerprint, MemberFingerprint = value.MemberFingerprint,
        GeometryFingerprint = value.GeometryFingerprint, MemberCount = value.MemberCount,
        Images = value.Images?.Select(i => i == null ? null! : new FamilyVisualImage { Sha256 = i.Sha256, Kind = i.Kind, Origin = i.Origin }).ToList()!
    };

    // Hashed locally only. Never part of FamilyRankRequest evidence or a network prompt.
    private static (string Source, string Members, string Geometry) Snapshot(IReadOnlyList<NeutralQuantityRecord> records)
    {
        if (records == null || records.Count is < 1 or > MaxMembers) throw new InvalidOperationException("Visual scope is empty or too large.");
        var rows = records.Select(r => {
            if (r?.Source == null || r.Measurement == null || !CatalogIdentity.IsValidSha256(r.Source.DrawingHash) ||
                string.IsNullOrWhiteSpace(r.Source.Handle) || string.IsNullOrWhiteSpace(r.Source.Drawing) ||
                !double.IsFinite(r.Measurement.RawValue)) throw new InvalidOperationException("Visual source identity is incomplete.");
            var source = new { r.ProjectProfileId, r.Source.Drawing, r.Source.DrawingPath, r.Source.DrawingHash, r.Source.Xref };
            var member = new { r.Source.Drawing, r.Source.DrawingPath, r.Source.Xref, r.Source.Handle, r.Source.EntityType,
                r.Source.CivilIdentity, r.Measurement.Kind, r.Measurement.Method, r.Measurement.Unit,
                InstanceTransform = Parameter(r, EvidenceKeys.XrefTransform), InstanceTransformStatus = Parameter(r, EvidenceKeys.XrefTransform + EvidenceKeys.StatusSuffix) };
            var key = Hash(member);
            var geometry = new { r.Source, r.Measurement.RawValue, r.Measurement.GeometryEvidence,
                Sample = Parameter(r, EvidenceKeys.GeometrySample), SampleStatus = Parameter(r, EvidenceKeys.GeometrySample + EvidenceKeys.StatusSuffix),
                Transform = Parameter(r, EvidenceKeys.XrefTransform), TransformStatus = Parameter(r, EvidenceKeys.XrefTransform + EvidenceKeys.StatusSuffix) };
            return new { Key = key, Source = Hash(source), Geometry = Hash(geometry) };
        }).OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        if (rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new InvalidOperationException("Visual member identity is ambiguous.");
        return (Hash(rows.Select(r => r.Source).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            Hash(rows.Select(r => r.Key).ToArray()), Hash(rows.Select(r => new { r.Key, r.Geometry }).ToArray()));
    }
    private static string? Parameter(NeutralQuantityRecord r, string key) => r.Measurement.Parameters.TryGetValue(key, out var value) ? value : null;
    private static string Hash<T>(T value) => ArtifactHash.Sha256OfText(JsonSerializer.Serialize(value));
}
