using System;
using System.Diagnostics;
using Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Corridor
{
    /// <summary>
    /// Shared reflection helpers for corridor tools.
    /// Civil 3D subassemblies are accessed via reflection because the managed API
    /// does not always expose typed properties for AppliedSubassembly objects.
    /// </summary>
    internal static class CorridorReflectionHelper
    {
        // Toggle verbose diagnostic logging by setting env var MAHOD_SUBASSEMBLY_TRACE=1
        // before the Civil 3D session starts. Falls back to `false` otherwise so
        // the hot path doesn't pay the cost of writing messages per subassembly.
        private static readonly bool _trace =
            Environment.GetEnvironmentVariable("MAHOD_SUBASSEMBLY_TRACE") == "1";

        /// <summary>
        /// Gets a property value from an object using reflection.
        /// </summary>
        internal static T? GetReflectionProperty<T>(object obj, string propertyName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    var value = prop.GetValue(obj);
                    if (value is T typedValue)
                        return typedValue;
                }
            }
            catch { }
            return default;
        }

        /// <summary>
        /// Resolves the display name of an AppliedSubassembly using multiple fallback paths.
        /// Prefer <see cref="ResolveSubassemblyName(object, int, Transaction)"/> when a
        /// transaction is available — it can open the definition Subassembly via
        /// <c>SubassemblyId</c> and is the most reliable path on Civil 3D 2026.
        /// </summary>
        internal static string ResolveSubassemblyName(object appliedSub, int index)
            => ResolveSubassemblyName(appliedSub, index, null);

        /// <summary>
        /// Resolves the display name of an AppliedSubassembly using multiple fallback paths.
        /// Order (first non-empty wins):
        ///   1. Open <c>SubassemblyId</c> in the supplied transaction and read
        ///      <c>Name</c> / <c>DisplayName</c> on the DBObject. (Most reliable.)
        ///   2. <c>SubassemblyName</c> property (direct).
        ///   3. <c>Name</c> property.
        ///   4. <c>Subassembly.Name</c> / <c>Subassembly.DisplayName</c> via reflection.
        ///   5. <c>DisplayName</c>, <c>RawDisplayName</c>, <c>SubassemblyDisplayName</c>.
        ///   6. Fallback <c>Unknown_{index}</c>.
        /// </summary>
        /// <remarks>
        /// When the fallback is hit and <c>MAHOD_SUBASSEMBLY_TRACE=1</c> is set, the
        /// runtime type name plus every readable public property is written to
        /// <see cref="Debug"/> so we can see *why* every name path came back empty.
        /// Civil 3D 2026 occasionally reshapes these APIs per update — the trace is
        /// the fastest way to learn the new shape without a debugger.
        /// </remarks>
        internal static string ResolveSubassemblyName(
            object appliedSub, int index, Transaction? tr)
        {
            // 1. Open the design-time Subassembly via ObjectId + transaction.
            if (tr != null)
            {
                try
                {
                    var idProp = appliedSub.GetType().GetProperty("SubassemblyId");
                    if (idProp != null)
                    {
                        var rawId = idProp.GetValue(appliedSub);
                        if (rawId is ObjectId oid && !oid.IsNull)
                        {
                            var dbo = tr.GetObject(oid, OpenMode.ForRead);
                            if (dbo != null)
                            {
                                var fromDb =
                                    GetReflectionProperty<string>(dbo, "Name")
                                    ?? GetReflectionProperty<string>(dbo, "DisplayName")
                                    ?? GetReflectionProperty<string>(dbo, "RawDisplayName");
                                if (!string.IsNullOrEmpty(fromDb))
                                    return fromDb;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace($"  [1] SubassemblyId open failed: {ex.GetType().Name} {ex.Message}");
                }
            }

            // 2. Direct SubassemblyName property
            var name = GetReflectionProperty<string>(appliedSub, "SubassemblyName");
            if (!string.IsNullOrEmpty(name))
                return name;

            // 3. Generic Name property
            name = GetReflectionProperty<string>(appliedSub, "Name");
            if (!string.IsNullOrEmpty(name))
                return name;

            // 4. Navigate to definition: AppliedSubassembly.Subassembly → Name / DisplayName
            try
            {
                var subProp = appliedSub.GetType().GetProperty("Subassembly");
                if (subProp != null)
                {
                    var defn = subProp.GetValue(appliedSub);
                    if (defn != null)
                    {
                        name = GetReflectionProperty<string>(defn, "Name");
                        if (!string.IsNullOrEmpty(name))
                            return name;
                        name = GetReflectionProperty<string>(defn, "DisplayName");
                        if (!string.IsNullOrEmpty(name))
                            return name;
                        name = GetReflectionProperty<string>(defn, "RawDisplayName");
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace($"  [4] Subassembly navigation failed: {ex.GetType().Name} {ex.Message}");
            }

            // 5. Alternative display-name properties on the applied object itself
            foreach (var candidate in new[] { "DisplayName", "RawDisplayName", "SubassemblyDisplayName" })
            {
                name = GetReflectionProperty<string>(appliedSub, candidate);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            // 6. Fallback — log the concrete type + properties so the next run can be
            //    inspected against actual API shape.
            if (_trace)
            {
                try
                {
                    var type = appliedSub.GetType();
                    Debug.WriteLine($"[MahodAI][subassembly] Unknown name for index {index}. Type={type.FullName}");
                    foreach (var p in type.GetProperties())
                    {
                        try
                        {
                            var val = p.CanRead ? p.GetValue(appliedSub) : null;
                            Debug.WriteLine($"  {p.Name} : {p.PropertyType.Name} = {val}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"  {p.Name} : <{ex.GetType().Name}>");
                        }
                    }
                }
                catch { /* diagnostic must never throw */ }
            }
            return $"Unknown_{index}";
        }

        private static void Trace(string msg)
        {
            if (_trace) Debug.WriteLine($"[MahodAI][subassembly]{msg}");
        }
    }
}
