using Xunit;
namespace XinSpect.Tests;
public sealed class NumaAvailableMemoryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static NumaTopologySnapshot Nodes(params uint[] ids) => new(FactAvailability.Present,
        ids.Select(id => new NumaTopologyNode(id, [])).ToArray());
    private static HardwareFact Memory(IReadOnlyList<HardwareFact> facts, uint id) =>
        Assert.Single(facts, f => f.Key == $"numa.node.{id}.available_bytes");
    [Fact]
    public void SparseIdentitiesExactBytesAndPerNodeTimesArePreserved()
    {
        var seen = new List<uint>();
        var facts = NumaTopologyService.Collect(At, () => Nodes(7, 65535), id =>
        { seen.Add(id); return new(id, At.AddSeconds(id), id == 7 ? 0UL : ulong.MaxValue, FactAvailability.Present); });
        Assert.Equal(new uint[] {7, 65535}, seen);
        Assert.Equal("0", Memory(facts, 7).Value);
        Assert.Equal(0d, Memory(facts, 7).NumericValue);
        Assert.Equal(ulong.MaxValue.ToString(), Memory(facts, 65535).Value);
        Assert.Null(Memory(facts, 65535).NumericValue);
        Assert.Equal(At.AddSeconds(65535), Memory(facts, 65535).MeasuredAtUtc);
        Assert.Equal("bytes", Memory(facts, 7).Unit);
    }
    [Fact]
    public void OversizedNodeAndMismatchedIdentityNeverExposeValue()
    {
        int calls = 0;
        var facts = NumaTopologyService.Collect(At, () => Nodes(3, 65536), id =>
        { calls++; return new(4, At, 999, FactAvailability.Present); });
        Assert.Equal(1, calls);
        foreach (uint id in new uint[] {3,65536})
        { Assert.Equal(FactAvailability.Unknown, Memory(facts,id).Availability); Assert.Equal("", Memory(facts,id).Value); }
    }
    [Theory]
    [InlineData(FactAvailability.ReadError)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.Unknown)]
    public void FailureStateReasonAndSourceTimeRetained(FactAvailability state)
    {
        var f = Memory(NumaTopologyService.Collect(At, () => Nodes(9),
            id => new(id, At.AddSeconds(1), 123, state, "injected reason")),9);
        Assert.Equal(state,f.Availability); Assert.Equal("injected reason",f.UnavailableReason);
        Assert.Null(f.NumericValue); Assert.Equal("",f.Value); Assert.Equal(At.AddSeconds(1),f.MeasuredAtUtc);
    }
    [Fact]
    public void CountBudgetStopsQueriesWithoutInventingZero()
    {
        int calls=0;
        var facts=NumaTopologyService.CollectCore(At,()=>Nodes(Enumerable.Range(0,65).Select(i=>(uint)i).ToArray()),
            id=>{calls++;return new(id,At,1,FactAvailability.Present);},()=>0,1000);
        Assert.Equal(64,calls); Assert.Equal(FactAvailability.Unknown,Memory(facts,64).Availability);
        Assert.Equal("",Memory(facts,64).Value);
    }
    [Fact]
    public void DispatchTimeBudgetStopsAfterSlowQueryWithoutClaimingHardCancel()
    {
        long tick=0;int calls=0;
        var facts=NumaTopologyService.CollectCore(At,()=>Nodes(0,1),id=>
        {calls++;tick=251;return new(id,At,42,FactAvailability.Present);},()=>tick,1000);
        Assert.Equal(1,calls);Assert.Equal("42",Memory(facts,0).Value);
        Assert.Equal(FactAvailability.Unknown,Memory(facts,1).Availability);
    }
    [Fact]
    public void InjectedTopologyWithoutMemoryProbeNeverInvokesNative()
    { Assert.Equal(FactAvailability.Unknown,Memory(NumaTopologyService.Collect(At,()=>Nodes(0)),0).Availability); }
    [Fact]
    public void MalformedTopologyMakesNoMemoryQueries()
    {
        int calls=0;var facts=NumaTopologyService.Collect(At,()=>Nodes(1,1),id=>
        {calls++;return new(id,At,1,FactAvailability.Present);});
        Assert.Equal(0,calls);Assert.DoesNotContain(facts,f=>f.Key.EndsWith("available_bytes"));
    }
    [Fact]
    public void ProbeExceptionAndMissingSuccessBytesFailClosed()
    {
        var facts=NumaTopologyService.Collect(At,()=>Nodes(0,1),id=>id==0
            ? throw new InvalidOperationException("private") : new(id,At,null,FactAvailability.Present));
        Assert.Equal(FactAvailability.ReadError,Memory(facts,0).Availability);
        Assert.DoesNotContain("private",Memory(facts,0).UnavailableReason!);
        Assert.Equal(FactAvailability.ReadError,Memory(facts,1).Availability);
    }
}
