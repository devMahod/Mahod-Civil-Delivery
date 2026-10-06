namespace MahodAI.Civil3D.Plugin.CivilDelivery.Commands
{
    /// <summary>
    /// Every Civil Delivery command name, in one place.
    /// </summary>
    /// <remarks>
    /// MahodAI (the AI assistant, updated from its own feed) still ships an older Civil Delivery under the MHD_*
    /// names. The separate Mahod Civil Delivery plugin (built with MAHOD_CD_STANDALONE) therefore registers MCD_*:
    /// two assemblies must never register the same command in one Civil 3D, and a resume-after-save or a hint on
    /// the command line must name the command of the tool that is actually running. Use these constants for every
    /// [CommandMethod], SendStringToExecute and user-facing hint — never a literal.
    /// </remarks>
    public static class CivilDeliveryCommandNames
    {
#if MAHOD_CD_STANDALONE
        public const string Panel = "MCD_CIVIL_DELIVERY";
        public const string AfterSave = "MCD_DELIVERY_AFTER_SAVE";
        public const string Setup = "MCD_SETUP";
        public const string Sections = "MCD_SECTIONS";
        public const string Estimate = "MCD_ESTIMATE";
        public const string EstimateScanAfterSave = "MCD_ESTIMATE_SCAN_AFTER_SAVE";
        public const string SmokeDiscover = "MCD_SMOKE_DISCOVER";
        public const string SmokeSections = "MCD_SMOKE_SECTIONS";
        public const string SmokeEstimate = "MCD_SMOKE_ESTIMATE";
#else
        public const string Panel = "MHD_CIVIL_DELIVERY";
        public const string AfterSave = "MHD_DELIVERY_AFTER_SAVE";
        public const string Setup = "MHD_SETUP";
        public const string Sections = "MHD_SECTIONS";
        public const string Estimate = "MHD_ESTIMATE";
        public const string EstimateScanAfterSave = "MHD_ESTIMATE_SCAN_AFTER_SAVE";
        public const string SmokeDiscover = "MHD_SMOKE_DISCOVER";
        public const string SmokeSections = "MHD_SMOKE_SECTIONS";
        public const string SmokeEstimate = "MHD_SMOKE_ESTIMATE";
#endif
    }
}
