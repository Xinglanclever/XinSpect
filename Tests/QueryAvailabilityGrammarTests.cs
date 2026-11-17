using Xunit;
namespace XinSpect.Tests;

// Pure shared parser only: no snapshot collection, network, or hardware.
public sealed class QueryAvailabilityGrammarTests
{
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("+0")]
    [InlineData("00")]
    [InlineData("Present,ReadError")]
    [InlineData("Present,Present")]
    [InlineData("ReadError, NotSupported")]
    [InlineData("Present,0")]
    public void NonNameEnumSyntaxCannotMasqueradeAsAnEmptyQuery(string text)
    {
        Assert.Null(QueryParser.ParseAvailability(text));
        var exception = Assert.Throws<QueryParseException>(() => QueryParser.Parse("# first line\navailability=" + text));
        Assert.Contains("第 2 行", exception.Message);
        Assert.Contains("availability", exception.Message);
        Assert.Contains(text, exception.Message);
        Assert.Contains("unconfirmed", exception.Message);
    }

    [Fact]
    public void EveryDeclaredStateRetainsCanonicalNameCaseAndWhitespaceCompatibility()
    {
        foreach (FactAvailability state in Enum.GetValues<FactAvailability>())
        {
            string text = "  " + state.ToString().ToUpperInvariant() + "  ";
            Assert.Equal(state, QueryParser.ParseAvailability(text));
            var parsed = QueryParser.Parse("availability=" + text);
            Assert.Equal("availability", Assert.Single(parsed.Clauses).Field);
        }
    }

    [Theory]
    [InlineData("present", FactAvailability.Present)]
    [InlineData("ok", FactAvailability.Present)]
    [InlineData("可讀", FactAvailability.Present)]
    [InlineData("read-error", FactAvailability.ReadError)]
    [InlineData("error", FactAvailability.ReadError)]
    [InlineData("not-supported", FactAvailability.NotSupported)]
    [InlineData("insufficient-privilege", FactAvailability.InsufficientPrivilege)]
    [InlineData("no-permission", FactAvailability.InsufficientPrivilege)]
    [InlineData("not-applicable", FactAvailability.NotApplicable)]
    [InlineData("unconfirmed", FactAvailability.Unknown)]
    public void DocumentedAliasesStillReachTheSharedParser(string text, FactAvailability expected)
    {
        Assert.Equal(expected, QueryParser.ParseAvailability(text));
        Assert.Single(QueryParser.Parse("availability=" + text).Clauses);
    }

    [Theory]
    [InlineData("unlisted=x")]
    [InlineData("since~=2026-11-17")]
    [InlineData("until=not-a-date")]
    [InlineData("availability~=Present")]
    [InlineData("key=")]
    [InlineData("key-missing-operator")]
    public void ValidationBranchesReportTheActualOneBasedSourceLine(string line)
    {
        var exception=Assert.Throws<QueryParseException>(()=>QueryParser.Parse("# comment\n"+line));
        Assert.StartsWith("第 2 行",exception.Message);
    }
}
