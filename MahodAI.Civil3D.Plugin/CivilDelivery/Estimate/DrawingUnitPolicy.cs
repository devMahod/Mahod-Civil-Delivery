using System;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Explicit INSUNITS boundary for quantity take-off.  Every supported drawing
    /// unit is normalized to SI before it enters a neutral quantity record.  Unitless
    /// and exotic/unknown units remain measurable as evidence, but carry a global
    /// export blocker and are never labelled as metres.
    /// </summary>
    internal static class DrawingUnitPolicy
    {
        internal sealed record Scale(
            bool IsSupported, double LinearToMetres, string SourceName,
            string LengthUnit, string AreaUnit, string VolumeUnit)
        {
            internal double Length(double value) => value * LinearToMetres;
            internal double Area(double value) => value * LinearToMetres * LinearToMetres;
            internal double Volume(double value) =>
                value * LinearToMetres * LinearToMetres * LinearToMetres;

            internal double[]? Bounds(double[]? value)
            {
                if (value == null || value.Length != 4) return value;
                return new[]
                {
                    Length(value[0]), Length(value[1]), Length(value[2]), Length(value[3]),
                };
            }
        }

        internal static Scale Resolve(UnitsValue units) => Resolve((int)units, units.ToString());

        internal static Scale Resolve(int unitCode, string? sourceName = null)
        {
            // Numeric values are part of AutoCAD's long-standing UnitsValue ABI.
            // Using the numeric tail avoids compiling a 2026 component against enum
            // names introduced by a later host while keeping every conversion explicit.
            var physical = PhysicalDrawingUnitPolicy.Resolve(unitCode, null, null,
                Array.Empty<ProjectProfile.DrawingUnitDeclaration>(), isHostDrawing: false);
            var factor = physical.LinearToMetres;
            var supported = physical.IsSupported;
            return new Scale(
                supported,
                supported ? factor : 1.0,
                sourceName ?? $"UnitsValue({unitCode})",
                supported ? "מטר" : "יחידת שרטוט",
                supported ? "מ\"ר" : "יחידת שרטוט²",
                supported ? "מ\"ק" : "יחידת שרטוט³");
        }

        internal static DeliveryFinding? Validate(UnitsValue units, string projectProfileId)
            => Validate((int)units, units.ToString(), projectProfileId);

        internal static DeliveryFinding? Validate(
            int unitCode, string? sourceName, string projectProfileId)
        {
            var scale = Resolve(unitCode, sourceName);
            if (scale.IsSupported) return null;
            return new DeliveryFinding
            {
                Code = EstimateFindingCodes.UnitUnknown,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = "יחידות השרטוט אינן מוגדרות — האומדן חסום לייצוא",
                Message = $"INSUNITS={scale.SourceName}. הכמויות נשמרות כראיות ביחידות שרטוט ואינן מוצגות כמטרים.",
                RecommendedAction = "יש לבדוק יחידות פיזיות בלשונית פרויקט: שרטוט Unitless המצויר במטרים דורש הצהרה מפורשת. אין לשנות INSUNITS בלבד כתחליף לבירור/המרה של גאומטריה. לאחר ההכרעה יש לסרוק מחדש.",
                ProjectProfileId = projectProfileId,
            };
        }
    }
}
