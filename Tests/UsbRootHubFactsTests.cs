using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// USB 根集線器埠數金標（U10 第三圈 NP-0003）：合成的 <c>USB_NODE_INFORMATION</c> 逐欄比對，
/// 加上「埠數（硬體說的）」與「在場子裝置數（PnP 樹說的）」並排時的四種情形。
/// </summary>
public class UsbRootHubFactsTests
{
    // ── 合成 IOCTL 輸出 ─────────────────────────────────────────────────

    /// <summary>
    /// 照 Windows 標頭的真實佈局造資料（<c>usbspec.h</c> 在 <c>PSHPACK1.H</c> 内，位移＝宣告順序）：
    /// <c>bNumberOfPorts</c> 是**一個位元組**（位移 6），<c>wHubCharacteristics</c> 從位移 7 開始。
    /// 替身必須跟規格一致，不能跟實作的假設一致——先前這裡寫成 u16，於是實作讀錯也一路綠燈。
    /// </summary>
    private static byte[] NodeInfo(int nodeType = 0, byte descriptorLength = 9, byte descriptorType = 0x29,
        byte ports = 4, ushort characteristics = 0x0009)
    {
        var b = new byte[76];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0, 4), nodeType);
        b[4] = descriptorLength;
        b[5] = descriptorType;
        b[6] = ports;                                                              // bNumberOfPorts（u8）
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(7, 2), characteristics);  // wHubCharacteristics（u16）
        return b;
    }

    private static DeviceDiagnosticDevice Hub(string instanceId, params DeviceDiagnosticDevice[] children) =>
        new() { InstanceId = instanceId, IsPresent = true, Children = children.ToList() };

    private static DeviceDiagnosticDevice Child(string instanceId, bool present) =>
        new() { InstanceId = instanceId, IsPresent = present };

    private static DeviceDiagnosticReport Report(params DeviceDiagnosticDevice[] devices) =>
        new(devices.ToList(), devices.ToList());

    /// <summary>依 instance ID 給答案的假接縫；沒列到的就回「開檔失敗（5）」。</summary>
    private sealed class FakeHubProbe(params (string Id, ushort? Ports, uint Error, string? Why)[] replies) : IUsbHubProbe
    {
        public List<string> Asked { get; } = [];

        public UsbHubRead Read(string instanceId)
        {
            Asked.Add(instanceId);
            foreach (var r in replies)
                if (string.Equals(r.Id, instanceId, StringComparison.Ordinal))
                    return new UsbHubRead(instanceId, r.Ports, r.Error, r.Why);
            return new UsbHubRead(instanceId, null, 5, "開檔失敗（5）");
        }
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 解碼器 ──────────────────────────────────────────────────────────

    [Fact]
    public void 節點資訊_逐欄比對()
    {
        var n = UsbHubNodeDecoder.ParseNodeInformation(NodeInfo(ports: 26));
        Assert.NotNull(n);
        Assert.Equal(0, n.NodeType);
        Assert.Equal(9, n.DescriptorLength);
        Assert.Equal(0x29, n.DescriptorType);
        Assert.Equal(26, n.NumberOfPorts);
        Assert.True(n.IsHubDescriptor);
    }

    [Fact]
    public void 埠數只取一個位元組_集線器特性不黏進來()
    {
        // 真機最常見的 0x0009（per-port 供電切換、無 over-current 回報）：若把位移 6 讀成 u16，
        // 低位元組 0x09 會變成埠數的高位——4 埠被讀成 0x0904＝2308，後面的「空埠」全部跟著錯。
        var n = UsbHubNodeDecoder.ParseNodeInformation(NodeInfo(ports: 4, characteristics: 0x0009))!;
        Assert.Equal(4, n.NumberOfPorts);
    }

    [Fact]
    public void 超級速度集線器_0x2A也要認()
    {
        // xHCI 根集線器用的是 SuperSpeed hub descriptor（USB 3.0 §10.13.2.1）。
        // 只認 0x29 的話，現代機器上的每一個 USB 3 根永遠拿不到埠數。
        var n = UsbHubNodeDecoder.ParseNodeInformation(
            NodeInfo(descriptorType: 0x2A, descriptorLength: 12, ports: 6))!;
        Assert.True(n.IsHubDescriptor);
        Assert.Equal(6, n.NumberOfPorts);
    }

    [Fact]
    public void 節點資訊_不足12位元組回null()
    {
        Assert.Null(UsbHubNodeDecoder.ParseNodeInformation(new byte[11]));
    }

    [Fact]
    public void 節點資訊_不是hub節點回null()
    {
        // NodeType=1 是 UsbMiParent：上面那一層不是 hub，埠數沒有意義。
        Assert.Null(UsbHubNodeDecoder.ParseNodeInformation(NodeInfo(nodeType: 1)));
    }

    [Fact]
    public void 描述符型別不是hub_不採信埠數()
    {
        var n = UsbHubNodeDecoder.ParseNodeInformation(NodeInfo(descriptorType: 0x22))!;
        Assert.False(n.IsHubDescriptor);
    }

    [Fact]
    public void 裝置介面路徑_反斜線換井號且小寫()
    {
        Assert.Equal(
            @"\\?\usb#root_hub30#5&7d03ca&0&0#{f18a0e88-c30c-11d0-8815-00a0c906bed8}",
            SystemUsbHubProbe.InterfacePath(@"USB\ROOT_HUB30\5&7D03CA&0&0"));
    }

    // ── 服務：正常路徑 ──────────────────────────────────────────────────

    [Fact]
    public void 空埠等於埠數減在場子裝置()
    {
        var report = Report(
            Hub(@"USB\ROOT_HUB30\A", Child(@"USB\VID_1&PID_1\1", true), Child(@"USB\VID_1&PID_2\2", false)),
            Hub(@"USB\ROOT_HUB30\B"));
        var probe = new FakeHubProbe((@"USB\ROOT_HUB30\A", 4, 0, null), (@"USB\ROOT_HUB30\B", 26, 0, null));
        var facts = UsbRootHubFactsService.Collect(report, DateTimeOffset.UnixEpoch, probe);

        // 不在場的子裝置不佔埠（Ghost 是歷史留下的，不是現在插著的那一顆）
        Assert.Contains("在場子裝置 1 個", Fact(facts, "usb.hub.ports.0001").Value);
        Assert.Contains("空埠 3 個", Fact(facts, "usb.hub.ports.0001").Value);
        Assert.Contains("空埠 26 個", Fact(facts, "usb.hub.ports.0002").Value);
        var summary = Fact(facts, "usb.hub.summary");
        Assert.Equal(30d, summary.NumericValue!.Value);
        Assert.Equal(FactAvailability.Present, summary.Availability);
        Assert.Contains("2 顆 USB 根集線器", summary.Value);
    }

    [Fact]
    public void 子裝置比埠多_不給空埠數字()
    {
        // 複合裝置／內建裝置可以不佔獨立埠，所以「埠數 − 子裝置數」會變成負的。
        var report = Report(Hub(@"USB\ROOT_HUB30\A",
            Child(@"USB\A\1", true), Child(@"USB\A\2", true), Child(@"USB\A\3", true)));
        var facts = UsbRootHubFactsService.Collect(report, DateTimeOffset.UnixEpoch,
            new FakeHubProbe((@"USB\ROOT_HUB30\A", 2, 0, null)));

        Assert.Contains("**在場子裝置 3 個比埠數多**", Fact(facts, "usb.hub.ports.0001").Value);
        Assert.Contains("不給數字", Fact(facts, "usb.hub.ports.0001").Value);
        var summary = Fact(facts, "usb.hub.summary");
        Assert.DoesNotContain("空埠 1 個", summary.Value);
        Assert.Contains("空埠總數**不給**", summary.Value);
    }

    [Fact]
    public void 埠數讀不到_那一顆仍佔家族成員且不計入總數()
    {
        var report = Report(Hub(@"USB\ROOT_HUB30\A"), Hub(@"USB\ROOT_HUB30\B"));
        var facts = UsbRootHubFactsService.Collect(report, DateTimeOffset.UnixEpoch,
            new FakeHubProbe((@"USB\ROOT_HUB30\A", 4, 0, null), (@"USB\ROOT_HUB30\B", null, 5, "開檔失敗（5）")));

        var unreadable = Fact(facts, "usb.hub.ports.0002");
        Assert.Equal(FactAvailability.InsufficientPrivilege, unreadable.Availability);
        Assert.Null(unreadable.NumericValue);
        Assert.Contains("開檔失敗", unreadable.Value);

        var summary = Fact(facts, "usb.hub.summary");
        Assert.Equal(FactAvailability.Unknown, summary.Availability);
        Assert.Null(summary.NumericValue); // 非 Present 不得帶數字
        Assert.Contains("另有 1 顆根集線器的埠數讀不到", summary.Value);
        Assert.Contains("只是下限", summary.UnavailableReason);
    }

    [Fact]
    public void 沒有根集線器_標不適用而不是讀不到()
    {
        var facts = UsbRootHubFactsService.Collect(Report(Child(@"PCI\VEN_1\1", true)), DateTimeOffset.UnixEpoch,
            new FakeHubProbe());
        var summary = Fact(facts, "usb.hub.summary");
        Assert.Equal(FactAvailability.NotApplicable, summary.Availability);
        Assert.Contains("不是「讀不到埠數」", summary.UnavailableReason);
        // 界線宣告與機器無關，永遠都在
        Assert.Equal(FactAvailability.Present, Fact(facts, "usb.hub.boundary").Availability);
    }

    [Fact]
    public void 界線宣告_明說控制器版本在MMIO讀不到()
    {
        var boundary = Fact(UsbRootHubFactsService.Collect(
            Report(Hub(@"USB\ROOT_HUB30\A")), DateTimeOffset.UnixEpoch,
            new FakeHubProbe((@"USB\ROOT_HUB30\A", 4, 0, null))), "usb.hub.boundary");
        Assert.Equal(FactAvailability.Present, boundary.Availability);
        Assert.Null(boundary.UnavailableReason);
        Assert.Contains("MMIO", boundary.Value);
        Assert.Contains("不遞迴外接集線器", boundary.Value);
        Assert.Contains("不是「可用頻寬」", boundary.Value);
    }

    [Fact]
    public void 三種根集線器型別要分得出來()
    {
        var report = Report(Hub(@"USB\ROOT_HUB30\A"), Hub(@"USB\ROOT_HUB20\B"), Hub(@"USB\ROOT_HUB\C"));
        var probe = new FakeHubProbe(
            (@"USB\ROOT_HUB\C", 2, 0, null),
            (@"USB\ROOT_HUB20\B", 2, 0, null),
            (@"USB\ROOT_HUB30\A", 2, 0, null));
        var facts = UsbRootHubFactsService.Collect(report, DateTimeOffset.UnixEpoch, probe);
        // 成員按 instance ID 排序（序數比較）：'2' < '3' < '\'，所以
        // USB\ROOT_HUB20\B < USB\ROOT_HUB30\A < USB\ROOT_HUB\C。
        Assert.Contains("USB 2.0（EHCI）", Fact(facts, "usb.hub.ports.0001").Value);
        Assert.Contains("USB 3.x（xHCI）", Fact(facts, "usb.hub.ports.0002").Value);
        Assert.Contains("USB 1.1（UHCI／OHCI）", Fact(facts, "usb.hub.ports.0003").Value);
    }

    [Fact]
    public void 每顆根只問一次_不重複開檔()
    {
        var report = Report(Hub(@"USB\ROOT_HUB30\A"));
        var probe = new FakeHubProbe((@"USB\ROOT_HUB30\A", 4, 0, null));
        UsbRootHubFactsService.Collect(report, DateTimeOffset.UnixEpoch, probe);
        Assert.Single(probe.Asked);
    }
}
