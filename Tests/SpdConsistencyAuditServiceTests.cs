using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class SpdConsistencyAuditServiceTests
{
    private static SpdAuditSourceInput Native(
        string locator = "DIMM_A1",
        string vendor = "SK Hynix",
        string part = "HX432C16",
        string serial = "1234ABCD",
        int capacity = 8192,
        int speed = 3200,
        bool baseCrc = true,
        bool moduleCrc = true,
        int? year = 2024,
        int? week = 51)
        => new(SpdAuditSourceKind.NativeSpd, "原生 SPD ・ 測試匯流排 ・ 0x50", locator,
            vendor, part, serial, capacity, speed, year, week,
            baseCrc, moduleCrc, true, $"基本段 {(baseCrc ? "OK" : "不符")}；模組段 {(moduleCrc ? "OK" : "不符")}");

    private static SpdAuditSourceInput CpuZ(
        string locator = "DIMM_A1",
        string vendor = "SK Hynix",
        string part = "HX432C16",
        string? serial = null,
        int capacity = 8192,
        int speed = 3200,
        int? year = 2024,
        int? week = 51)
        => new(SpdAuditSourceKind.CpuZ, "CPU-Z 報告", locator, vendor, part, serial,
            capacity, speed, year, week);

    private static SpdAuditSourceInput Smbios(
        string locator = "DIMM_A1",
        string vendor = "SK Hynix",
        string part = "HX432C16",
        string serial = "1234ABCD",
        int capacity = 8192,
        int speed = 3200)
        => new(SpdAuditSourceKind.Smbios, "SMBIOS Type 17", locator, vendor, part, serial,
            capacity, speed);

    private static SpdSlotAudit Audit(
        SpdAuditSourceInput native,
        SpdAuditSourceInput? cpuz = null,
        SpdAuditSourceInput? smbios = null,
        CurrentMemoryTimingEvidence? current = null)
        => Assert.Single(SpdConsistencyAuditService.Audit(new SpdAuditInput(
            [native], cpuz is null ? [] : [cpuz], smbios is null ? [] : [smbios], current)));

    [Fact]
    public void 全部可用來源匹配時判為一致且每筆Finding可追溯()
    {
        var result = Audit(Native(), CpuZ(), Smbios(),
            new CurrentMemoryTimingEvidence("CPU-Z 目前時序（全機）", "DDR4", 3200, "16-18-18-36 2T"));

        Assert.Equal(SpdAuditVerdict.Consistent, result.Verdict);
        Assert.All(result.Findings, finding => Assert.NotEmpty(finding.Evidence));
        Assert.All(result.Findings.SelectMany(x => x.Evidence), evidence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(evidence.Source));
            Assert.False(string.IsNullOrWhiteSpace(evidence.Value));
        });
        Assert.Contains(result.Findings, x => x.Field == SpdAuditField.ManufactureDate);
    }

    [Fact]
    public void 外部來源缺失時保留原生證據並判缺資料()
    {
        var result = Audit(Native(), current: null);

        Assert.Equal(SpdAuditVerdict.MissingData, result.Verdict);
        var missing = Assert.Single(result.Findings, x => x.Field == SpdAuditField.SourceAvailability);
        Assert.Contains("CPU-Z", missing.Summary);
        Assert.Contains("SMBIOS", missing.Summary);
        Assert.Single(result.Sources);
    }

    [Fact]
    public void 單獨CRC壞只報矛盾_不武斷稱為重刷()
    {
        var result = Audit(Native(baseCrc: false), CpuZ(), Smbios());
        var crc = Assert.Single(result.Findings, x => x.Field == SpdAuditField.Checksum);

        Assert.Equal(SpdAuditVerdict.Conflict, result.Verdict);
        Assert.Equal(SpdAuditVerdict.Conflict, crc.Verdict);
        Assert.Contains("單獨不足", crc.Summary);
        Assert.DoesNotContain(result.Findings, x => x.Verdict == SpdAuditVerdict.Suspicious
            && x.Summary.Contains("至少兩類", StringComparison.Ordinal));
        Assert.DoesNotContain("曾被重刷", result.Summary);
    }

    [Fact]
    public void SMBIOS通用值視為缺資料而不是矛盾()
    {
        var generic = Smbios(vendor: "To Be Filled By O.E.M.", part: "Default string", serial: "00000000");
        var result = Audit(Native(), CpuZ(), generic);

        Assert.NotEqual(SpdAuditVerdict.Conflict, result.Verdict);
        Assert.Equal(SpdAuditVerdict.MissingData,
            Assert.Single(result.Findings, x => x.Field == SpdAuditField.SerialNumber).Verdict);
        Assert.DoesNotContain(result.Findings, x => x.Field is SpdAuditField.JedecVendor or SpdAuditField.PartNumber
            && x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Fact]
    public void 序號衝突清楚列出兩邊來源和值()
    {
        var result = Audit(Native(), CpuZ(serial: "1234ABCD"), Smbios(serial: "DEADBEEF"));
        var finding = Assert.Single(result.Findings, x => x.Field == SpdAuditField.SerialNumber);

        Assert.Equal(SpdAuditVerdict.Conflict, finding.Verdict);
        Assert.Contains(finding.Evidence, x => x.Kind == SpdAuditSourceKind.NativeSpd && x.Value == "1234ABCD");
        Assert.Contains(finding.Evidence, x => x.Kind == SpdAuditSourceKind.Smbios && x.Value == "DEADBEEF");
        Assert.Contains("不等同於偽造", result.Summary);
    }

    [Fact]
    public void 多項獨立異常才升級為可疑且措辭保守()
    {
        var result = Audit(Native(baseCrc: false), CpuZ(), Smbios(serial: "DEADBEEF"));

        Assert.Equal(SpdAuditVerdict.Suspicious, result.Verdict);
        var finding = Assert.Single(result.Findings, x => x.Verdict == SpdAuditVerdict.Suspicious
            && x.Summary.Contains("至少兩類", StringComparison.Ordinal));
        Assert.Contains("不能單獨證明", finding.Summary);
        Assert.Contains("不據此斷言", result.Summary);
    }

    [Fact]
    public void 多DIMM依唯一識別配對而非CPUZ清單順序()
    {
        var n1 = Native("DIMM_A1", part: "PART-A", serial: "AAAA0001");
        var n2 = Native("DIMM_B1", part: "PART-B", serial: "BBBB0002");
        var c2 = CpuZ("CPUZ #2", part: "PART-B", serial: "BBBB0002");
        var c1 = CpuZ("CPUZ #1", part: "PART-A", serial: "AAAA0001");
        var s2 = Smbios("DIMM_B1", part: "PART-B", serial: "BBBB0002");
        var s1 = Smbios("DIMM_A1", part: "PART-A", serial: "AAAA0001");

        var results = SpdConsistencyAuditService.Audit(new SpdAuditInput(
            [n1, n2], [c2, c1], [s2, s1]));

        Assert.Equal(2, results.Count);
        Assert.All(results, x => Assert.Equal(SpdAuditVerdict.Consistent, x.Verdict));
        Assert.Equal("PART-A", results[0].Sources.Single(x => x.Kind == SpdAuditSourceKind.CpuZ).PartNumber);
        Assert.Equal("PART-B", results[1].Sources.Single(x => x.Kind == SpdAuditSourceKind.CpuZ).PartNumber);
        Assert.DoesNotContain(results.SelectMany(x => x.Findings), x => x.Verdict == SpdAuditVerdict.Conflict);
    }

    [Fact]
    public void 多DIMM同型號無唯一識別時不強行配對()
    {
        var n1 = Native("BUS0-50", serial: "AAAA0001");
        var n2 = Native("BUS1-50", serial: "BBBB0002");
        var generic1 = CpuZ("DIMM #1", serial: null);
        var generic2 = CpuZ("DIMM #2", serial: null);

        var results = SpdConsistencyAuditService.Audit(new SpdAuditInput([n1, n2], [generic1, generic2], []));

        Assert.All(results, x => Assert.DoesNotContain(x.Sources, s => s.Kind == SpdAuditSourceKind.CpuZ));
        Assert.All(results, x => Assert.Contains(x.Findings,
            f => f.Field == SpdAuditField.SourceAvailability && f.Summary.Contains("CPU-Z", StringComparison.Ordinal)));
    }

    [Fact]
    public void Adapter重用既有原生解碼CPUZ與SMBIOS模型()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        var direct = new SpdDirectRead("處理器 iMC SMBus", 0x50, raw, SpdDecoder.Decode(raw)!);
        var cpuz = new SpdModule
        {
            Slot = "DIMM #1", Manufacturer = "SK Hynix", PartNumber = "ZhuQue_8G_Y",
            Size = "8192 MBytes", MaxJedec = "DDR4-3200 (1600 MHz)",
            ManufacturingDate = "Week 51/Year 24",
        };
        var smbios = new SmbiosDimmRow("DIMM_A1", "BANK 0", "8 GB", "DDR4", "3200 MT/s", "3200 MT/s",
            "SK Hynix", "58585858", "ZhuQue_8G_Y", "1");

        var nativeInput = SpdConsistencyAuditService.FromNative(direct, "DIMM_A1");
        var result = Audit(nativeInput, SpdConsistencyAuditService.FromCpuZ(cpuz),
            SpdConsistencyAuditService.FromSmbios(smbios));

        Assert.Equal("ZhuQue_8G_Y", nativeInput.PartNumber);
        Assert.Equal(8192, nativeInput.CapacityMiB);
        Assert.Equal(SpdAuditVerdict.Consistent, result.Verdict);
    }
}
