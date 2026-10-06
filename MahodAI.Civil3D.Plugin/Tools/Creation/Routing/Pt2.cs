namespace MahodAI.Civil3D.Plugin.Tools.Creation.Routing
{
    /// <summary>
    /// Plain 2D point used by the routing engine. Decoupled from
    /// <c>Autodesk.AutoCAD.Geometry.Point2d</c> so the routing classes can be unit-tested
    /// without loading AutoCAD host assemblies.
    /// </summary>
    public readonly record struct Pt2(double X, double Y)
    {
        public double DistanceTo(Pt2 other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
