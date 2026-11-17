using Xunit;
namespace XinSpect.Tests;
public class NumaTopologyTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static byte[] Node(uint id, params (ulong Mask, ushort Group)[] groups)
    {
        var data = new byte[32 + 16 * groups.Length];
        BitConverter.GetBytes(6).CopyTo(data, 0);
        BitConverter.GetBytes(data.Length).CopyTo(data, 4);
        BitConverter.GetBytes(id).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)groups.Length).CopyTo(data, 30);
        for (int i = 0; i < groups.Length; i++) {
            BitConverter.GetBytes(groups[i].Mask).CopyTo(data, 32 + 16 * i);
            BitConverter.GetBytes(groups[i].Group).CopyTo(data, 40 + 16 * i);
        }
        return data;
    }
    private static IReadOnlyList<HardwareFact> Facts(byte[] bytes)
        => NumaTopologyService.Collect(At, () => NumaTopologyService.Parse(bytes));
    [Fact]
    public void MultiGroupAndSparseIdsEnumerateActualNodes()
    {
        var facts = Facts(Node(2, (3, 0), (3, 1)).Concat(Node(9000, (12, 0))).ToArray());
        Assert.Equal(2, Assert.Single(facts, f => f.Key == "numa.topology").NumericValue);
        var node = Assert.Single(facts, f => f.Key == "numa.node.2");
        Assert.Equal(4, node.NumericValue);
        Assert.Contains("群組 1", node.Value);
        Assert.Contains(facts, f => f.Key == "numa.node.9000");
        Assert.Equal(FactAvailability.Unknown, Assert.Single(facts, f => f.Key == "numa.topology.xnode").Availability);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NodesWithoutActiveProcessorsRemainWithoutMemoryCapacityInference(bool zeroAffinity)
    {
        var memory = zeroAffinity ? Node(10, (0, 0)) : Node(10);
        var facts = Facts(Node(0, (3, 0)).Concat(memory).ToArray());
        Assert.Equal(2, Assert.Single(facts, f => f.Key == "numa.topology").NumericValue);
        var node = Assert.Single(facts, f => f.Key == "numa.node.10");
        Assert.Equal(0, node.NumericValue);
        Assert.Contains("是否為純記憶體節點未確認", node.Value);
    }
    [Fact]
    public void MemoryOnlyZeroGroupCanRetainZeroedNativeArraySlot()
    {
        var bytes = Node(5, (0, 0));
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 30);
        Assert.Equal(FactAvailability.Present, NumaTopologyService.Parse(bytes).Availability);
        bytes[32] = 1;
        Assert.Equal(FactAvailability.ReadError, NumaTopologyService.Parse(bytes).Availability);
    }
    [Fact]
    public void SingleNodeIsNotCrossNodeMeasurementEvidence()
    {
        var facts = Facts(Node(0, (3, 0)));
        Assert.Equal(FactAvailability.NotApplicable, Assert.Single(facts, f => f.Key == "numa.topology.xnode").Availability);
    }
    [Fact]
    public void MalformedBuffersRejectWholeSnapshot()
    {
        var good = Node(0, (3, 0));
        var oversized = good.ToArray();
        BitConverter.GetBytes(int.MaxValue).CopyTo(oversized, 4);
        var badCount = good.ToArray();
        BitConverter.GetBytes((ushort)2).CopyTo(badCount, 30);
        var oldRelation = good.ToArray();
        BitConverter.GetBytes(0).CopyTo(oldRelation, 0);
        foreach (var bytes in new[] { Array.Empty<byte>(), good[..^1], good.Concat(new byte[1]).ToArray(), oversized, badCount, oldRelation,
            good.Concat(good).ToArray(), Node(0, (1, 0), (2, 0)), good.Concat(Node(1, (1, 0))).ToArray() })
        {
            Assert.Equal(FactAvailability.ReadError, NumaTopologyService.Parse(bytes).Availability);
            Assert.DoesNotContain(Facts(bytes), f => f.Availability == FactAvailability.Present);
        }
    }
    [Theory]
    [InlineData(87, FactAvailability.NotSupported)]
    [InlineData(50, FactAvailability.NotSupported)]
    [InlineData(5, FactAvailability.ReadError)]
    [InlineData(122, FactAvailability.ReadError)]
    public void NativeFailurePreservesUnsupportedVersusFailed(int error, FactAvailability expected)
    {
        var facts = NumaTopologyService.Collect(At, () => NumaTopologyService.NativeFailure(error));
        Assert.All(facts, f => Assert.Equal(expected, f.Availability));
    }
    [Fact]
    public void NodeAndBufferBudgetsAreBounded()
    {
        Assert.Equal(FactAvailability.Unknown, NumaTopologyService.Parse(new byte[NumaTopologyService.MaxBufferBytes + 1]).Availability);
        var nodes = Enumerable.Range(0, NumaTopologyService.MaxEnumeratedNodes).SelectMany(i => Node((uint)i)).ToArray();
        Assert.Equal(FactAvailability.Present, NumaTopologyService.Parse(nodes).Availability);
        Assert.Equal(FactAvailability.Unknown, NumaTopologyService.Parse(nodes.Concat(Node(uint.MaxValue)).ToArray()).Availability);
    }
    [Fact]
    public void DirectInjectedInvalidIdentityIsRejected()
    {
        var snapshot = new NumaTopologySnapshot(FactAvailability.Present,
            [new(1, [new(0, 1)]), new(1, [])]);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(NumaTopologyService.Collect(At, () => snapshot), f => f.Key == "numa.topology").Availability);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void FullGroupResponseAcceptsBothDocumentedNumaRelationshipCodes(int relationship)
    {
        var bytes = Node(17, (3, 0), (12, 1));
        BitConverter.GetBytes(relationship).CopyTo(bytes, 0);
        var snapshot = NumaTopologyService.Parse(bytes);
        Assert.Equal(FactAvailability.Present, snapshot.Availability);
        Assert.Equal(2, Assert.Single(snapshot.Nodes).Groups.Count);
    }
}
