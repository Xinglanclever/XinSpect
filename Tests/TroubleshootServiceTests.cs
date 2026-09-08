using Xunit;

namespace XinSpect.Tests;

public class TroubleshootServiceTests
{
    [Fact]
    public void Scenarios_Has6Entries()
    {
        Assert.Equal(6, TroubleshootService.Scenarios.Count);
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("game-lag")]
    [InlineData("bsod")]
    [InlineData("fan-noise")]
    [InlineData("slow-boot")]
    [InlineData("network")]
    public void GetById_ReturnsCorrectScenario(string id)
    {
        var s = TroubleshootService.GetById(id);
        Assert.NotNull(s);
        Assert.Equal(id, s.Id);
        Assert.False(string.IsNullOrWhiteSpace(s.Title));
        Assert.False(string.IsNullOrWhiteSpace(s.Icon));
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        Assert.Null(TroubleshootService.GetById("nonexistent"));
    }

    [Fact]
    public void AllScenarioIds_AreUnique()
    {
        var ids = TroubleshootService.Scenarios.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllScenarios_HaveNonEmptyFields()
    {
        foreach (var s in TroubleshootService.Scenarios)
        {
            Assert.NotEmpty(s.PossibleCauses);
            Assert.NotEmpty(s.CheckItems);
            Assert.NotEmpty(s.Suggestions);
            Assert.False(string.IsNullOrWhiteSpace(s.RelatedPageKey));
            Assert.False(string.IsNullOrWhiteSpace(s.RelatedPageLabel));
        }
    }

    [Fact]
    public void AllRelatedPageKeys_ExistInPageRegistry()
    {
        foreach (var s in TroubleshootService.Scenarios)
        {
            var found = PageRegistry.FindAny(s.RelatedPageKey);
            Assert.True(found is not null,
                $"Scenario '{s.Id}' references page key '{s.RelatedPageKey}' which does not exist in PageRegistry");
        }
    }
}
