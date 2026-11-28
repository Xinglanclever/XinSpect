using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

public sealed class UiTextTranslationTests
{
    [Fact]
    public void MatchSafelyReturnsNullWhenMatcherTimesOut()
    {
        var expression = new Regex("value");
        const string input = "value";

        Match? result = UiTextTranslation.MatchSafely(expression, input, static (_, _) =>
            throw new RegexMatchTimeoutException(input, "value", TimeSpan.FromMilliseconds(1)));

        Assert.Null(result);
    }
}
