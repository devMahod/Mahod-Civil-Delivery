using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.Utilities;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The firm's own cross-section program, MahodCivilNet's <c>Sc_CreateMultipleSectionView</c>
    /// (class <c>MahodCivilNet.MHSection.CreateMultipleSectionView</c>), driven headless through
    /// reflection: bind once, configure per station, run once per pick. No UI, no MessageBox, no
    /// command of its own; the typical-sections service wires it.
    /// </summary>
    /// <remarks>
    /// Civil 3D section views are not what the firm delivers. The firm's sections are one block per
    /// station (<c>MSV_&lt;align&gt;_&lt;station&gt;_…</c>) of linework and 0.4 m text on the HW-CS layers at
    /// 1:200, the drawings the engineers edit. So Civil Delivery draws them with the firm's code, not a
    /// copy of it, the way the firm itself rebuilds sections without a dialog
    /// (<c>Cfg.FromString(…)</c> then <c>ProcessCorridor(origin)</c>).
    /// <para>
    /// This is a self-contained copy of the MahodAI plugin's <c>DrawFirmCrossSectionsTool</c> and
    /// <c>FirmSectionsGuard</c>: that folder is not shipped from this repo, and the standalone build
    /// compiles only <c>CivilDelivery\**</c>. Keep the two in step when MahodCivilNet changes.
    /// </para>
    /// <para>
    /// Traps the wrapper learned live (2026-10-06, 2026-10-22), all kept here: ProcessCorridor shows a
    /// modal MessageBox for any error outside its station loop (missing corridor/alignment/surface, no
    /// station, more than <c>FirmSectionSettings.MaxSections</c>), so the caller checks every one of those
    /// first; it works on the ACTIVE drawing only (<see cref="TargetIsActive"/> refuses anything else,
    /// or a modal NullReference box blocks Civil 3D); and two statics survive between calls
    /// (<c>_dcUsedLayers</c>, <c>csMajorStep</c>) and are reset by <see cref="Configure"/>.
    /// </para>
    /// </remarks>
    internal sealed class FirmSectionAdapter
    {
        /// <summary>The firm's section blocks: one per station, named <c>MSV_&lt;align&gt;_&lt;station&gt;_…</c>.</summary>
        public const string SectionBlockNamePrefix = "MSV_";

        private const string FormTypeName = "MahodCivilNet.MHSection.CreateMultipleSectionView";
        private const string Point2TypeName = "MahodCivilNet.Point2";

        /// <summary>What the engineer is told when the guard refuses (the wrapper's text, verbatim).</summary>
        public const string NotActiveMessageHe =
            "כלי החתכים של מהוד (MahodCivilNet) עובד רק על השרטוט הפעיל, והשרטוט של השיחה אינו " +
            "השרטוט הפעיל כרגע — החתכים לא שורטטו בפורמט המשרד. עבור לשרטוט ונסה שוב.";

        /// <summary>What the engineer is told when no MahodCivilNet is on the machine.</summary>
        public const string NotFoundMessageHe =
            "כלי החתכים של מהוד (MahodCivilNet) לא נמצא במחשב — החתכים לא שורטטו בפורמט המשרד.";

        /// <summary>What the engineer is told when the MahodCivilNet found lacks the members used here.</summary>
        public const string IncompatibleMessageHe =
            "גרסת MahodCivilNet במחשב אינה תואמת (ProcessCorridor / Cfg לא נמצאו).";

        /// <summary>Prefix of the message when the firm's code itself threw; the firm's own reason follows.</summary>
        public const string FailedMessagePrefixHe = "כלי החתכים של מהוד נכשל: ";

        private Type? _formType;
        private Type? _point2Type;
        private MethodInfo? _processCorridor;
        private object? _cfg;
        private MethodInfo? _fromString;
        private MethodInfo? _setValue;

        /// <summary>Where the firm's assembly came from (<c>loaded:&lt;name&gt;</c> or a path); null until bound.</summary>
        public string? Source { get; private set; }

        /// <summary>The technical reason behind the last <see cref="TryBind"/> failure, for the log and support.</summary>
        public string? BindDetail { get; private set; }

        public bool IsBound =>
            _formType != null && _point2Type != null && _processCorridor != null
            && _cfg != null && _fromString != null && _setValue != null;

        /// <summary>
        /// True only when the target database is both the working database and the active document's
        /// database. Compared by the native database (<c>UnmanagedObject</c>), not the managed wrapper:
        /// AutoCAD hands out a new wrapper for the same drawing, so reference equality refused the only
        /// open drawing (live 2026-10-22).
        /// </summary>
        public static bool TargetIsActive(IntPtr targetDatabase, IntPtr workingDatabase, IntPtr activeDatabase) =>
            activeDatabase != IntPtr.Zero
            && targetDatabase == activeDatabase
            && workingDatabase == activeDatabase;

        /// <summary>
        /// The engineer's loaded MahodCivilNet, else the first DLL found on disk: <c>MAHOD_CIVILNET_DLL</c>,
        /// <c>MahodCivilNet\</c> beside this plugin, beside this plugin, then <c>C:\C3D\&lt;year&gt;\</c>.
        /// </summary>
        public static Assembly? LoadFirmAssembly(out string? source, out string? error)
        {
            error = null;
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => (a.GetName().Name ?? "").StartsWith("MahodCivilNet_", StringComparison.OrdinalIgnoreCase));
            if (loaded != null) { source = "loaded:" + loaded.GetName().Name; return loaded; }

            // The product year from the host's own API, not from the runtime: 2025 and 2026
            // are both .NET 8, and a 2026 toolkit does not load in 2025.
            string year = HostProductYear();
            string here = Path.GetDirectoryName(typeof(FirmSectionAdapter).Assembly.Location) ?? "";
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("MAHOD_CIVILNET_DLL");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env.Trim());
            candidates.Add(Path.Combine(here, "MahodCivilNet", $"MahodCivilNet_{year}.dll"));
            candidates.Add(Path.Combine(here, $"MahodCivilNet_{year}.dll"));
            candidates.Add($@"C:\C3D\{year}\MahodCivilNet_{year}.dll");
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var asm = Assembly.LoadFrom(path);
                    source = path;
                    return asm;
                }
                catch (Exception ex) { error = $"{path}: {ex.Message}"; }
            }
            source = null;
            error ??= "MahodCivilNet_" + year + ".dll not found in: " + string.Join("; ", candidates);
            return null;
        }

        /// <summary>2025 / 2026 / 2027 from acdbmgd's assembly version (25.0 / 25.1 / 26.0).</summary>
        public static string HostProductYear()
        {
            var v = typeof(Database).Assembly.GetName().Version;
            if (v == null) return Environment.Version.Major >= 10 ? "2027" : "2026";
            if (v.Major >= 26) return "2027";
            return v.Minor >= 1 ? "2026" : "2025";
        }

        /// <summary>
        /// Loads the firm's assembly and resolves everything the run needs. False with the Hebrew message
        /// for the engineer; the technical reason goes to <see cref="BindDetail"/> and the log.
        /// </summary>
        public bool TryBind(out string? error)
        {
            error = null;
            BindDetail = null;
            if (IsBound) return true;

            var asm = LoadFirmAssembly(out var source, out var loadError);
            if (asm == null)
            {
                BindDetail = loadError;
                MahodLogger.Warning($"[FirmSectionAdapter] MahodCivilNet not found: {loadError}");
                error = NotFoundMessageHe;
                return false;
            }

            Type? formType = null, point2Type = null;
            MethodInfo? process = null, fromString = null, setValue = null;
            object? cfg = null;
            string? detail = null;
            try
            {
                formType = asm.GetType(FormTypeName);
                point2Type = asm.GetType(Point2TypeName);
                process = formType?.GetMethod("ProcessCorridor", BindingFlags.Public | BindingFlags.Static);
                var cfgField = formType?.GetField("Cfg", BindingFlags.Public | BindingFlags.Static);
                cfg = cfgField?.GetValue(null);
                var cfgType = cfg?.GetType();
                fromString = cfgType?.GetMethod("FromString", new[] { typeof(string) })
                             ?? cfgType?.GetMethod("FromString");
                setValue = cfgType?.GetMethod("SetValue", new[] { typeof(string), typeof(string) });
                if (formType == null) detail = FormTypeName + " not found";
                else if (point2Type == null) detail = Point2TypeName + " not found";
                else if (process == null) detail = "static ProcessCorridor not found";
                else if (cfgField == null) detail = "static field Cfg not found";
                else if (cfg == null) detail = "Cfg is null";
                else if (fromString == null) detail = "Cfg.FromString not found";
                else if (setValue == null) detail = "Cfg.SetValue(string,string) not found";
            }
            catch (Exception ex)
            {
                detail = ex.Message;
            }

            if (detail != null)
            {
                BindDetail = $"{source}: {detail}";
                MahodLogger.Warning($"[FirmSectionAdapter] MahodCivilNet incompatible: {BindDetail}");
                error = IncompatibleMessageHe;
                return false;
            }

            _formType = formType;
            _point2Type = point2Type;
            _processCorridor = process;
            _cfg = cfg;
            _fromString = fromString;
            _setValue = setValue;
            Source = source;
            MahodLogger.Info($"[FirmSectionAdapter] bound to {source}");
            return true;
        }

        /// <summary>
        /// <c>Cfg.FromString(defaults)</c>, then <c>Cfg.SetValue</c> for each override in order, then the two
        /// statics that survive between runs are reset the way the firm's dialog constructor resets them.
        /// </summary>
        public void Configure(string defaults, IEnumerable<(string Key, string Value)> overrides)
        {
            if (defaults == null) throw new ArgumentNullException(nameof(defaults));
            if (overrides == null) throw new ArgumentNullException(nameof(overrides));
            RequireBound();

            _fromString!.Invoke(_cfg, new object[] { defaults });
            foreach (var (key, value) in overrides)
                _setValue!.Invoke(_cfg, new object[] { key, value });

            // Statics that survive between calls (the dialog's constructor resets the first).
            try { (_formType!.GetField("_dcUsedLayers", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary)?.Clear(); } catch { }
            try { _formType!.GetField("csMajorStep", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, 5.0); } catch { }
        }

        /// <summary>
        /// Runs the firm's code once at the origin and returns the handles of the section blocks it added:
        /// model-space <c>MSV_*</c> block references on <paramref name="sectionLayer"/> (the firm's
        /// <c>&lt;alignment&gt;_SectionView</c>) that were not there before. Empty when it drew nothing.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Not bound, or the firm's code threw: the message is <see cref="FailedMessagePrefixHe"/> plus
        /// the firm's own reason, the firm's exception inside.
        /// </exception>
        public IReadOnlyList<Handle> ProcessAt(Transaction tr, Database db, string sectionLayer, double originX, double originY)
        {
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (string.IsNullOrWhiteSpace(sectionLayer)) throw new ArgumentException("The section layer is empty.", nameof(sectionLayer));
            RequireBound();

            var before = SectionBlockHandles(tr, db, sectionLayer);
            var known = new HashSet<long>(before.Select(h => h.Value));
            var origin = Activator.CreateInstance(_point2Type!, originX, originY)
                         ?? throw new InvalidOperationException(IncompatibleMessageHe);
            var linesBefore = CommandLineHistory();
            var thrown = new List<string>();
            var firmAssembly = _formType!.Assembly;
            var callThread = Environment.CurrentManagedThreadId;
            var recording = false;
            void OnThrow(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
            {
                // Where inside the firm's code each exception starts: its box shows the message only (eNullObjectId).
                if (recording || Environment.CurrentManagedThreadId != callThread || thrown.Count >= 30) return;
                recording = true;
                try
                {
                    var frames = new System.Diagnostics.StackTrace(1, false).GetFrames()
                        .Select(frame => frame.GetMethod())
                        .Where(method => method?.DeclaringType?.Assembly == firmAssembly)
                        .Select(method => method!.DeclaringType!.Name + "." + method.Name)
                        .Take(6);
                    thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message} @ {string.Join(" < ", frames)}");
                }
                catch { /* diagnostics only */ }
                finally { recording = false; }
            }
            var watch = new FirmMessageBoxWatch();
            AppDomain.CurrentDomain.FirstChanceException += OnThrow;
            try
            {
                _processCorridor!.Invoke(null, new[] { origin });
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                MahodLogger.Error($"[FirmSectionAdapter] ProcessCorridor failed at ({originX:0.###}, {originY:0.###}) from {Source}", inner);
                throw new InvalidOperationException(FailedMessagePrefixHe + inner.Message, inner);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= OnThrow;
                watch.Dispose();
                LastFirmBoxes = watch.Texts;
                LastFirmExceptions = thrown;
                LastFirmMessages = NewLines(linesBefore, CommandLineHistory());
            }

            var added = SectionBlockHandles(tr, db, sectionLayer).Where(h => !known.Contains(h.Value)).ToList();
            MahodLogger.Info($"[FirmSectionAdapter] ProcessCorridor at ({originX:0.###}, {originY:0.###}): {added.Count} section block(s) on {sectionLayer}" +
                             $" · command-line history {linesBefore.Count} lines");
            foreach (var box in LastFirmBoxes)
                MahodLogger.Warning($"[FirmSectionAdapter] the firm's message box (closed): {box}");
            foreach (var exception in LastFirmExceptions)
                MahodLogger.Info($"[FirmSectionAdapter] thrown inside the firm's code: {exception}");
            foreach (var line in LastFirmMessages)
                MahodLogger.Info($"[FirmSectionAdapter] command line: {line}");
            return added;
        }

        /// <summary>
        /// The text of every message box the firm's code raised during the last <see cref="ProcessAt"/>, read and closed
        /// by <see cref="FirmMessageBoxWatch"/>. ProcessCorridor answers an error outside its station loop this way and
        /// draws nothing.
        /// </summary>
        public IReadOnlyList<string> LastFirmBoxes { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Every exception thrown inside the firm's code during the last <see cref="ProcessAt"/>, caught there or not,
        /// with the firm's methods it was thrown through (first 30): the box gives the message, this gives the place.
        /// </summary>
        public IReadOnlyList<string> LastFirmExceptions { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// What the firm's code printed on the command line during the last <see cref="ProcessAt"/>. It reports a
        /// station it could not draw only there ("Station creation error: …") and goes on, so this is the one place
        /// its reason survives.
        /// </summary>
        public IReadOnlyList<string> LastFirmMessages { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// The command line's last lines (AutoCAD's <c>Internal.Utils.GetLastCommandLines</c>, reached by reflection so
        /// a host without it only loses the diagnostics). Empty when unavailable.
        /// </summary>
        private static List<string> CommandLineHistory()
        {
            try
            {
                var utils = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => string.Equals(a.GetName().Name, "AcMgd", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.GetType("Autodesk.AutoCAD.Internal.Utils"))
                    .FirstOrDefault(t => t != null);
                var method = utils?.GetMethod("GetLastCommandLines", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(int), typeof(bool) }, null);
                return method?.Invoke(null, new object[] { 200, true }) is IEnumerable<string> lines
                    ? lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>The lines after the longest tail of <paramref name="before"/> that <paramref name="after"/> still holds.</summary>
        internal static IReadOnlyList<string> NewLines(IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            for (var skip = 0; skip < before.Count; skip++)
            {
                var tail = before.Count - skip;
                if (tail > after.Count) continue;
                var matches = true;
                for (var i = 0; i < tail && matches; i++)
                    matches = string.Equals(before[skip + i], after[i], StringComparison.Ordinal);
                if (matches) return after.Skip(tail).ToList();
            }
            return after.ToList();
        }

        /// <summary>Handles of the firm's section blocks (MSV_*) on the layer, in model-space order.</summary>
        internal static List<Handle> SectionBlockHandles(Transaction tr, Database db, string layer)
        {
            var handles = new List<Handle>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!id.ObjectClass.DxfName.Equals("INSERT", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.Equals(br.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                    var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    if (btr.Name.StartsWith(SectionBlockNamePrefix, StringComparison.OrdinalIgnoreCase))
                        handles.Add(br.Handle);
                }
                catch { }
            }
            return handles;
        }

        private void RequireBound()
        {
            if (!IsBound)
                throw new InvalidOperationException("FirmSectionAdapter is not bound; call TryBind first.");
        }
    }
}
