using System.IO;
using System.Management;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 差分測試（V7 T3／WP50）：同一份 SMBIOS 表、<b>兩個獨立實作</b>——本專案解碼器
/// （<c>Fixtures/smbios-real.bin</c> 實機傾印）vs 微軟 WMI 提供者（Win32_PhysicalMemory）。
/// 上游同源（都是韌體給的表），但解析路徑完全不同：我們的 offset 錯了就會跟微軟的答案對不上——
/// 這是「兩邊都錯但錯得一樣」抓不到、只能靠對照另一個實作的地方。
/// </summary>
/// <remarks>真機測試：fixture 與本機硬體必須一致；若換過 RAM，請重錄 fixture（見 Fixtures/README.md）。</remarks>
public class SmbiosDifferentialTests
{
    private static VerifyFacts Facts()
        => new(SmbiosFacts.From(SmbiosParser.Parse(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "smbios-real.bin"))),
            DateTime.UnixEpoch));

    private static List<ManagementObject> WmiModules()
    {
        var list = new List<ManagementObject>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
        foreach (ManagementObject m in searcher.Get()) list.Add(m);
        return list;
    }

    private static uint U32(ManagementObject m, string prop)
    {
        var v = m[prop];
        return v is null ? 0 : Convert.ToUInt32(v);
    }

    [Fact]
    public void 模組數_我們的解碼與微軟WMI一致()
    {
        int ours = (int)Facts().Num(FactId.DimmCount)!.Value;
        int wmi = WmiModules().Count;
        Assert.True(ours == wmi,
            $"SMBIOS 解碼得 {ours} 條、WMI 得 {wmi} 條——兩個獨立實作不一致。" +
            "先確認 fixture 是否過期（本機硬體換過沒有），再查 Type 17 位移。");
    }

    [Fact]
    public void 總容量MiB_兩個實作一致()
    {
        double ours = Facts().Num(FactId.DimmSizeTotalMiB)!.Value;
        ulong wmiBytes = WmiModules().Aggregate(0UL, (sum, m) => sum + Convert.ToUInt64(m["Capacity"]));
        Assert.Equal(wmiBytes / (1024.0 * 1024.0), ours, precision: 0);
    }

    [Fact]
    public void 標稱與實際速度_兩個實作一致()
    {
        var facts = Facts();
        var wmi = WmiModules();

        uint wmiSpeed = wmi.Select(m => U32(m, "Speed")).Where(s => s > 0).Max();
        uint wmiConfigured = wmi.Select(m => U32(m, "ConfiguredClockSpeed")).Where(s => s > 0).Max();

        Assert.True(facts.Num(FactId.DimmSpeedMts) == wmiSpeed,
            $"標稱速度：SMBIOS 解碼 {facts.Num(FactId.DimmSpeedMts)} vs WMI {wmiSpeed} MT/s——查 Type 17 +0x15");
        Assert.True(facts.Num(FactId.DimmConfiguredMts) == wmiConfigured,
            $"實際速度：SMBIOS 解碼 {facts.Num(FactId.DimmConfiguredMts)} vs WMI {wmiConfigured} MT/s——查 Type 17 +0x20（SMBIOS 2.7+）");
    }
}
