using Xunit;
namespace XinSpect.Tests;

public sealed class SlcMathBoundaryTests
{
    private static (double PeakMbps, double PeakSec, double CliffSec, double PostMedian) Analyze(params double[] speeds)
        => SlcMath.Analyze(Enumerable.Range(0, speeds.Length).Select(i => (double)i).ToArray(), speeds);

    [Fact]
    public void ExactlyFiveCandidateWindowSamplesAreInsufficient()
        => Assert.Equal(-1, Analyze(1000, 100, 100, 100, 100, 100).CliffSec);

    [Fact]
    public void CandidateAndFollowingFiveUseMeanOfBothCentralValues()
    {
        // sorted six: 100,100,400,480,600,600 => median440, not upper-middle480.
        var result = Analyze(1000, 100, 100, 400, 480, 600, 600);
        Assert.Equal(1, result.CliffSec);
        Assert.Equal(440, result.PostMedian);
    }

    [Fact]
    public void PostMedianUsesAllRemainingSamplesIncludingOddTail()
    {
        var result = Analyze(1000, 100, 100, 100, 100, 100, 100, 800);
        Assert.Equal(1, result.CliffSec);
        Assert.Equal(100, result.PostMedian);
    }

    [Fact]
    public void MedianAtThresholdDoesNotQualify()
        => Assert.Equal(-1, Analyze(1000, 100, 100, 400, 500, 600, 600).CliffSec);

    [Fact]
    public void CandidateAtThresholdDoesNotQualify()
        => Assert.Equal(-1, Analyze(1000, 350, 350, 350, 350, 350, 350).CliffSec);

    [Fact]
    public void PostMedianRemainsFiniteWhenCentralSumWouldOverflow()
    {
        var result = Analyze(double.MaxValue, 1, 1, 1, 1, 1, 1,
            double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue,
            double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue);
        Assert.Equal(1, result.CliffSec);
        Assert.Equal(double.MaxValue, result.PostMedian);
    }

    [Fact]
    public void PositiveSubnormalThroughputsDoNotUnderflowToZeroMedian()
    {
        var result = Analyze(1, double.Epsilon, double.Epsilon, double.Epsilon,
            double.Epsilon, double.Epsilon, double.Epsilon);
        Assert.Equal(1, result.CliffSec);
        Assert.Equal(double.Epsilon, result.PostMedian);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void InvalidCurveIsRejectedRatherThanIndexedOrReported(int kind)
    {
        double[] times = [0, 1];
        double[] speeds = [100, 90];
        switch (kind)
        {
            case 0: times = [0]; break;
            case 1: speeds = []; break;
            case 2: times = [0, double.NaN]; break;
            case 3: times = [1, 1]; break;
            case 4: times = [-1, 1]; break;
            case 5: speeds = [100, double.PositiveInfinity]; break;
            case 6: speeds = [100, 0]; break;
            case 7: speeds = [100, -1]; break;
        }
        Assert.Throws<ArgumentException>(() => SlcMath.Analyze(times, speeds));
    }

    [Fact]
    public void FirstPeakTieAndEmptyCurveContractsRemainStable()
    {
        Assert.Equal((0d, 0d, -1d, 0d), SlcMath.Analyze([], []));
        Assert.Equal(0, Analyze(1000, 1000, 100, 100, 100, 100, 100, 100).PeakSec);
    }
}
