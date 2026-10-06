namespace MahodAI.Civil3D.Plugin.Utilities.CurveFitting
{
    public readonly record struct Pt2D(double X, double Y)
    {
        public double DistanceTo(Pt2D other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }

        public static Pt2D operator +(Pt2D a, Pt2D b) => new(a.X + b.X, a.Y + b.Y);
        public static Pt2D operator -(Pt2D a, Pt2D b) => new(a.X - b.X, a.Y - b.Y);
        public static Pt2D operator *(Pt2D a, double s) => new(a.X * s, a.Y * s);
    }
}
