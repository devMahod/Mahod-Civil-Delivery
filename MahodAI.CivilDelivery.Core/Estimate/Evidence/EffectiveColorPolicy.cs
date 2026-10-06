using System.Globalization;

namespace MahodAI.CivilDelivery.Estimate.Evidence;

/// <summary>
/// Resolves the colour an entity is displayed with (<c>ev_color_effective</c>, "r,g,b") through its insertion
/// chain, without AutoCAD. Rules, applied from the entity outward:
/// <list type="bullet">
/// <item>TrueColor → its RGB. ACI 1..255 → the standard AutoCAD palette below. ACI 7 is reported as
/// 255,255,255, its palette value, although AutoCAD displays it black on a light background.</item>
/// <item>ByLayer → the colour of the entity's layer; on layer "0" inside an INSERT (block or XREF) the entity
/// follows the INSERT's layer instead, recursively.</item>
/// <item>ByBlock → the effective colour of the innermost INSERT, recursively. ByBlock with no INSERT above it
/// (model-space root) is <c>unavailable:byblock-at-root</c>: AutoCAD draws it with the background-dependent
/// foreground colour, which is not a stored colour, so no RGB is invented.</item>
/// <item>Any other method (Foreground, None, ByPen, LayerOff/LayerFrozen) or an unreadable colour/layer →
/// unavailable with the reason.</item>
/// </list>
/// Viewport layer overrides and XREF layer VISRETAIN semantics are not modelled: the layer colour is whatever the
/// caller read from the entity's layer record.
/// </summary>
public static class EffectiveColorPolicy
{
    public enum Method
    {
        ByLayer,
        ByBlock,
        Aci,
        TrueColor,
        Unsupported,
    }

    public readonly record struct ColorValue(Method Method, int Index, byte R, byte G, byte B, string? Detail = null)
    {
        public static ColorValue ByLayer => new(Method.ByLayer, 256, 0, 0, 0);
        public static ColorValue ByBlock => new(Method.ByBlock, 0, 0, 0, 0);
        public static ColorValue FromAci(int index) => new(Method.Aci, index, 0, 0, 0);
        public static ColorValue FromRgb(byte r, byte g, byte b) => new(Method.TrueColor, -1, r, g, b);
        public static ColorValue Unsupported(string? detail) => new(Method.Unsupported, -1, 0, 0, 0, detail);
    }

    /// <summary>
    /// One drawn object of the chain: the entity itself or an INSERT above it. <paramref name="LayerColor"/>
    /// is null when the layer record could not be read.
    /// </summary>
    public sealed record Subject(ColorValue Color, string? Layer, ColorValue? LayerColor);

    public const int MaxDepth = 64;

