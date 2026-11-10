using System.Diagnostics;
using System.Net.NetworkInformation;
using Xunit;

namespace XinSpect.Tests;

public class NetworkSamplingTests
{
    private sealed class Nic(string id) : NetworkInterface
    {
        public long Rx, Tx;
        public bool Fail;
        public override string Id => id;
        public override string Name => id;
        public override string Description => "測試網卡";
        public override long Speed => 1_000_000_000;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ethernet;
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
        public override IPInterfaceProperties GetIPProperties() => throw new NotSupportedException();
        public override IPInterfaceStatistics GetIPStatistics()
            => Fail ? throw new NetworkInformationException() : new Stats(Rx, Tx);
    }

    private sealed class Stats(long rx, long tx) : IPInterfaceStatistics
    {
        public override long BytesReceived => rx;
        public override long BytesSent => tx;
        public override long IncomingPacketsDiscarded => 0;
        public override long IncomingPacketsWithErrors => 0;
        public override long IncomingUnknownProtocolPackets => 0;
        public override long NonUnicastPacketsReceived => 0;
        public override long NonUnicastPacketsSent => 0;
        public override long OutgoingPacketsDiscarded => 0;
        public override long OutgoingPacketsWithErrors => 0;
        public override long OutputQueueLength => 0;
        public override long UnicastPacketsReceived => 0;
        public override long UnicastPacketsSent => 0;
    }

    [Fact]
    public void 每秒統計保持即時但不重複列舉組態()
    {
        long ticks = 0;
        int enumerations = 0;
        var nic = new Nic("a") { Rx = 500_000, Tx = 250_000 };
        using var service = new NetworkService(() => { enumerations++; return [nic]; }, () => ticks);
        for (int i = 1; i <= 5; i++)
        {
            ticks = i * Stopwatch.Frequency;
            nic.Rx += 1000;
            nic.Tx += 2000;
            service.Refresh();
            Assert.Equal(1000, service.TotalDownBps);
            Assert.Equal(2000, service.TotalUpBps);
        }
        Assert.Equal(1, enumerations);
        ticks = 30 * Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(2, enumerations);
    }

    [Fact]
    public void 網路變更後下一拍重新列舉並加入新介面()
    {
        long ticks = 0;
        var a = new Nic("a");
        var b = new Nic("b") { Rx = 900_000 };
        NetworkInterface[] nics = [a];
        using var service = new NetworkService(() => nics, () => ticks);
        nics = [a, b];
        service.InvalidateInventory();
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(2, service.Adapters.Count);
        Assert.Equal(0, service.TotalDownBps);
        ticks += Stopwatch.Frequency;
        b.Rx += 4000;
        service.Refresh();
        Assert.Equal(4000, service.TotalDownBps);
    }

    [Fact]
    public void 首次統計失敗後恢復不會把開機累計當流量()
    {
        long ticks = 0;
        var nic = new Nic("a") { Fail = true, Rx = 900_000 };
        using var service = new NetworkService(() => [nic], () => ticks);
        nic.Fail = false;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(0, service.TotalDownBps);
        ticks += Stopwatch.Frequency;
        nic.Rx += 1000;
        service.Refresh();
        Assert.Equal(1000, service.TotalDownBps);
    }

    [Fact]
    public void 介面消失時清掉舊速率且重接不產生尖峰()
    {
        long ticks = 0;
        var nic = new Nic("a");
        NetworkInterface[] nics = [nic];
        using var service = new NetworkService(() => nics, () => ticks);
        nic.Rx = 1000;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(1000, service.Adapters[0].DownBps);
        nics = [];
        service.InvalidateInventory();
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(0, service.Adapters[0].DownBps);
        nics = [nic];
        nic.Rx = 900_000;
        service.InvalidateInventory();
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(0, service.TotalDownBps);
    }

    [Fact]
    public void 計數器重設時重新建立基準()
    {
        long ticks = 0;
        var nic = new Nic("a") { Rx = 100_000, Tx = 10_000 };
        using var service = new NetworkService(() => [nic], () => ticks);
        nic.Rx = 10;
        nic.Tx = 20;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(0, service.TotalDownBps);
        nic.Rx += 1000;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(1000, service.TotalDownBps);
    }

    [Fact]
    public void 列舉失敗保留快取並重試且釋放後停止採集()
    {
        long ticks = 0;
        int enumerations = 0;
        bool fail = false;
        var nic = new Nic("a");
        using var service = new NetworkService(() =>
        {
            enumerations++;
            if (fail) throw new NetworkInformationException();
            return [nic];
        }, () => ticks);
        fail = true;
        service.InvalidateInventory();
        nic.Rx += 1000;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(1000, service.TotalDownBps);
        fail = false;
        nic.Rx += 1000;
        ticks += Stopwatch.Frequency;
        service.Refresh();
        Assert.Equal(3, enumerations);
        Assert.Equal(1000, service.TotalDownBps);
        service.Dispose();
        service.InvalidateInventory();
        service.Refresh();
        Assert.Equal(3, enumerations);
    }
}
