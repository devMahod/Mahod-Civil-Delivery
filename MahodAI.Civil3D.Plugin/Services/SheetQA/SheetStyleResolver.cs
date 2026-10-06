using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA
{
    /// <summary>
    /// Answers "what does the reader actually see" for an entity: its effective colour and
    /// linetype, whether its layer is visible at all, and which font its text style uses.
    ///
    /// Every lookup is keyed on the layer name exactly as the entity reports it. Xref
    /// content reports the host-prefixed name ("survey|contours") and the host layer table
    /// contains that same prefixed record, so one dictionary serves host and xref geometry
    /// alike — as long as nothing prefixes the name a second time.
    ///
    /// The symbol tables are read once per scan; a coordination sheet reaches ~3,700 layers
    /// and tens of thousands of entities, so re-opening the tables per entity is the
    /// difference between a responsive scan and a frozen Civil 3D.
    /// </summary>
    public sealed class SheetStyleResolver
    {
        private readonly Transaction _tr;

        private readonly Dictionary<string, bool> _layerHidden = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _layerColor = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _layerLinetype = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ObjectId, string> _linetypeNames = new();
        private readonly Dictionary<ObjectId, string> _textStyleFonts = new();

        public SheetStyleResolver(Transaction tr, Database db)
        {
            _tr = tr ?? throw new ArgumentNullException(nameof(tr));
            if (db == null) throw new ArgumentNullException(nameof(db));

            CacheLinetypes(db);
            CacheLayers(db);
        }

        /// <summary>Number of layers in the drawing, for diagnostics.</summary>
        public int LayerCount => _layerHidden.Count;

        /// <summary>True when the layer is frozen or off globally (per-viewport freezing is separate).</summary>
        public bool IsLayerHidden(string? layerName) =>
            layerName != null && _layerHidden.TryGetValue(layerName, out var hidden) && hidden;

        /// <summary>Effective ACI colour index, or -1 when it cannot be resolved.</summary>
        public int ResolveColorIndex(AcEntity ent)
        {
            try
            {
                if (!ent.Color.IsByLayer && !ent.Color.IsByBlock) return ent.Color.ColorIndex;
            }
            catch { /* fall through to the layer */ }

            return _layerColor.TryGetValue(ent.Layer, out var index) ? index : -1;
        }

        /// <summary>Effective linetype, normalised to a bare lower-case base name.</summary>
        public string ResolveLinetype(AcEntity ent)
        {
            try
            {
                var own = ent.Linetype;
                if (!string.IsNullOrEmpty(own) &&
                    !own.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) &&
                    !own.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
                {
                    return LegendMatcher.NormalizeLinetype(own);
                }
            }
            catch { /* fall through to the layer */ }

            return _layerLinetype.TryGetValue(ent.Layer, out var name)
                ? LegendMatcher.NormalizeLinetype(name)
                : "?";
        }

        /// <summary>
        /// Font file of a text style. This is what tells keyboard-mapped Hebrew (SHX faces
        /// such as mirym) apart from genuine English in the same drawing.
        /// </summary>
        public string? GetFontFile(ObjectId styleId)
        {
            if (styleId.IsNull) return null;
            if (_textStyleFonts.TryGetValue(styleId, out var cached))
            {
                return string.IsNullOrEmpty(cached) ? null : cached;
            }

            string? font = null;
            try
            {
                if (_tr.GetObject(styleId, OpenMode.ForRead) is TextStyleTableRecord style)
                {
                    font = !string.IsNullOrWhiteSpace(style.FileName) ? style.FileName : style.Font.TypeFace;
                }
            }
            catch { /* unknown font: the decoder falls back to its statistical guard */ }

            _textStyleFonts[styleId] = font ?? string.Empty;
            return font;
        }

        private void CacheLinetypes(Database db)
        {
            try
            {
                if (_tr.GetObject(db.LinetypeTableId, OpenMode.ForRead) is not LinetypeTable linetypes) return;
                foreach (ObjectId id in linetypes)
                {
                    if (_tr.GetObject(id, OpenMode.ForRead) is LinetypeTableRecord record)
                    {
                        _linetypeNames[id] = record.Name;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SheetQA] Linetype table unavailable: {ex.Message}");
            }
        }

        private void CacheLayers(Database db)
        {
            try
            {
                if (_tr.GetObject(db.LayerTableId, OpenMode.ForRead) is not LayerTable layers) return;
                foreach (ObjectId id in layers)
                {
                    if (_tr.GetObject(id, OpenMode.ForRead) is not LayerTableRecord layer) continue;

                    _layerHidden[layer.Name] = layer.IsFrozen || layer.IsOff;
                    try { _layerColor[layer.Name] = layer.Color.ColorIndex; }
                    catch { _layerColor[layer.Name] = -1; }
                    _layerLinetype[layer.Name] = _linetypeNames.TryGetValue(layer.LinetypeObjectId, out var lt)
                        ? lt
                        : "?";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SheetQA] Layer table unavailable: {ex.Message}");
            }
        }
    }
}