    /// <param name="entity">The entity whose colour is resolved.</param>
    /// <param name="ancestorsInnermostFirst">The INSERTs above it, innermost (direct parent) first; empty at model-space root.</param>
    public static EvidenceValue Resolve(Subject entity, IReadOnlyList<Subject>? ancestorsInnermostFirst)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var ancestors = ancestorsInnermostFirst ?? Array.Empty<Subject>();
        if (ancestors.Count > MaxDepth || ancestors.Any(subject => subject == null))
            return EvidenceValue.Unavailable("color-chain-invalid");
        return ResolveSubject(entity, ancestors, 0);
    }

    private static EvidenceValue ResolveSubject(Subject subject, IReadOnlyList<Subject> ancestors, int next)
    {
        switch (subject.Color.Method)
        {
            case Method.TrueColor:
                return EvidenceValue.Read(Rgb(subject.Color.R, subject.Color.G, subject.Color.B));
            case Method.Aci:
                return AciToRgbText(subject.Color.Index) is { } aci
                    ? EvidenceValue.Read(aci)
                    : EvidenceValue.Unavailable("aci-out-of-range");
            case Method.ByLayer:
                return ResolveLayer(subject, ancestors, next);
            case Method.ByBlock:
                return next < ancestors.Count
                    ? ResolveSubject(ancestors[next], ancestors, next + 1)
                    : EvidenceValue.Unavailable("byblock-at-root");
            default:
                return EvidenceValue.Unavailable("unsupported-color-method" +
                    (string.IsNullOrWhiteSpace(subject.Color.Detail) ? string.Empty : ":" + subject.Color.Detail));
        }
    }

    private static EvidenceValue ResolveLayer(Subject subject, IReadOnlyList<Subject> ancestors, int next)
    {
        // Layer 0 inside a block definition (or an XREF's layer 0) adopts the layer of the INSERT.
        if (IsLayerZero(subject.Layer) && next < ancestors.Count)
            return ResolveLayer(ancestors[next], ancestors, next + 1);
        if (subject.LayerColor is not { } layerColor) return EvidenceValue.Unavailable("layer-color-unread");
        return layerColor.Method switch
        {
            Method.TrueColor => EvidenceValue.Read(Rgb(layerColor.R, layerColor.G, layerColor.B)),
            Method.Aci => AciToRgbText(layerColor.Index) is { } aci
                ? EvidenceValue.Read(aci)
                : EvidenceValue.Unavailable("layer-aci-out-of-range"),
            _ => EvidenceValue.Unavailable("layer-color-not-explicit"),
        };
    }

    public static bool IsLayerZero(string? layer) => string.Equals(layer?.Trim(), "0", StringComparison.Ordinal);

    public static string? AciToRgbText(int index) =>
        TryAciToRgb(index, out var r, out var g, out var b) ? Rgb(r, g, b) : null;

    public static bool TryAciToRgb(int index, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (index < 1 || index > 255) return false;
        var offset = (index - 1) * 3;
        (r, g, b) = (Palette[offset], Palette[offset + 1], Palette[offset + 2]);
        return true;
    }

    private static string Rgb(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"{r},{g},{b}");

    /// <summary>
    /// The standard AutoCAD Color Index palette, entries 1..255 as R,G,B triples. 1..9 and 250..255 are
    /// fixed entries. 10..249 are 24 hues 15° apart; within a hue, entries alternate full and half saturation
    /// at brightness 255, 204, 153, 127 and 76 (so 10 = 255,0,0, 11 = 255,127,127, 12 = 204,0,0 ... 30 = 255,127,0),
    /// channels truncated to integers. Built once; never read from AutoCAD.
    /// </summary>
    private static readonly byte[] Palette = BuildPalette();

    private static byte[] BuildPalette()
    {
        var palette = new byte[255 * 3];
        void Set(int index, int r, int g, int b)
        {
            var offset = (index - 1) * 3;
            palette[offset] = (byte)r;
            palette[offset + 1] = (byte)g;
            palette[offset + 2] = (byte)b;
        }
        Set(1, 255, 0, 0);
        Set(2, 255, 255, 0);
        Set(3, 0, 255, 0);
        Set(4, 0, 255, 255);
        Set(5, 0, 0, 255);
        Set(6, 255, 0, 255);
        Set(7, 255, 255, 255);
        Set(8, 128, 128, 128);
        Set(9, 192, 192, 192);
        int[] levels = { 255, 204, 153, 127, 76 };
        for (var index = 10; index <= 249; index++)
        {
            var hue = (index - 10) / 10;           // 0..23, 15° each
            var step = (index - 10) % 10;
            var value = levels[step / 2];
            var half = step % 2 == 1;
            var sector = hue / 4;                  // 60° sectors
            var q = hue % 4;                       // position inside the sector, quarters
            // HSV with S = 1 (full) or 1/2 (half), exact integer arithmetic, truncated.
            var p = half ? value / 2 : 0;
            var falling = half ? value * (8 - q) / 8 : value * (4 - q) / 4;
            var rising = half ? value * (4 + q) / 8 : value * q / 4;
            var (r, g, b) = sector switch
            {
                0 => (value, rising, p),
                1 => (falling, value, p),
                2 => (p, value, rising),
                3 => (p, falling, value),
                4 => (rising, p, value),
                _ => (value, p, falling),
            };
            Set(index, r, g, b);
        }
        Set(250, 51, 51, 51);
        Set(251, 80, 80, 80);
        Set(252, 105, 105, 105);
        Set(253, 130, 130, 130);
        Set(254, 190, 190, 190);
        Set(255, 255, 255, 255);
        return palette;
    }
}
