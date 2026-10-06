using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// Reading a DWG someone else holds open is the NORMAL case, not an error.
    ///
    /// ReadDwgFile(path, FileShare.Read, …) demands that nobody holds the file for
    /// writing — and AutoCAD holds every open drawing for writing. The moment the CL
    /// drawing was open in another tab, "בחר קובץ CL" showed a raw eFileSharingViolation
    /// (2026-08-26), while the guide explicitly promises the file may stay open.
    /// These are source-level assertions; SideDwg itself only runs inside a Civil host.
    /// </summary>
    public class SideDwgTests
    {
        private static string PluginSourceDir =>
            typeof(SideDwgTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "MahodPluginSourceDir").Value!;

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { PluginSourceDir }.Concat(parts).ToArray()));

        private static string Services(string file) =>
            Read("CivilDelivery", "Sections", "Services", file);

        [Theory]
        [InlineData("ClInstructionReader.cs")]
        [InlineData("ProjectSetupScanner.cs")]
        [InlineData("ProjectDashboardService.cs")]
        public void EverySideReadTolerates_AnOpenDrawing(string file)
        {
            var src = Services(file);

            src.Should().Contain("SideDwg.OpenReadOnly",
                "every external-DWG read must go through the sharing-tolerant path");
            src.Should().NotContain("FileShare.Read,",
                "share-Read demands no writers, which fails the moment the drawing is open in a tab");
        }

        [Fact]
        public void SideDwg_HasAllThreeLevels()
        {
            var src = Services("SideDwg.cs");

            src.Should().Contain("open-document",
                "a drawing open in this session is read from its live database, not from the file");
            src.Should().Contain("FileShare.ReadWrite",
                "a side database must tolerate a writer in another session");
            src.Should().Contain("shadow-copy", "the last resort reads a byte copy from %TEMP%");
            src.Should().Contain("FileShare.ReadWrite | FileShare.Delete",
                "the copy stream itself must be sharing-tolerant or it fails the same way");
            src.Should().Contain("File.Delete(_tempCopy)", "shadow copies must not accumulate");
            src.Should().Contain("ownsDb: false",
                "an open document's database belongs to the document and must never be disposed here");
        }

        [Fact]
        public void ThePickerNeverShowsARawSharingViolation()
        {
            var ui = Read("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs");

            ui.Should().Contain("ErrorStatus.FileSharingViolation",
                "if every level still fails, the engineer reads a sentence, not eFileSharingViolation");
            ui.Should().Contain("הקובץ נעול על ידי תוכנית אחרת");
        }
    }
}
