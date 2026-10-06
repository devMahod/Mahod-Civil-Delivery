using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using Policy = MahodAI.CivilDelivery.Shared.SectionSampleLineStyleLogic;

namespace MahodAI.Core.Tests;

public sealed class SectionSampleLineStyleLogicTests
{
    private static readonly string[] Styles = { "Road Sample Lines", "Standard", "Office" };

    [Theory]
    [InlineData("Standard")]
    [InlineData("Road Sample Lines")]
    [InlineData("Office")]
    public void ExistingOwnedStyleSurvivesEnumerationOrderAndNewStyles(string name)
    {
        var owned = new Policy.OwnedStyle(Policy.OwnedState.Readable, name);
        foreach (var available in new[] { Styles, Styles.Reverse().ToArray(),
                     new[] { "A newly imported style" }.Concat(Styles).ToArray() })
        {
            var decision = Policy.Resolve(null, available, owned);
            decision.Name.Should().Be(name);
            decision.Source.Should().Be(Policy.Origin.ExistingOwned);
            decision.IsResolved.Should().BeTrue();
        }
    }

    [Fact]
    public void ExplicitProjectStyleOverridesExistingOwnedStyle()
    {
        var result = Policy.Resolve("Office", Styles,
            new(Policy.OwnedState.Readable, "Standard"));
        result.Name.Should().Be("Office");
        result.Source.Should().Be(Policy.Origin.ExplicitProject);
    }

    [Theory]
    [InlineData(Policy.OwnedState.Absent)]
    [InlineData(Policy.OwnedState.Readable)]
    public void MissingExplicitStyleNeverFallsBack(Policy.OwnedState state)
    {
        var result = Policy.Resolve("Missing office style", Styles,
            new(state, state == Policy.OwnedState.Readable ? "Standard" : null));
        result.IsResolved.Should().BeFalse();
        result.Error.Should().Be("configured-sample-line-style-missing");
    }

    [Theory]
    [InlineData(Policy.OwnedState.Unreadable, null)]
    [InlineData(Policy.OwnedState.Unreadable, "Office")]
    [InlineData(Policy.OwnedState.Conflict, null)]
    [InlineData(Policy.OwnedState.Conflict, "Office")]
    public void UnreadableOrAmbiguousOwnershipIsNotAnAbsentObject(
        Policy.OwnedState state, string? configured)
    {
        var result = Policy.Resolve(configured, Styles, new(state, Error: "native-read-failed"));
        result.IsResolved.Should().BeFalse();
        result.Name.Should().BeNull();
        result.Error.Should().Be("native-read-failed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Missing old style")]
    public void OwnedStyleMustBeReadableAndStillAvailable(string? style)
    {
        Policy.Resolve(null, Styles, new(Policy.OwnedState.Readable, style))
            .IsResolved.Should().BeFalse();
    }

    [Fact]
    public void NewObjectsChooseTheSameProvisionalStyleInAnyEnumerationOrder()
    {
        var owned = new Policy.OwnedStyle(Policy.OwnedState.Absent);
        var a = Policy.Resolve(null, Styles, owned);
        var b = Policy.Resolve(null, Styles.Reverse().ToArray(), owned);
        a.Should().Be(b);
        a.Name.Should().Be("Office");
        a.Source.Should().Be(Policy.Origin.NewDrawingDefault);
    }

    [Fact]
    public void NoAvailableStyleDoesNotInventADocumentDefault()
    {
        var result = Policy.Resolve(null, Array.Empty<string>(), new(Policy.OwnedState.Absent));
        result.IsResolved.Should().BeFalse();
        result.Error.Should().Be("no-sample-line-style-available");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("standard")]
    public void IncompleteOrCaseAmbiguousStyleInventoryDoesNotChooseFirst(string additional)
    {
        Policy.Resolve(null, new[] { "Standard", additional }, new(Policy.OwnedState.Absent))
            .IsResolved.Should().BeFalse();
    }

    [Theory]
    [InlineData("Standard", "Standard", true)]
    [InlineData("standard", "Standard", true)]
    [InlineData("Office", "Standard", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("Standard", null, false)]
    public void ReadbackAndVerifyRequireAnActualMatchingStyle(
        string? expected, string? actual, bool match) =>
        Policy.Matches(expected, actual).Should().Be(match);
}
