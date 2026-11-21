using Xunit;
namespace XinSpect.Tests;
public class WindowsTopologyBufferTests
{
    private static byte[] Record(int relationship, params (ulong Mask, ushort Group)[] groups)
    {
        int start = relationship == 2 ? 40 : 32;
        var bytes = new byte[start + groups.Length * 16];
        BitConverter.GetBytes(relationship).CopyTo(bytes, 0);
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)groups.Length).CopyTo(bytes, start - 2);
        for (int i = 0; i < groups.Length; i++) {
            BitConverter.GetBytes(groups[i].Mask).CopyTo(bytes, start + i * 16);
            BitConverter.GetBytes(groups[i].Group).CopyTo(bytes, start + i * 16 + 8);
        }
        return bytes;
    }
    [Fact]
    public void MultipleGroupsPreserveOneCoreAndAllLogicalProcessors()
    {
        var bytes = Record(0, (3, 0), (5, 1));
        Assert.Equal((1, 4), CpuIdService.ParseOsTopology(bytes));
        var cores = CpuAffinity.ParsePhysicalCoreProcessorSets(bytes, true, ulong.MaxValue);
        Assert.Equal(4, Assert.Single(cores).Count);
        Assert.Equal(2, Assert.Single(CpuAffinity.ParsePhysicalCoreProcessorSets(bytes, false, ulong.MaxValue)).Count);
        Assert.Null(ThreadMigrationService.ParseRecords(bytes, 0));
    }
    [Fact]
    public void TruncationOversizedRecordsAndTrailingBytesRejectWholeBuffer()
    {
        var good = Record(0, (3, 0));
        foreach (var malformed in new[] { good[..^1], good.Concat(new byte[] { 0 }).ToArray(), new byte[8] }) {
            Assert.Null(WindowsTopologyBuffer.Parse(malformed));
            Assert.Empty(CpuAffinity.ParsePhysicalCoreProcessorSets(malformed, true, ulong.MaxValue));
            Assert.Equal((0, 0), CpuIdService.ParseOsTopology(malformed));
            Assert.Null(ThreadMigrationService.ParseRecords(malformed, 0));
        }
        BitConverter.GetBytes(int.MaxValue).CopyTo(good, 4);
        Assert.Null(WindowsTopologyBuffer.Parse(good));
    }
    [Fact]
    public void AffinityCountMustFitRecord()
    {
        var bytes = Record(0, (3, 0));
        BitConverter.GetBytes((ushort)2).CopyTo(bytes, 30);
        Assert.Null(WindowsTopologyBuffer.Parse(bytes));
    }
    [Fact]
    public void HeterogeneousCachesKeepSizesAndCrossGroupSharing()
    {
        var a = Record(2, (3, 0), (3, 1));
        var b = Record(2, (3, 0));
        a[8] = b[8] = 2;
        BitConverter.GetBytes(1024 * 1024u).CopyTo(a, 12);
        BitConverter.GetBytes(2 * 1024 * 1024u).CopyTo(b, 12);
        var topology = CpuTopologyService.ParseTopology(a.Concat(b).ToArray());
        Assert.NotNull(topology);
        Assert.Equal(2, topology!.Caches.Count);
        Assert.Contains(topology.Caches, row => row.Detail.Contains("1 MB") && row.Detail.Contains("4"));
        Assert.Contains(topology.Caches, row => row.Detail.Contains("2 MB"));
    }
    [Fact]
    public void NativeAndManagedBufferBudgetsRejectOversizedInput()
    {
        Assert.False(WindowsTopologyBuffer.IsWithinBudget(0));
        Assert.True(WindowsTopologyBuffer.IsWithinBudget(WindowsTopologyBuffer.MaxBufferBytes));
        Assert.False(WindowsTopologyBuffer.IsWithinBudget(WindowsTopologyBuffer.MaxBufferBytes + 1));
        Assert.False(WindowsTopologyBuffer.IsWithinBudget(uint.MaxValue));
        Assert.Null(WindowsTopologyBuffer.Parse(new byte[WindowsTopologyBuffer.MaxBufferBytes + 1]));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(2)]
    public void ProcessorAndCacheRecordsRejectEmptyMask(int relationship)
        => Assert.Null(WindowsTopologyBuffer.Parse(Record(relationship, (0, 0))));
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(6)]
    public void RepeatedGroupCannotInflateProcessorCount(int relationship)
        => Assert.Null(WindowsTopologyBuffer.Parse(Record(relationship, (1, 0), (2, 0))));
    [Fact]
    public void LegacyCacheZeroCountRetainsSingleAffinity()
    {
        var bytes = Record(2, (3, 0));
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 38);
        Assert.Equal(3UL, Assert.Single(Assert.Single(WindowsTopologyBuffer.Parse(bytes)!).Affinities).Mask);
    }
}
