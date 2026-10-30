using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// xHCI 能力區金標（U10 第三圈 NP-0002）：逐欄位照 xHCI 規格 1.1 §5.3 擺位元組，
/// 加上「讀不到時要說出缺哪一步」的四種失敗。
/// </summary>
public class XhciFactsTests
{
    /// <summary>
    /// 造一段能力區。預設值照常見的 xHCI 1.1 控制器：CAPLENGTH 0x40、HCIVERSION 0x0110、
    /// 8 slots／1 interrupter／4 ports、AC64、CSZ、xECP 0x0010。
    /// </summary>
    private static byte[] Capability(
        byte caplength = 0x40,
        ushort hciVersion = 0x0110,
        byte maxSlots = 8,
        byte maxInterrupters = 1,
        byte maxPorts = 4,
        uint hccParams1 = (1u << 0) | (1u << 2) | (0x0010u << 16),
        int length = 0x40)
    {
        var b = new byte[Math.Max(length, 0x14)];
        b[0] = caplength;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2, 2), hciVersion);
        uint sp1 = maxSlots | ((uint)maxInterrupters << 8) | ((uint)maxPorts << 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), sp1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x10, 4), hccParams1);
        return b;
    }

    /// <summary>在 <paramref name="offset"/> 放一個「支援協定」擴充能力（ID 0x02）。</summary>
    private static void WriteSupportedProtocol(byte[] b, int offset, byte majorRevision, byte type,
        byte nextDwordOffset, params (byte Id, ushort Mantissa, byte Exp, byte Protocol)[] speeds)
    {
        b[offset] = 0x02;                       // Capability ID
        b[offset + 1] = nextDwordOffset;        // Next Capability Pointer（DWORD 位移）
        b[offset + 2] = 0x00;                   // MinorRevision
        b[offset + 3] = majorRevision;          // MajorRevision
        b[offset + 4] = 0x00;                   // NameString
        b[offset + 5] = 0x00;                   // CompliancePortPower
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset + 6, 2), type);
        b[offset + 0x0C] = (byte)speeds.Length;
        for (int i = 0; i < speeds.Length; i++)
        {
            int at = offset + 0x10 + i * 4;
            uint psi = (uint)(speeds[i].Id & 0x0F)
                | ((uint)(speeds[i].Protocol & 0x03) << 5)
                | ((uint)(speeds[i].Mantissa & 0x0F) << 16)
                | ((uint)(speeds[i].Exp & 0x0F) << 20);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at, 4), psi);
        }
    }

    private sealed class FakePci(bool available, uint? bar0, string? why = null) : IPciConfigReader
    {
        public bool Available => available;
        public string? UnavailableReason => why;
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => bar0;
    }

    private sealed class FakeMmio(bool available, byte[]? block, string? why = null) : IMmioReader
    {
        public bool Available => available;
        public string? UnavailableReason => why;
        public string? BackendName => "fake";
        public string? LastFailReason => null;

        public byte[]? ReadBlock(ulong physicalAddress, int length) => block;
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    // ── 解碼器 ──────────────────────────────────────────────────────────

    [Fact]
    public void 能力區_逐欄比對()
    {
        var c = XhciCapabilityDecoder.ParseCapabilities(Capability())!;
        Assert.Equal(0x40, c.Caplength);
        Assert.Equal(0x0110, c.HciVersion);
        Assert.Equal("1.10", c.VersionText);
        Assert.Equal(8, c.MaxSlots);
        Assert.Equal(1, c.MaxInterrupters);
        Assert.Equal(4, c.MaxPorts);
        Assert.True(c.Ac64);
        Assert.True(c.Csz);
        Assert.Equal(0x0010, c.Xecp);
    }

    [Fact]
    public void 能力區_不足20位元組回null()
    {
        Assert.Null(XhciCapabilityDecoder.ParseCapabilities(new byte[0x13]));
    }

    [Fact]
    public void 版本BCD_0100是一點零零()
    {
        var c = XhciCapabilityDecoder.ParseCapabilities(Capability(hciVersion: 0x0100))!;
        Assert.Equal("1.00", c.VersionText);
    }

    [Fact]
    public void Scratchpad數量由高低位組起來()
    {
        // MaxScratchpadLo（bits20:16）＝3、MaxScratchpadHi（bits10:8）＝1 → 3 + 1*32 = 35
        uint sp2 = (3u << 16) | (1u << 8);
        var b = Capability();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8, 4), sp2);
        Assert.Equal(35, XhciCapabilityDecoder.ParseCapabilities(b)!.ScratchpadBuffers);
    }

    [Fact]
    public void 支援協定_逐欄與速度表()
    {
        var b = Capability(length: 0x100);
        // xECP 0x0010 → 位移 0x40
        WriteSupportedProtocol(b, 0x40, majorRevision: 0x02, type: 0x01, nextDwordOffset: 0x00,
            (1, 480, 0, 0), (2, 12, 0, 0), (3, 480, 1, 0));

        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        var protocols = XhciCapabilityDecoder.ParseSupportedProtocols(b, caps);
        Assert.Single(protocols);
        var p = protocols[0];
        Assert.Equal("USB 2.0", p.Name);
        Assert.Equal(3, p.Speeds.Count);
        // 速度值欄位的位界在規格表與 Linux 標頭互相矛盾 → 本版不換算，只給 PSIV 與原值
        Assert.Equal(1, p.Speeds[0].SpeedId);
        Assert.Equal(2, p.Speeds[1].SpeedId);
        Assert.Equal(3, p.Speeds[2].SpeedId);
        Assert.Equal("USB 2.0", p.Speeds[0].LinkProtocolName);
    }

    [Fact]
    public void 支援協定_USB3要認得()
    {
        var b = Capability(length: 0x100);
        WriteSupportedProtocol(b, 0x40, majorRevision: 0x03, type: 0x02, nextDwordOffset: 0x00,
            (1, 5, 0, 1), (2, 10, 0, 1));
        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        var p = XhciCapabilityDecoder.ParseSupportedProtocols(b, caps)[0];
        Assert.Equal("USB 3.x（SuperSpeed）", p.Name);
        Assert.Equal(1, p.Speeds[0].SpeedId);
        Assert.Equal("USB 3.0", p.Speeds[0].LinkProtocolName);
    }

    [Fact]
    public void 未收錄的主修訂不命名()
    {
        var b = Capability(length: 0x100);
        WriteSupportedProtocol(b, 0x40, majorRevision: 0x07, type: 0x00, nextDwordOffset: 0x00);
        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        var p = XhciCapabilityDecoder.ParseSupportedProtocols(b, caps)[0];
        Assert.Contains("本版不命名", p.Name);
    }

    [Fact]
    public void xECP為零時沒有擴充串列()
    {
        var b = Capability(hccParams1: 1u << 0);   // xECP = 0
        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        Assert.Empty(XhciCapabilityDecoder.ParseSupportedProtocols(b, caps));
    }

    [Fact]
    public void 環狀串列不會卡死()
    {
        var b = Capability(length: 0x100);
        // 位移 0x40 的下一項指回自己
        b[0x40] = 0x01;   // 非支援協定的能力
        b[0x41] = 0x10;   // next = 0x10 DWORD = 0x40 → 指回自己
        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        var p = XhciCapabilityDecoder.ParseSupportedProtocols(b, caps);
        Assert.Empty(p);
    }

    [Fact]
    public void 速度值不換算_只給原值()
    {
        // 這一條是「刻意不做」的守門：換算需要唯一定義的位界，而那件事本版查不到一致答案。
        var b = Capability(length: 0x100);
        WriteSupportedProtocol(b, 0x40, 0x02, 0x01, 0x00, (1, 480, 0, 0));
        var caps = XhciCapabilityDecoder.ParseCapabilities(b)!;
        var psi = XhciCapabilityDecoder.ParseSupportedProtocols(b, caps)[0].Speeds[0];
        Assert.Equal(0x01u, psi.Raw & 0x0F);            // PSIV 仍可讀
        Assert.DoesNotContain("Mb/s", psi.LinkProtocolName);
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有控制器_標不適用而不是讀不到()
    {
        var facts = XhciFactsService.Collect([], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, XhciFactsService.SummaryKey);
        Assert.Equal(FactAvailability.NotApplicable, s.Availability);
        Assert.Contains("不是「讀不到版本」", s.UnavailableReason);
        Assert.Equal(FactAvailability.Present, Fact(facts, XhciFactsService.BoundaryKey).Availability);
    }

    [Fact]
    public void 讀不到能力區_標不足權限且不從驅動版本反推()
    {
        var facts = XhciFactsService.Collect(
            [new XhciControllerSample("PCI\\VEN_8086&D", null, "MMIO 不可用")], DateTimeOffset.UnixEpoch);
        var c = Fact(facts, XhciFactsService.ControllerPrefix + "0001");
        Assert.Equal(FactAvailability.InsufficientPrivilege, c.Availability);
        Assert.Null(c.NumericValue);
        Assert.Contains("MMIO 不可用", c.UnavailableReason);
        Assert.Contains("不從驅動版本反推", c.UnavailableReason);
    }

    [Fact]
    public void 解開一個控制器_值要含版本與埠數()
    {
        var b = Capability(length: 0x100);
        WriteSupportedProtocol(b, 0x40, 0x02, 0x01, 0x00, (1, 480, 0, 0));
        var facts = XhciFactsService.Collect(
            [new XhciControllerSample("PCI\\VEN_8086&D", b, null, 0xFE000000)], DateTimeOffset.UnixEpoch);
        var c = Fact(facts, XhciFactsService.ControllerPrefix + "0001");
        Assert.Equal(FactAvailability.Present, c.Availability);
        Assert.Equal(4d, c.NumericValue!.Value);
        Assert.Contains("HCIVERSION 0x0110（1.10）", c.Value);
        Assert.Contains("**4 個埠**", c.Value);
        Assert.Contains("BAR 0xFE000000", c.Value);
        Assert.Contains("USB 2.0 最小修訂 0", c.Value);
        Assert.Contains("PSIV 1（USB 2.0", c.Value);
        Assert.Contains("原值 0x", c.Value);
        Assert.Contains("不是「現在跑幾倍速」", c.Value);
    }

    [Fact]
    public void 部分控制器讀不到_總覽要說只是下限()
    {
        var b = Capability();
        var facts = XhciFactsService.Collect(
        [
            new XhciControllerSample("A", b, null, 0x1000),
            new XhciControllerSample("B", null, "PCI 不可用"),
        ], DateTimeOffset.UnixEpoch);
        var s = Fact(facts, XhciFactsService.SummaryKey);
        Assert.Equal(FactAvailability.Unknown, s.Availability);
        Assert.Null(s.NumericValue);
        Assert.Contains("讀得到 1 個、解得開 1 個", s.Value);
        Assert.Contains("只是下限", s.UnavailableReason);
    }

    [Fact]
    public void 太短的能力區標讀取錯誤()
    {
        var facts = XhciFactsService.Collect(
            [new XhciControllerSample("A", new byte[8], null)], DateTimeOffset.UnixEpoch);
        var c = Fact(facts, XhciFactsService.ControllerPrefix + "0001");
        Assert.Equal(FactAvailability.ReadError, c.Availability);
        Assert.Contains("不猜欄位", c.UnavailableReason);
    }

    [Fact]
    public void 界線宣告_五條都要在()
    {
        var f = Fact(XhciFactsService.Collect([], DateTimeOffset.UnixEpoch), XhciFactsService.BoundaryKey);
        Assert.Contains("PCI BAR 的 MMIO", f.Value);
        Assert.Contains("作業暫存器、埠狀態、裝置插槽一律不讀", f.Value);
        Assert.Contains("HCSPARAMS3", f.Value);
        Assert.Contains("不寫任何暫存器", f.Value);
        Assert.Contains("兩者不同不是矛盾，是不同層", f.Value);
    }

    // ── 讀取器 ──────────────────────────────────────────────────────────

    [Fact]
    public void 控制器清單來自ROOT_HUB30的父節點()
    {
        var report = new DeviceDiagnosticReport(
        [
            new DeviceDiagnosticDevice { InstanceId = @"USB\ROOT_HUB30\1", ParentInstanceId = @"PCI\VEN_8086&DEV_A2AF\3&1&0&A0" },
            new DeviceDiagnosticDevice { InstanceId = @"USB\ROOT_HUB30\2", ParentInstanceId = @"PCI\VEN_8086&DEV_A2AF\3&1&0&A0" },
            new DeviceDiagnosticDevice { InstanceId = @"USB\ROOT_HUB20\3", ParentInstanceId = @"PCI\VEN_8086&DEV_1E2D\3&1&0&B0" },
        ], []);
        var ids = XhciControllerReader.FindControllers(report);
        Assert.Single(ids);   // 去重：兩個 ROOT_HUB30 同一個父
        Assert.StartsWith(@"PCI\VEN_8086&DEV_A2AF", ids[0]);
    }

    [Fact]
    public void 位置解碼_舊式格式()
    {
        var loc = XhciControllerReader.ParseLocation(@"PCI\VEN_8086&DEV_A2AF\0:1F.5");
        Assert.NotNull(loc);
        Assert.Equal(0, loc!.Value.Bus);
        Assert.Equal(0x1F, loc.Value.Device);
        Assert.Equal(5, loc.Value.Function);
    }

    [Fact]
    public void 位置解碼_ACPI格式本版不認()
    {
        Assert.Null(XhciControllerReader.ParseLocation(@"PCI\VEN_8086&DEV_A2AF\3&11583659&0&A0"));
    }

    [Fact]
    public void PCI不可用_理由要指名那一步()
    {
        var s = XhciControllerReader.Read(@"PCI\VEN_1\0:1F.5",
            new FakePci(false, null, "缺 ring0"), new FakeMmio(true, Capability()));
        Assert.Null(s.Mmio);
        Assert.Contains("PCI 設定空間不可用", s.Why);
    }

    [Fact]
    public void MMIO不可用_理由要指名那一步()
    {
        var s = XhciControllerReader.Read(@"PCI\VEN_1\0:1F.5",
            new FakePci(true, 0xFE000000), new FakeMmio(false, null, "未載入驅動"));
        Assert.Null(s.Mmio);
        Assert.Contains("MMIO 不可用", s.Why);
    }

    [Fact]
    public void BAR是IO空間_不可用()
    {
        var s = XhciControllerReader.Read(@"PCI\VEN_1\0:1F.5",
            new FakePci(true, 0x0000C001), new FakeMmio(true, Capability()));
        Assert.Null(s.Mmio);
        Assert.Contains("I/O 空間", s.Why);
    }

    [Fact]
    public void BAR是零_不可用()
    {
        var s = XhciControllerReader.Read(@"PCI\VEN_1\0:1F.5",
            new FakePci(true, 0), new FakeMmio(true, Capability()));
        Assert.Null(s.Mmio);
        Assert.Contains("沒有宣告記憶體基底", s.Why);
    }

    [Fact]
    public void 成功路徑_要回BAR位址()
    {
        var s = XhciControllerReader.Read(@"PCI\VEN_1\0:1F.5",
            new FakePci(true, 0xFE000004), new FakeMmio(true, Capability()));
        Assert.NotNull(s.Mmio);
        Assert.Equal(0xFE000000UL, s.BarAddress);   // 低位元型別位被遮掉
    }
}
