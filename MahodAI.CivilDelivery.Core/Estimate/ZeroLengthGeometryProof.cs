using System;
using System.Collections.Generic;
using System.Globalization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Explains an unsuccessful zero measurement; never returns a quantity or an
/// exclusion. A closed curve's coincident endpoints alone are not this proof.
/// The native adapter must read every simple vertex and every applicable bulge.
/// </summary>
public static class ZeroLengthGeometryProof
{
    public const string Contract = "exact-zero-length-simple-vertices-v1";
    public const int MaxVertices = 100000;

    public enum GeometryKind { Line, Polyline, Polyline2d, Polyline3d }
    public readonly record struct Vertex(double X, double Y, double Z, double Bulge = 0);

    public static DeliveryFinding? CreateFailure(
        double measuredLength, GeometryKind kind, IReadOnlyList<Vertex>? vertices,
        bool complete, bool simple, string? profileId, ProvenanceRef? source)
    {
        if (measuredLength != 0 || !complete || !simple || vertices == null ||
            vertices.Count < 2 || vertices.Count > MaxVertices ||
            !Enum.IsDefined(kind) || kind == GeometryKind.Line && vertices.Count != 2)
            return null;

        var first = vertices[0];
        foreach (var vertex in vertices)
        {
            // Exact equality only: no length tolerance, snapping, closure or
            // summing endpoints across an unread arc/control-point sequence.
            if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) ||
                !double.IsFinite(vertex.Z) || !double.IsFinite(vertex.Bulge) ||
                vertex.Bulge != 0 || vertex.X != first.X || vertex.Y != first.Y || vertex.Z != first.Z)
                return null;
        }

        var point = string.Create(CultureInfo.InvariantCulture, $"({first.X:R},{first.Y:R},{first.Z:R})");
        var finding = MeasurementFailureProvenance.Create(profileId,
            $"גאומטריה באורך אפס מוכח — העצם {source?.SourceHandle ?? "(לא תועד)"} אינו מדוד",
            $"proof={Contract}; native-measured-length=0; geometry={kind}; " +
            $"complete-simple-vertex-count={vertices.Count}; all-vertices-exactly-equal={point}; all-bulges=0. " +
            "כל קודקודי המסלול שנקראו סופיים וזהים בדיוק; אין מסלול באורך חיובי. " +
            "יש לאתר את העצם בשכבת המקור ולבדוק או לתקן אותו במקור, ואז לסרוק מחדש. " +
            "העצם נשאר לא־מדוד; לא אושרה כמות אפס ולא הוחרגה שכבה.", source, "length");
        return finding;
    }
}
