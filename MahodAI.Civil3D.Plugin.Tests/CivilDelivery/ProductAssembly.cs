namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// The product assembly these tests are compiled against: the MahodAI fork, or the separate Mahod Civil Delivery
    /// plugin (MahodCivilDelivery\Mahod.CivilDelivery.Tests, MAHOD_CD_STANDALONE). Tests that inspect the shipped DLL
    /// read this name, so each lane checks the file it actually ships.
    /// </summary>
    internal static class ProductAssembly
    {
#if MAHOD_CD_STANDALONE
        public const string FileName = "Mahod.CivilDelivery.dll";
#else
        public const string FileName = "MahodAI.Civil3D.Plugin.dll";
#endif
    }
}
