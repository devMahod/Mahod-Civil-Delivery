using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class ManualStyleDonorTests
    {
        private static ManualSectionReuseResolver.Candidate Donor(
            string handle, string? style, bool readable = true) => new(
            "MAIN", 100, readable,
            new List<ManualSectionReuseResolver.Point> { new(0, -10), new(0, 10) },
            readable, "G" + handle, "S" + handle, "V" + handle,
            SectionViewStyleName: style,
            PresentationReadable: readable,
            SingleDatumPresentationCompatible: readable);

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public void MultipleReadableManualSectionsWithOneCommonStyle_AreValidDonors()
        {
            SectionPlanService.CommonManualSectionViewStyle(new[]
                {
                    Donor("1", "OFFICE-SECTIONS"),
                    Donor("2", "office-sections"),
                    Donor("3", "OFFICE-SECTIONS"),
                })
                .Should().Be("OFFICE-SECTIONS");
        }

        [Fact]
        [Trait("Category", "RequiresCivil3D")]
        public void DisagreeingReadableManualStyles_AreNotGuessed()
        {
            SectionPlanService.CommonManualSectionViewStyle(new[]
                { Donor("1", "STYLE-A"), Donor("2", "STYLE-B") })
                .Should().BeNull();
        }
    }
}
