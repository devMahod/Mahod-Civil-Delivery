using System.Collections.Generic;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers
{
    /// <summary>
    /// Interface for geometry and compliance analyzers.
    /// Analyzers process extracted data and produce analysis results.
    /// </summary>
    public interface IAnalyzer
    {
        /// <summary>
        /// Analyzer name for identification
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Priority for analysis order (lower = first)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Run analysis on extracted data and update the model
        /// </summary>
        void Analyze(DrawingDataModel model);
    }

    /// <summary>
    /// Base class for analyzers with common functionality
    /// </summary>
    public abstract class AnalyzerBase : IAnalyzer
    {
        public abstract string Name { get; }
        public virtual int Priority => 100;

        public abstract void Analyze(DrawingDataModel model);

        /// <summary>
        /// Sanitize double values (replace Infinity/NaN with 0)
        /// </summary>
        protected static double Sanitize(double value)
        {
            if (double.IsInfinity(value) || double.IsNaN(value))
                return 0;
            return value;
        }
    }
}
