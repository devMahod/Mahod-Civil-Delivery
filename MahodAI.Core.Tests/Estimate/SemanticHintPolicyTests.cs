using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class SemanticHintPolicyTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Key = "layer:DSFSDF|length";
    private static NeutralQuantityRecord Record(string handle = "1", string layer = "DSFSDF", string key = Key,
        string hash = Hash, string run = "run-one", string kind = "length", string unit = "m") => new()
    {
        RecordId = run + handle, RunId = run, ProjectProfileId = "hint-fixture",
        Source = new() { Drawing = "fixture.dwg", DrawingPath = @"C:\fixture\source.dwg", DrawingHash = hash,
            Handle = handle, EntityType = "LWPOLYLINE", Layer = layer, Xref = "ref-one" },
        Measurement = new() { Kind = kind, Unit = unit, Method = "fixture", RawValue = 12.5 },
        Classification = new() { RuleKey = key },
    };
    private static SemanticHintPolicy.Scope Scope(params NeutralQuantityRecord[] records) =>
        SemanticHintPolicy.Capture("hint-fixture", @"C:\fixture\host.dwg", Hash, Key,
            records.Length == 0 ? new[] { Record() } : records);

    [Fact]
    public void RoleAndDescriptionProduceSingleBoundedContextWithoutFillerOrTruncation()
    {
        SemanticHintPolicy.TryContext(new("curb", "מונמכת ליד מעבר חציה"), out var text, out _).Should().BeTrue();
        text.Should().Be("אבן שפה — מונמכת ליד מעבר חציה");
        SemanticHintPolicy.TryContext(new("unknown", ""), out text, out _).Should().BeTrue(); text.Should().BeEmpty();
        SemanticHintPolicy.TryContext(new("other", ""), out text, out _).Should().BeTrue(); text.Should().BeEmpty();
        var longInput = new SemanticHintPolicy.Draft("curb", new string('א', 620));
        SemanticHintPolicy.TryContext(longInput, out text, out var error).Should().BeFalse();
        text.Should().EndWith(longInput.Description); error.Should().Contain("לא קוצר");
        longInput.Description.Should().HaveLength(620);
        SemanticHintPolicy.TryContext(new("curb", "שורה\nנוספת"), out _, out _).Should().BeFalse();
        SemanticHintPolicy.TryContext(new("invented", "תיאור"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void SourceScopeSurvivesRescanIdsButNotChangedSourceOrMeasurementIdentity()
    {
        var scope = Scope();
        Scope(Record(run: "another-run")).Should().Be(scope);
        Scope(Record(handle: "2")).Should().NotBe(scope);
        Scope(Record(hash: new string('b', 64))).Should().NotBe(scope);
        Scope(Record(layer: "OTHER")).Should().NotBe(scope);
        SemanticHintPolicy.Capture("hint-fixture", @"C:\fixture\other-host.dwg", Hash, Key,
            new[] { Record() }).Should().NotBe(scope);
        SemanticHintPolicy.Capture("hint-fixture", @"C:\fixture\host.dwg", new string('b', 64), Key,
            new[] { Record() }).Should().NotBe(scope);
        SemanticHintPolicy.Capture("hint-fixture", @"C:\fixture\host.dwg", Hash, "layer:DSFSDF|area",
            new[] { Record(key: "layer:DSFSDF|area", kind: "area", unit: "m2") }).Should().NotBe(scope);
    }

    [Fact]
    public void CaseVariantGroupsKeepTheirCompleteSourceIdentityRegardlessOfRecordOrder()
    {
        var first = Record(); var second = Record(handle: "2", layer: "dsfsdf", key: "layer:dsfsdf|length");
        Scope(first, second).Should().Be(Scope(second, first));
        Scope(first, second).Should().NotBe(Scope(first), "the full group cannot borrow a partial source set");
    }

    [Fact]
    public void HintValidityRequiresExactScopeRecordedAuthorAndActualUserMeaning()
    {
        var hint = new SemanticHintPolicy.Hint { Source = Scope(), Input = new("curb", ""),
            RecordedBy = "FIXTURE", RecordedAtUtc = DateTime.UtcNow };
        SemanticHintPolicy.IsValid(hint).Should().BeTrue();
        SemanticHintPolicy.IsValid(hint with { RecordedBy = "" }).Should().BeFalse();
        SemanticHintPolicy.IsValid(hint with { Input = new("unknown", "") }).Should().BeFalse();
        SemanticHintPolicy.IsValid(hint with { Source = Scope() with { SourcesHash = "missing" } }).Should().BeFalse();
        SemanticHintPolicy.IsValid(hint with { RecordedAtUtc = default }).Should().BeFalse();
    }
}
