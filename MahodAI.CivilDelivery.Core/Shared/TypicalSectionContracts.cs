using System;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Names and limits of the "typical sections" flow (2026-10): the engineer clicks spots on the road, every click
    /// becomes a marker line across the road, and the firm's section program draws one cross-section per marker.
    /// Kept in Core so the panel, the pick command and the tests agree on one spelling.
    /// </summary>
    public static class TypicalSectionContracts
    {
        /// <summary>The marker lines (and their station texts) the pick command writes into the host drawing.</summary>
        public const string MarkerLayer = "MHD-TYPICAL-CL";

        /// <summary>Strip names Civil Delivery writes inside the firm's section blocks.</summary>
        public const string NamesLayer = "MHD-TYPICAL-NAMES";

        /// <summary>Two picks closer than this are one section twice; the second is refused.</summary>
        public const double MinPickSpacingM = 1.0;

        /// <summary>Above this a half-width is a unit mistake (cm or mm typed as metres), as for station markers.</summary>
        public const double MaxHalfWidthM = ProjectProfile.SectionsProfile.ClProfile.StationMarkerMaxHalfWidthM;

        /// <summary>Offered when the profile holds no approved half-width: what the MahodAI assistant passes today.</summary>
        public const double DefaultHalfWidthM = 20.0;

        /// <summary>A pick that moves this far or more to the nearest corridor station has its marker moved and a note.</summary>
        public const double SnapNoteThresholdM = 0.01;

        /// <summary>
        /// The marker's drawn width (a polyline's constant width, in metres): 3 % of the half-width, 0.6 m for the usual
        /// 20 m, between 0.3 and 2 m. A plain line vanished among the plan's linework (live 2026-10-28).
        /// </summary>
        public static double MarkerWidth(double halfWidthM) =>
            Math.Clamp(double.IsFinite(halfWidthM) ? halfWidthM * 0.03 : DefaultHalfWidthM * 0.03, 0.3, 2.0);

        /// <summary>The station label's height (metres): a sixth of the half-width, between 1.5 and 10 m.</summary>
        public static double MarkerTextHeight(double halfWidthM) =>
            Math.Clamp(double.IsFinite(halfWidthM) ? halfWidthM / 6.0 : DefaultHalfWidthM / 6.0, 1.5, 10.0);

        /// <summary>The text written at a marker's midpoint: the raw station, two decimals, invariant.</summary>
        public static string MarkerLabel(double station) => FormattableString.Invariant($"{station:0.00}");
    }
}
