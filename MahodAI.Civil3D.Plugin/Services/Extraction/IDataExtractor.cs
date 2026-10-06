using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Services.Extraction
{
    /// <summary>
    /// Interface for modular data extractors.
    /// Each extractor is responsible for extracting a specific type of Civil 3D object.
    /// </summary>
    public interface IDataExtractor
    {
        /// <summary>
        /// Type of Civil 3D object this extractor handles (e.g., "Alignment", "Geometry")
        /// </summary>
        string ObjectType { get; }

        /// <summary>
        /// Priority for extraction order (lower = first).
        /// Use for dependencies (e.g., Alignments before Profiles)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Check if this extractor is available
        /// </summary>
        bool IsAvailable(CivilDocument? civilDoc);

        /// <summary>
        /// Extract all data from the document
        /// </summary>
        object? ExtractAll(Transaction tr, CivilDocument? civilDoc, Database db);

        /// <summary>
        /// Extract data for specific object IDs (incremental updates)
        /// </summary>
        object? ExtractByIds(Transaction tr, IEnumerable<ObjectId> ids);
    }
}
