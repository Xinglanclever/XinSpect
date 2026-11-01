using System.IO;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 監控規劃的存檔（3.13）：把「監控規劃」頁的設計參數存成本機 JSON，
/// 下次開程式可以載回、匯出報告時看得到同一組數字。
/// </summary>
/// <remarks>
/// <para>
/// <b>存的是輸入，不是結果。</b>結果由 <see cref="SurveillancePlanning"/> 每次重算——
/// 公式改了以後，舊存檔不該帶著一份用舊公式算出來的 TB 數繼續流傳。
/// </para>
/// <para>
/// 檔案只在 <c>%LocalAppData%\XinSpect\surveillance-plan.json</c>，不上傳、不同步。
/// 寫入走 <see cref="AtomicWrite"/>（與設定檔同一套），兩個實例同時存不會互相搬半成品。
/// </para>
/// </remarks>
public static class SurveillancePlanStore
{
    /// <summary>測試用：改寫檔案位置；<c>null</c> 回預設。</summary>
    internal static string? PathOverride { get; set; }

    public static string FilePath => PathOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XinSpect", "surveillance-plan.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>最近一次載入或儲存的規劃；沒有存檔時為 <c>null</c>。</summary>
    public static SurveillancePlanDocument? Current { get; private set; }

    private static bool _loaded;

    /// <summary>從磁碟載入（只讀一次；之後用 <see cref="Current"/>）。讀不到回 null，不丟例外。</summary>
    public static SurveillancePlanDocument? Load()
    {
        if (_loaded) return Current;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return null;
            Current = JsonSerializer.Deserialize<SurveillancePlanDocument>(
                File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            // 壞檔不該讓整頁或整份報告產不出來：當成沒有存檔，使用者可以重新存一次。
            Current = null;
        }
        return Current;
    }

    /// <summary>寫入存檔並更新 <see cref="Current"/>。失敗會丟給呼叫端（畫面要說得出存不起來）。</summary>
    public static void Save(SurveillancePlanDocument doc)
    {
        string? dir = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        AtomicWrite.AllText(FilePath, JsonSerializer.Serialize(doc, JsonOptions));
        Current = doc;
        _loaded = true;
    }

    /// <summary>測試用：清掉記憶體狀態（不刪檔）。</summary>
    internal static void ResetMemory()
    {
        _loaded = false;
        Current = null;
    }
}

/// <summary>
/// 一份監控規劃的輸入。<b>全部是使用者宣告的設計參數</b>，沒有一個欄位是量測值。
/// 列舉型別的欄位（RAID／PoE／波長／AWG）存成整數索引，與畫面的下拉選單順序一致。
/// </summary>
public sealed record SurveillancePlanDocument(
    int Cameras, double BitrateMbps, double HoursPerDay, double RecordingDuty,
    // NVR
    int RetentionDays, int NvrDisks, double NvrDiskTb, int NvrRaidIndex, int NvrSpares, double NvrFsPercent,
    // 接入
    double ProtocolOverheadPercent, double NvrIngestMbps, double UplinkGbps,
    // PoE
    double PoeWatts, int PoePorts, double PoeBudgetWatts, int PoeStandardIndex, double PoeReservePercent,
    // 交換機
    int SwitchPorts, double SwitchSpeedGbps, int SwitchUplinkPorts, double SwitchUplinkGbps,
    double SwitchRatedBackplaneGbps, double SwitchRatedMpps,
    // 光纖
    double FiberKm, int FiberWavelengthIndex, int FiberConnectors, int FiberSplices,
    double FiberTxDbm, double FiberRxDbm,
    // 壓降
    double DropVoltage, double DropWatts, double DropMeters, int DropAwgIndex, int DropPairs, double DropMinVoltage,
    // 佈線
    int CablingRuns, double CablingAverageMeters, double CablingSlackPercent,
    double CablingBoxMeters, double CablingLoopMeters,
    // RAID
    int RaidDisks, double RaidDiskTb, int RaidLevelIndex, int RaidSpares, int RaidGroupSize, double RaidFsPercent,
    // 無線
    double WirelessPerApMbps, double WirelessRadiusMeters, double WirelessAreaSquareMeters, double WirelessOverlapPercent,
    // UPS 與機櫃
    double UpsLoadWatts, double UpsBatteryWh, double UpsEfficiencyPercent, double UpsDepthOfDischargePercent,
    int RackDeviceU, int RackReservedU, int RackTotalU,
    string SavedAtUtc)
{
    // 畫面下拉選單的索引 → 計算核心的列舉。集中在這裡，讀檔與存檔兩邊用同一套對照。
    public SurveillancePlanning.RaidLevel NvrRaid => RaidFrom(NvrRaidIndex);
    public SurveillancePlanning.RaidLevel RaidLevel => RaidFrom(RaidLevelIndex);
    public SurveillancePlanning.PoeStandard PoeStandard => PoeFrom(PoeStandardIndex);
    public SurveillancePlanning.FiberWavelength FiberWavelength => WavelengthFrom(FiberWavelengthIndex);
    public int DropAwg => AwgFrom(DropAwgIndex);

    internal static SurveillancePlanning.RaidLevel RaidFrom(int i) => i switch
    {
        0 => SurveillancePlanning.RaidLevel.Raid0,
        1 => SurveillancePlanning.RaidLevel.Raid1,
        2 => SurveillancePlanning.RaidLevel.Raid5,
        3 => SurveillancePlanning.RaidLevel.Raid6,
        4 => SurveillancePlanning.RaidLevel.Raid10,
        5 => SurveillancePlanning.RaidLevel.Raid50,
        6 => SurveillancePlanning.RaidLevel.Raid60,
        _ => SurveillancePlanning.RaidLevel.Raid5,
    };

    internal static SurveillancePlanning.PoeStandard PoeFrom(int i) => i switch
    {
        0 => SurveillancePlanning.PoeStandard.Af,
        1 => SurveillancePlanning.PoeStandard.At,
        2 => SurveillancePlanning.PoeStandard.BtType3,
        3 => SurveillancePlanning.PoeStandard.BtType4,
        _ => SurveillancePlanning.PoeStandard.At,
    };

    internal static SurveillancePlanning.FiberWavelength WavelengthFrom(int i) => i switch
    {
        0 => SurveillancePlanning.FiberWavelength.Nm850,
        1 => SurveillancePlanning.FiberWavelength.Nm1310,
        2 => SurveillancePlanning.FiberWavelength.Nm1550,
        _ => SurveillancePlanning.FiberWavelength.Nm1310,
    };

    internal static int AwgFrom(int i) => i switch
    {
        0 => 26, 1 => 24, 2 => 23, 3 => 22, 4 => 21, 5 => 20, 6 => 18, 7 => 16, _ => 24,
    };
}
