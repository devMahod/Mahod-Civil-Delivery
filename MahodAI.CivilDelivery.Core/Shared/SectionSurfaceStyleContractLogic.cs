using System;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Deterministic ownership and display contract for the two Civil Section styles
    /// created by Mahod. A reserved name without the exact description is a collision,
    /// never authority to rewrite an office style.
    /// </summary>
    public static class SectionSurfaceStyleContractLogic
    {
        public const string ExistingName = "MHD-EXISTING-V3";
        public const string DesignName = "MHD-DESIGN-V3";
        public const string ExistingDescription =
            "Mahod Civil Delivery reserved Section style v3; role=existing; aci=3; linetype=MHD-DASHED2; visible=true.";
        public const string LegacyExistingDescription =
            "Mahod Civil Delivery reserved Section style v1; role=existing; aci=3; linetype=DASHED2; visible=true.";
        public const string DesignDescription =
            "Mahod Civil Delivery reserved Section style v3; role=design; aci=1; linetype=Continuous; visible=true.";

        public sealed record Spec(
            string Name,
            string Description,
            short ColorIndex,
            string Linetype);

        public static readonly Spec Existing = new(
            ExistingName, ExistingDescription, 3,
            SectionAnnotationResourceContracts.DashedLinetypeName);

        public static readonly Spec Design = new(
            DesignName, DesignDescription, 1, "Continuous");

        public static bool IsToolOwnedDescription(Spec spec, string? description) =>
            string.Equals(description, spec.Description, StringComparison.Ordinal) ||
            (ReferenceEquals(spec, Existing) &&
             string.Equals(description, LegacyExistingDescription, StringComparison.Ordinal));

        public static bool TryValidateLive(
            Spec spec,
            string? name,
            string? description,
            short? colorIndex,
            string? colorMethod,
            bool? visible,
            string? linetype,
            out string error)
        {
            error = string.Empty;
            if (!string.Equals(name, spec.Name, StringComparison.OrdinalIgnoreCase))
                error = $"name={name ?? "(null)"}";
            else if (!string.Equals(description, spec.Description, StringComparison.Ordinal))
                error = "reserved style ownership description differs";
            else if (colorIndex != spec.ColorIndex ||
                     !string.Equals(colorMethod, "ByAci", StringComparison.OrdinalIgnoreCase))
                error = $"color={colorMethod}/{colorIndex}";
            else if (visible != true)
                error = "segments are not visible";
            else if (!string.Equals(linetype, spec.Linetype, StringComparison.OrdinalIgnoreCase))
                error = $"linetype={linetype ?? "(null)"}";

            return error.Length == 0;
        }
    }
}
