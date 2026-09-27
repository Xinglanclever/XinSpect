using Xunit;

namespace XinSpect.Tests;

/// <summary>整機驗機收攏服務的純函式部分:引擎跑在合成事實上,驗範圍標註與收攏正確。</summary>
public class MachineVerdictServiceTests
{
    private static readonly DateTime T = DateTime.UnixEpoch;

    private static VerifyFact Num(FactId id, double n) =>
        new(id, FactCatalog.Name(id), n.ToString("0.##"), n, "", FactSource.Smbios, "測試", false,
            FactTrust.FirmwareReported, T);

    private static VerifyFact Text(FactId id, string s) =>
        new(id, FactCatalog.Name(id), s, null, "", FactSource.Smbios, "測試", false, FactTrust.FirmwareReported, T);

    [Fact]
    public void 整機規則掛整機範圍_逐碟規則掛各自的碟標籤()
    {
        // 整機:記憶體混批(矛盾)。碟:一顆通電少寫入多(矛盾)。
        var machine = new VerifyFacts([
            Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|SK Hynix"),
            Text(FactId.DimmPartNumbers, "A|B"),
        ]);
        var disk = new VerifyFacts([
            Num(FactId.NvmeDataUnitsWritten, 80000), Num(FactId.NvmePowerOnHours, 100),
        ]);

        var v = MachineVerdictService.Build(T, machine, [("碟0 ・ 測試SSD", disk)]);

        var mem = v.Lines.Single(l => l.Finding.Id == "R-MEM-01");
        Assert.Equal("整機", mem.Scope);
        var ssd = v.Lines.Single(l => l.Finding.Id == "R-SSD-01");
        Assert.Equal("碟0 ・ 測試SSD", ssd.Scope);
        Assert.Equal(VerifyVerdict.Conflict, ssd.Finding.Verdict);
    }

    [Fact]
    public void 多顆碟_各自跑一遍儲存規則_行數等於碟數乘規則數()
    {
        var empty = new VerifyFacts([]);
        var disk1 = new VerifyFacts([Num(FactId.NvmeCriticalWarning, 0)]);   // 只餵一條規則的依賴
        var disk2 = new VerifyFacts([Num(FactId.NvmeCriticalWarning, 8)]);

        var v = MachineVerdictService.Build(T, empty, [("碟A", disk1), ("碟B", disk2)]);

        // 每顆碟都跑全部 6 條 Disk 規則(缺依賴者判讀不到)
        Assert.Equal(6, v.Lines.Count(l => l.Scope == "碟A"));
        Assert.Equal(6, v.Lines.Count(l => l.Scope == "碟B"));
        // 碟B 的 R-SSD-04 關鍵警告非零 → 矛盾;碟A → 相符
        Assert.Equal(VerifyVerdict.Match, v.Lines.Single(l => l.Scope == "碟A" && l.Finding.Id == "R-SSD-04").Finding.Verdict);
        Assert.Equal(VerifyVerdict.Conflict, v.Lines.Single(l => l.Scope == "碟B" && l.Finding.Id == "R-SSD-04").Finding.Verdict);
    }

    [Fact]
    public void 沒有碟時_只跑整機規則()
    {
        var v = MachineVerdictService.Build(T, new VerifyFacts([]), []);
        Assert.All(v.Lines, l => Assert.Equal("整機", l.Scope));
        Assert.DoesNotContain(v.Lines, l => l.Finding.Id.StartsWith("R-SSD-"));
    }
}
