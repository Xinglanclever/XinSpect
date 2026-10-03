using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// WP25 時間同步互校（usermode）：回答「這台機器有哪些計時源、它們對不對得上」。
/// <para>
/// ① HPET 存在＝ACPI 表列裡有 HPET 表（硬體指紋，韌體說的）；② ACPI PM timer＝FADT 的
/// PM_TMR_BLK 位址（offset 76，u32 LE）；③ QPC vs 系統時鐘漂移＝**行為量測**：QPC 用什麼
/// 硬體計時器（TSC/HPET/PM timer）是 Windows 的決策，本工具不猜硬體映射，只量「單調鐘與
/// 牆鐘在 200 毫秒窗內差多少 ppm」——NTP 校正會讓這個值正常地跳動，不是故障。
/// </para>
/// </summary>
public static class TimeSyncFactsService
{
    private const string Category = "系統與軟體";
    private const string Source = "ACPI 表列（HPET/FADT）＋QPC 行為量測";

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IAcpiTableSource acpi,
        Func<(double QpcElapsedUs, double WallElapsedUs)?>? driftProbe = null)
    {
        byte[]? fadt = null;
        bool hpetPresent = false;
        if (acpi.Available)
        {
            foreach (var table in acpi.ReadAll())
            {
                if (AcpiTable.TryParseHeader(table, out var header))
                {
                    if (header.Signature == "HPET") hpetPresent = true;
                    if (header.Signature == "FADT") fadt = table;
                }
            }
        }

        var hpetFact = new HardwareFact("time.hpet", Category, "HPET 計時器",
            hpetPresent ? "存在（ACPI HPET 表）" : "", "",
            Source, FactTrustLevel.Reported, false, at, null,
            hpetPresent ? FactAvailability.Present : FactAvailability.NotSupported,
            hpetPresent ? null : "表列裡沒有 HPET 表：平台沒有（或韌體未提供）HPET");

        uint? pmBlock = fadt is null ? null : DecodePmTimerBlock(fadt);
        var pmFact = new HardwareFact("time.pm_timer", Category, "ACPI PM timer 區塊",
            pmBlock is { } block ? $"0x{block:X}（{block}）" : "", "",
            Source, FactTrustLevel.Reported, false, at,
            pmBlock is { } b ? (double)b : null,
            pmBlock is not null ? FactAvailability.Present : FactAvailability.ReadError,
            pmBlock is not null ? null
                : fadt is null ? "表列裡沒有 FADT" : "FADT 過短，PM_TMR_BLK（offset 76）解不出");

        var driftFact = DriftFact(at, (driftProbe ?? MeasureDriftWindow)());
        return [hpetFact, pmFact, driftFact];
    }

    private static HardwareFact DriftFact(DateTimeOffset at, (double QpcElapsedUs, double WallElapsedUs)? sample)
    {
        const string key = "time.drift.ppm", name = "系統時鐘 vs 單調鐘漂移";
        if (sample is not { } s)
            return new HardwareFact(key, Category, name, "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "漂移量測不可用——量不到就是不猜");
        double ppm = (s.WallElapsedUs - s.QpcElapsedUs) / s.QpcElapsedUs * 1_000_000.0;
        return new HardwareFact(key, Category, name,
            $"{ppm:+0.0;-0.0;0} ppm（200 毫秒窗）", "ppm", Source,
            FactTrustLevel.Derived, false, at, ppm);
    }

    /// <summary>生產量測（200 毫秒窗口）：QPC 為單調鐘、DateTime.UtcNow 為牆鐘；失敗回 null。</summary>
    private static (double QpcElapsedUs, double WallElapsedUs)? MeasureDriftWindow()
    {
        try
        {
            long startQpc = Stopwatch.GetTimestamp();
            DateTime startWall = DateTime.UtcNow;
            Thread.Sleep(200);
            long endQpc = Stopwatch.GetTimestamp();
            DateTime endWall = DateTime.UtcNow;
            double qpcUs = (endQpc - startQpc) / (double)Stopwatch.Frequency * 1_000_000.0;
            double wallUs = (endWall - startWall).TotalMicroseconds;
            return (qpcUs, wallUs);
        }
        catch { return null; }
    }

    /// <summary>FADT 的 PM_TMR_BLK（offset 76，u32 LE）。表過短回 null 不猜。</summary>
    public static uint? DecodePmTimerBlock(byte[] fadt)
    {
        if (fadt.Length < 80) return null;
        return fadt[76] | (uint)(fadt[77] << 8) | (uint)(fadt[78] << 16) | (uint)(fadt[79] << 24);
    }
}
